using System.Runtime.Versioning;
using System.Text;
using BlazorNativeSdk.Desktop.Internal;
using BlazorNativeSdk.Desktop.Ipc;

namespace BlazorNativeSdk.Desktop.Protocols;

/// <summary>
/// Per-user URL scheme registration through a hidden desktop entry with <c>MimeType=x-scheme-handler/{scheme}</c> in
/// <c>$XDG_DATA_HOME/applications</c>, made the default handler with <c>xdg-mime default</c>.
/// </summary>
internal static class LinuxProtocolRegistrar
{
    private const string AppIdKey = "X-BlazorNativeSdk-AppId";
    private const string ExecutableKey = "X-BlazorNativeSdk-Executable";
    private static readonly CapabilityId s_capability = NativeCapabilities.ProtocolRegistration;
    private static readonly TimeSpan s_toolTimeout = TimeSpan.FromSeconds(15);

    public static NativeError? GetUnsupportedReason()
    {
        if (Environment.GetEnvironmentVariable("FLATPAK_ID") is { Length: > 0 } || Environment.GetEnvironmentVariable("SNAP") is { Length: > 0 })
        {
            return NativeError.Unsupported(s_capability,
                "Sandboxed (Flatpak/Snap) apps cannot register URL handlers at runtime; declare the MimeType in the package's desktop file.",
                NativeErrorCodes.MissingDeclaration);
        }

        return ProcessRunner.FindExecutable("xdg-mime") is null
            ? NativeError.Unavailable(s_capability, DesktopErrorCodes.ToolMissing, "xdg-mime is not installed (package xdg-utils).")
            : null;
    }

    public static async Task<NativeResult> RegisterAsync(string scheme, string executable, string template, string displayName, string appId, bool overwriteForeign, CancellationToken cancellationToken)
    {
        if (GetUnsupportedReason() is { } reason)
        {
            return reason;
        }

        var exec = BuildExec(executable, template);
        if (!exec.TryGetValue(out var execLine))
        {
            return exec.Error!;
        }

        var fileName = GetDesktopFileName(appId, scheme);
        var current = await QueryDefaultAsync(scheme, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(current) && current != fileName && !overwriteForeign)
        {
            return NativeError.Failed(s_capability, NativeErrorCodes.Conflict,
                $"The '{scheme}' scheme is handled by '{current}'. Set DesktopNativeOptions.OverwriteForeignProtocolRegistrations to replace it.");
        }

        var directory = GetApplicationsDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        var content = new StringBuilder()
            .Append("[Desktop Entry]\n")
            .Append("Type=Application\n")
            .Append("Version=1.0\n")
            .Append("Name=").Append(EscapeValue(displayName)).Append('\n')
            .Append("Exec=").Append(execLine).Append('\n')
            .Append("NoDisplay=true\n")
            .Append("Terminal=false\n")
            .Append("MimeType=x-scheme-handler/").Append(scheme).Append(";\n")
            .Append(AppIdKey).Append('=').Append(EscapeValue(appId)).Append('\n')
            .Append(ExecutableKey).Append('=').Append(EscapeValue(executable)).Append('\n')
            .ToString();

        // Atomic replace: a desktop database scan never sees a half-written entry.
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);

        var xdgMime = ProcessRunner.FindExecutable("xdg-mime")!;
        var outcome = await ProcessRunner.RunAsync(xdgMime, ["default", fileName, "x-scheme-handler/" + scheme], s_toolTimeout, cancellationToken).ConfigureAwait(false);
        if (!outcome.Succeeded)
        {
            File.Delete(path);
            return NativeError.Failed(s_capability, NativeErrorCodes.PlatformError, "xdg-mime default failed: " + outcome.Describe());
        }

        await UpdateDatabaseAsync(directory, cancellationToken).ConfigureAwait(false);
        return NativeResult.Ok;
    }

    public static async Task<NativeResult> UnregisterAsync(string scheme, string appId, CancellationToken cancellationToken)
    {
        var directory = GetApplicationsDirectory();
        var path = Path.Combine(directory, GetDesktopFileName(appId, scheme));
        if (!File.Exists(path))
        {
            return NativeResult.Ok;
        }

        var entries = await ReadEntriesAsync(path, cancellationToken).ConfigureAwait(false);
        if (!entries.TryGetValue(AppIdKey, out var owner) || owner != EscapeValue(appId))
        {
            return NativeError.Failed(s_capability, NativeErrorCodes.Conflict, $"'{path}' was not created by this application and was left unchanged.");
        }

        // xdg-mime has no "unset"; with the entry gone, desktops fall back to the next handler for the scheme.
        File.Delete(path);
        await UpdateDatabaseAsync(directory, cancellationToken).ConfigureAwait(false);
        return NativeResult.Ok;
    }

    public static async Task<bool> IsRegisteredAsync(string scheme, string executable, string appId, CancellationToken cancellationToken)
    {
        var fileName = GetDesktopFileName(appId, scheme);
        var path = Path.Combine(GetApplicationsDirectory(), fileName);
        if (!File.Exists(path))
        {
            return false;
        }

        var entries = await ReadEntriesAsync(path, cancellationToken).ConfigureAwait(false);
        return entries.TryGetValue(ExecutableKey, out var registered) &&
               registered == EscapeValue(executable) &&
               await QueryDefaultAsync(scheme, cancellationToken).ConfigureAwait(false) == fileName;
    }

    internal static string GetDesktopFileName(string appId, string scheme) =>
        IpcEndpoints.Sanitize(appId, 128) + "-" + scheme + ".desktop";

    /// <summary>Escapes a desktop-entry string value (backslash escapes for <c>\</c>, newline, tab, carriage return).</summary>
    internal static string EscapeValue(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal);

    /// <summary>
    /// Builds the <c>Exec</c> value: the executable and literal template arguments are quoted per the Desktop Entry
    /// specification (escaping <c>"</c>, <c>`</c>, <c>$</c> and <c>\</c>, doubling <c>%</c>), and the URL placeholder
    /// (<c>%1</c> in the Windows-style template) becomes the unquoted field code <c>%u</c>, as the specification requires.
    /// </summary>
    internal static NativeResult<string> BuildExec(string executable, string template)
    {
        var builder = new StringBuilder(QuoteArgument(executable));
        foreach (var token in Tokenize(template))
        {
            builder.Append(' ');
            if (token == "%1")
            {
                builder.Append("%u");
            }
            else if (token.Contains("%1", StringComparison.Ordinal))
            {
                return NativeError.Failed(s_capability, NativeErrorCodes.InvalidArgument,
                    "On Linux the URL placeholder must be a separate argument (\"%1\"); it cannot be embedded in another argument.");
            }
            else
            {
                builder.Append(QuoteArgument(token));
            }
        }

        // The Exec value is itself a desktop-entry string, so backslashes are escaped once more.
        return builder.ToString().Replace("\\", "\\\\", StringComparison.Ordinal);
    }

    private static string QuoteArgument(string argument)
    {
        var escaped = argument
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal)
            .Replace("%", "%%", StringComparison.Ordinal);
        return "\"" + escaped + "\"";
    }

    private static IEnumerable<string> Tokenize(string template)
    {
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        foreach (var c in template)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken)
                {
                    yield return current.ToString();
                    current.Clear();
                    hasToken = false;
                }
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }

        if (hasToken)
        {
            yield return current.ToString();
        }
    }

    private static async Task<string?> QueryDefaultAsync(string scheme, CancellationToken cancellationToken)
    {
        if (ProcessRunner.FindExecutable("xdg-mime") is not { } xdgMime)
        {
            return null;
        }

        var outcome = await ProcessRunner.RunAsync(xdgMime, ["query", "default", "x-scheme-handler/" + scheme], s_toolTimeout, cancellationToken).ConfigureAwait(false);
        return outcome.Succeeded ? outcome.Output.Trim() : null;
    }

    private static async Task UpdateDatabaseAsync(string directory, CancellationToken cancellationToken)
    {
        if (ProcessRunner.FindExecutable("update-desktop-database") is { } tool)
        {
            await ProcessRunner.RunAsync(tool, [directory], s_toolTimeout, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<Dictionary<string, string>> ReadEntriesAsync(string path, CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                entries.TryAdd(line[..separator].Trim(), line[(separator + 1)..].Trim());
            }
        }

        return entries;
    }

    private static string GetApplicationsDirectory()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg && Path.IsPathFullyQualified(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(dataHome, "applications");
    }
}
