namespace BlazorNativeSdk.Desktop.Internal;

/// <summary>Classifies untrusted paths before they reach the file system or the shell.</summary>
internal static class PathSafety
{
    /// <summary>
    /// Returns whether <paramref name="path"/> is, on Windows, a UNC path (<c>\\server\share</c>, <c>//server/share</c>), a Win32
    /// device or extended-length path (<c>\\.\</c>, <c>\\?\</c>, including <c>\\?\UNC\</c>) or an NT object path (<c>\??\</c>).
    /// </summary>
    /// <remarks>
    /// Merely probing a UNC path (<c>File.Exists</c>, <c>GetFileAttributes</c>) makes Windows connect to the server and
    /// authenticate with the user's credentials (NTLM), so untrusted input must never reach a probe or a shell call with such
    /// a path. Device paths bypass Win32 path normalization and address devices rather than files. Reserved device names
    /// (<c>NUL</c>, <c>COM1</c>) are caught as well, because <see cref="Path.GetFullPath(string)"/> maps them to <c>\\.\</c>
    /// paths; check the full path as well as the input. Always <see langword="false"/> on other operating systems, where a
    /// leading <c>//</c> is an ordinary local path and network file systems are mounted by the administrator.
    /// </remarks>
    public static bool IsRemoteOrDevicePath(string? path) => OperatingSystem.IsWindows() && IsRemoteOrDeviceWindowsPath(path);

    /// <summary>The Windows rules of <see cref="IsRemoteOrDevicePath"/>, testable on every OS.</summary>
    internal static bool IsRemoteOrDeviceWindowsPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        // Win32 ignores leading spaces in some APIs; never let them hide a prefix.
        var span = path.AsSpan().TrimStart();
        if (span.Length >= 2 && IsSeparator(span[0]) && IsSeparator(span[1]))
        {
            return true;
        }

        return span.Length >= 4 && IsSeparator(span[0]) && span[1] == '?' && span[2] == '?' && IsSeparator(span[3]);
    }

    /// <summary>
    /// Removes the trailing dots and spaces that Win32 strips from the last path segment (<c>run.exe. .</c> opens
    /// <c>run.exe</c>), so extension checks see the name Windows actually uses.
    /// </summary>
    internal static string TrimWin32TrailingDotsAndSpaces(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var end = path.Length;
        while (end > 0 && path[end - 1] is '.' or ' ')
        {
            end--;
        }

        // Keep "." and ".." segments (and anything ending in a separator) intact.
        if (end == 0 || IsSeparator(path[end - 1]))
        {
            return path;
        }

        return end == path.Length ? path : path[..end];
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';
}
