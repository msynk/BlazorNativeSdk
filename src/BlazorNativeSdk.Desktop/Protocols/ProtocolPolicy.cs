using System.Diagnostics.CodeAnalysis;

namespace BlazorNativeSdk.Desktop.Protocols;

/// <summary>Validation for custom URL scheme registration.</summary>
internal static class ProtocolPolicy
{
    internal const int MaxSchemeLength = 64;

    // Schemes owned by browsers, the OS or other well-known handlers. Registering them would hijack system behavior.
    private static readonly HashSet<string> s_reserved = new(
    [
        "about", "blob", "chrome", "chrome-extension", "cid", "data", "edge", "file", "ftp", "ftps", "help", "hcp", "http", "https",
        "its", "jar", "javascript", "ldap", "ldaps", "mailto", "mid", "mk", "moz-extension", "news", "nntp", "res", "resource",
        "search", "search-ms", "sftp", "shell", "smb", "telnet", "urn", "view-source", "vbscript", "ws", "wss",
    ], StringComparer.OrdinalIgnoreCase);

    private static readonly string[] s_reservedPrefixes = ["ms-", "microsoft", "windows", "x-scheme-handler"];

    /// <summary>Validates a scheme (RFC 3986: <c>ALPHA *( ALPHA / DIGIT / "+" / "-" / "." )</c>) and returns it lowercased.</summary>
    public static bool TryValidateScheme(string? scheme, [NotNullWhen(true)] out string? normalized, [NotNullWhen(false)] out NativeError? error)
    {
        error = ValidateSchemeCore(scheme, out normalized);
        return error is null;
    }

    private static NativeError? ValidateSchemeCore(string? scheme, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrEmpty(scheme) || scheme.Length > MaxSchemeLength || !IsSchemeSyntax(scheme))
        {
            return NativeError.Failed(NativeCapabilities.ProtocolRegistration, NativeErrorCodes.InvalidArgument,
                $"'{scheme}' is not a valid URI scheme: use a letter followed by letters, digits, '+', '-' or '.' (at most {MaxSchemeLength} characters).");
        }

        var lower = scheme.ToLowerInvariant();
        if (s_reserved.Contains(lower) || s_reservedPrefixes.Any(p => lower.StartsWith(p, StringComparison.Ordinal)))
        {
            return NativeError.Failed(NativeCapabilities.ProtocolRegistration, NativeErrorCodes.InvalidArgument,
                $"The '{lower}' scheme is reserved for the browser or operating system and cannot be registered.");
        }

        normalized = lower;
        return null;
    }

    /// <summary>Returns whether <paramref name="scheme"/> matches the RFC 3986 scheme grammar.</summary>
    public static bool IsSchemeSyntax(string scheme) =>
        scheme.Length > 0 &&
        char.IsAsciiLetter(scheme[0]) &&
        scheme.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.');

    /// <summary>
    /// Validates the Windows argument template. Every <c>%1</c> must be enclosed in double quotes: an unquoted URL
    /// containing spaces would be split into extra arguments (for example <c>myapp://x --some-flag</c>), the classic
    /// protocol-handler argument-injection bug.
    /// </summary>
    public static bool TryValidateTemplate(string? template, [NotNullWhen(true)] out string? normalized, [NotNullWhen(false)] out NativeError? error)
    {
        error = ValidateTemplateCore(template, out normalized);
        return error is null;
    }

    private static NativeError? ValidateTemplateCore(string? template, out string? normalized)
    {
        normalized = null;
        var value = string.IsNullOrWhiteSpace(template) ? "\"%1\"" : template.Trim();
        if (value.Any(char.IsControl))
        {
            return Invalid("The argument template contains control characters.");
        }

        var index = value.IndexOf("%1", StringComparison.Ordinal);
        if (index < 0)
        {
            return Invalid("The argument template must contain %1 (the URL).");
        }

        for (; index >= 0; index = value.IndexOf("%1", index + 2, StringComparison.Ordinal))
        {
            var quoted = index > 0 && value[index - 1] == '"' && index + 2 < value.Length && value[index + 2] == '"';
            if (!quoted)
            {
                return Invalid("Every %1 in the argument template must be enclosed in double quotes (\"%1\") so a URL cannot inject extra arguments.");
            }
        }

        normalized = value;
        return null;

        static NativeError Invalid(string message) =>
            NativeError.Failed(NativeCapabilities.ProtocolRegistration, NativeErrorCodes.InvalidArgument, message);
    }

    /// <summary>Resolves and validates the handler executable.</summary>
    public static bool TryResolveExecutable(string? explicitPath, [NotNullWhen(true)] out string? executable, [NotNullWhen(false)] out NativeError? error)
    {
        error = ResolveExecutableCore(explicitPath, out executable);
        return error is null;
    }

    private static NativeError? ResolveExecutableCore(string? explicitPath, out string? executable)
    {
        executable = null;
        var candidate = explicitPath;
        if (string.IsNullOrWhiteSpace(candidate) && OperatingSystem.IsLinux() &&
            Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage && File.Exists(appImage))
        {
            // Inside an AppImage the process runs from a temporary mount; the AppImage file itself is the stable launcher.
            candidate = appImage;
        }

        candidate ??= Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate))
        {
            return NativeError.Failed(NativeCapabilities.ProtocolRegistration, NativeErrorCodes.InvalidArgument,
                "The handler executable must be a fully qualified path (ProtocolRegistrationOptions.ExecutablePath).");
        }

        if (explicitPath is null && Path.GetFileNameWithoutExtension(candidate).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return NativeError.Failed(NativeCapabilities.ProtocolRegistration, NativeErrorCodes.InvalidArgument,
                "The process runs under the 'dotnet' host, which cannot be registered as a URL handler. Publish the app with an " +
                "apphost or set ProtocolRegistrationOptions.ExecutablePath.");
        }

        if (candidate.Contains('"', StringComparison.Ordinal) || candidate.Any(char.IsControl))
        {
            return NativeError.Failed(NativeCapabilities.ProtocolRegistration, NativeErrorCodes.InvalidArgument, "The executable path contains invalid characters.");
        }

        if (!File.Exists(candidate))
        {
            return NativeError.Failed(NativeCapabilities.ProtocolRegistration, NativeErrorCodes.NotFound, $"The executable '{candidate}' does not exist.");
        }

        executable = Path.GetFullPath(candidate);
        return null;
    }

    /// <summary>Extracts the executable from a command line such as <c>"C:\app.exe" "%1"</c>.</summary>
    public static string? GetCommandExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var text = command.TrimStart();
        string token;
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end < 0)
            {
                return null;
            }

            token = text[1..end];
        }
        else
        {
            var end = text.IndexOf(' ', StringComparison.Ordinal);
            token = end < 0 ? text : text[..end];
        }

        try
        {
            return Path.IsPathFullyQualified(token) ? Path.GetFullPath(token) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
