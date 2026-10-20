using System.Diagnostics.CodeAnalysis;
using BlazorNativeSdk.Desktop.Internal;

namespace BlazorNativeSdk.Desktop.Shell;

/// <summary>Input validation for shell operations: paths, executable detection and the URI scheme allow-list.</summary>
internal static class ShellPolicy
{
    /// <summary>Schemes <see cref="IDesktopFileOperations.OpenUriAsync"/> always allows.</summary>
    internal static readonly IReadOnlySet<string> DefaultUriSchemes =
        new HashSet<string>(["http", "https", "mailto"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Schemes that are rejected even when configured: they run code or address local files.</summary>
    internal static readonly IReadOnlySet<string> ForbiddenUriSchemes =
        new HashSet<string>(["file", "javascript", "vbscript", "data"], StringComparer.OrdinalIgnoreCase);

    private const int MaxUriLength = 32 * 1024;

    // Extensions that execute code when "opened" with the default handler.
    private static readonly HashSet<string> s_windowsExecutableExtensions = new(
    [
        ".exe", ".com", ".bat", ".cmd", ".scr", ".pif", ".cpl", ".msi", ".msp", ".mst", ".msc", ".lnk", ".url", ".website",
        ".ps1", ".psm1", ".psd1", ".ps1xml", ".vb", ".vbs", ".vbe", ".js", ".jse", ".ws", ".wsf", ".wsh", ".wsc", ".sct", ".hta",
        ".jar", ".reg", ".inf", ".scf", ".chm", ".hlp", ".application", ".appref-ms", ".appx", ".appxbundle", ".msix", ".msixbundle",
        ".settingcontent-ms", ".library-ms", ".search-ms", ".searchconnector-ms", ".gadget", ".diagcab", ".xbap", ".py", ".pyw", ".pl",
    ], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> s_unixExecutableExtensions = new(
    [
        ".app", ".command", ".tool", ".sh", ".bash", ".zsh", ".csh", ".ksh", ".run", ".bin", ".appimage", ".desktop", ".jar",
        ".py", ".pl", ".rb", ".terminal", ".workflow", ".action", ".pkg", ".mpkg", ".scpt", ".scptd", ".applescript", ".deb", ".rpm",
    ], StringComparer.OrdinalIgnoreCase);

    /// <summary>Validates a path and resolves it to a full path that exists.</summary>
    public static bool TryValidateExistingPath(CapabilityId capability, string? path, [NotNullWhen(true)] out string? fullPath, out bool isDirectory, [NotNullWhen(false)] out NativeError? error)
    {
        error = ValidateExistingPathCore(capability, path, out fullPath, out isDirectory);
        return error is null;
    }

    private static NativeError? ValidateExistingPathCore(CapabilityId capability, string? path, out string? fullPath, out bool isDirectory)
    {
        fullPath = null;
        isDirectory = false;
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0', StringComparison.Ordinal))
        {
            return NativeError.Failed(capability, NativeErrorCodes.InvalidArgument, "A non-empty path is required.");
        }

        if (OperatingSystem.IsWindows() && path.Contains('"', StringComparison.Ordinal))
        {
            return NativeError.Failed(capability, NativeErrorCodes.InvalidArgument, "The path contains invalid characters.");
        }

        // Checked before anything touches the file system: probing a UNC path sends the user's credentials to the server.
        if (PathSafety.IsRemoteOrDevicePath(path))
        {
            return RemoteOrDevicePath(capability);
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return NativeError.Failed(capability, NativeErrorCodes.InvalidArgument, $"The path is not valid: {ex.Message}");
        }

        if (OperatingSystem.IsWindows())
        {
            // A relative path can resolve onto a UNC working directory, and reserved names (NUL, COM1) become \\.\ devices.
            if (PathSafety.IsRemoteOrDevicePath(full))
            {
                return RemoteOrDevicePath(capability);
            }

            // Past the drive ("C:"), a colon only appears in alternate data stream syntax ("run.exe::$DATA").
            if (full.IndexOf(':', 2) >= 0)
            {
                return NativeError.Failed(capability, NativeErrorCodes.InvalidArgument, "Alternate data streams are not supported.");
            }

            // Win32 strips trailing dots and spaces ("run.exe. ." opens "run.exe"); use the name Windows uses.
            full = PathSafety.TrimWin32TrailingDotsAndSpaces(full);
        }

        if (Directory.Exists(full))
        {
            isDirectory = true;
        }
        else if (!File.Exists(full) && !IsDanglingLink(full))
        {
            return NativeError.Failed(capability, NativeErrorCodes.NotFound, $"'{full}' does not exist.");
        }

        // "C:\dir\" → "C:\dir" (roots keep their separator), so shell APIs see the item rather than its contents.
        fullPath = Path.TrimEndingDirectorySeparator(full);
        return null;
    }

    /// <summary>Returns whether opening <paramref name="fullPath"/> with its default handler would run code.</summary>
    public static bool IsExecutableLike(string fullPath, bool isDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            if (isDirectory)
            {
                return false;
            }

            return IsWindowsExecutableName(fullPath);
        }

        var extension = Path.GetExtension(fullPath);
        if (isDirectory)
        {
            // macOS application and installer bundles are directories.
            return extension.Equals(".app", StringComparison.OrdinalIgnoreCase) || extension.Equals(".pkg", StringComparison.OrdinalIgnoreCase);
        }

        if (s_unixExecutableExtensions.Contains(extension))
        {
            return true;
        }

        try
        {
            const UnixFileMode Execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(fullPath) & Execute) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Returns whether Windows would run <paramref name="fileName"/> as code when it is opened: its extension, after the trailing
    /// dots and spaces that Win32 strips (<c>run.exe. .</c> is <c>run.exe</c>), is a known executable type or listed in
    /// <c>PATHEXT</c>.
    /// </summary>
    internal static bool IsWindowsExecutableName(string fileName)
    {
        var extension = Path.GetExtension(PathSafety.TrimWin32TrailingDotsAndSpaces(fileName));
        if (extension.Length == 0)
        {
            return false;
        }

        if (s_windowsExecutableExtensions.Contains(extension))
        {
            return true;
        }

        var pathExt = Environment.GetEnvironmentVariable("PATHEXT")?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        return pathExt.Any(entry => string.Equals(PathSafety.TrimWin32TrailingDotsAndSpaces(entry), extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Validates a URI for <see cref="IDesktopFileOperations.OpenUriAsync"/> and returns the string to launch.</summary>
    public static bool TryValidateUri(CapabilityId capability, Uri uri, IEnumerable<string> allowedSchemes, [NotNullWhen(true)] out string? launchText, [NotNullWhen(false)] out NativeError? error)
    {
        error = ValidateUriCore(capability, uri, allowedSchemes, out launchText);
        return error is null;
    }

    private static NativeError? ValidateUriCore(CapabilityId capability, Uri uri, IEnumerable<string> allowedSchemes, out string? launchText)
    {
        launchText = null;
        if (!uri.IsAbsoluteUri)
        {
            return NativeError.Failed(capability, NativeErrorCodes.InvalidArgument, "Only absolute URIs can be opened.");
        }

        var scheme = uri.Scheme;
        if (ForbiddenUriSchemes.Contains(scheme))
        {
            return NativeError.Failed(capability, NativeErrorCodes.InvalidArgument,
                $"The '{scheme}' scheme is never opened, because it runs code or addresses local files. Use OpenPathAsync for local files.");
        }

        if (!DefaultUriSchemes.Contains(scheme) && !allowedSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase))
        {
            return NativeError.Failed(capability, NativeErrorCodes.InvalidArgument,
                $"The '{scheme}' scheme is not allowed. Only http, https and mailto are opened by default, so untrusted input cannot " +
                $"launch arbitrary protocol handlers; add '{scheme}' to DesktopNativeOptions.AllowedUriSchemes to permit it.");
        }

        if (scheme is "http" or "https" && string.IsNullOrEmpty(uri.Host))
        {
            return NativeError.Failed(capability, NativeErrorCodes.InvalidArgument, "HTTP(S) URIs must have a host.");
        }

        // AbsoluteUri is the escaped form: spaces, quotes and control characters are percent-encoded.
        var text = uri.AbsoluteUri;
        if (text.Length > MaxUriLength || text.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            return NativeError.Failed(capability, NativeErrorCodes.InvalidArgument, "The URI is too long or contains invalid characters.");
        }

        launchText = text;
        return null;
    }

    /// <summary>Builds a percent-encoded <c>file://</c> URI (only unreserved characters stay literal).</summary>
    public static string ToFileUri(string fullPath) =>
        "file://" + string.Join('/', fullPath.Split('/').Select(Uri.EscapeDataString));

    private static NativeError RemoteOrDevicePath(CapabilityId capability) =>
        NativeError.Failed(capability, NativeErrorCodes.InvalidArgument,
            "Network (UNC) and device paths are not supported; use a local path. Accessing them would send the user's credentials to the server.");

    private static bool IsDanglingLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
