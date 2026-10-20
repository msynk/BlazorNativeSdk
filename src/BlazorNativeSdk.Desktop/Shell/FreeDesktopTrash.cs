using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using BlazorNativeSdk.Desktop.Interop;

namespace BlazorNativeSdk.Desktop.Shell;

/// <summary>
/// An implementation of the FreeDesktop.org Trash specification 1.0, used on Linux when <c>gio trash</c> is unavailable.
/// </summary>
/// <remarks>
/// Items on the same mount as the home trash go to <c>$XDG_DATA_HOME/Trash</c>; items on other mounts go to
/// <c>$topdir/.Trash-$uid</c>. The <c>.trashinfo</c> file is created first with <see cref="FileMode.CreateNew"/>, which
/// atomically reserves the name in the trash before the item is renamed into <c>files/</c>, as the specification requires.
/// An existing <c>$topdir/.Trash-$uid</c> is only used when it is a real directory owned by the user with mode 0700 (as the
/// specification requires); otherwise, for example when another user created it first, the item goes to the home trash.
/// Nothing is ever written into a directory owned by someone else.
/// </remarks>
internal static class FreeDesktopTrash
{
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly CapabilityId s_capability = NativeCapabilities.DesktopFiles;

    [SupportedOSPlatform("linux")]
    public static NativeResult MoveToTrash(string fullPath, bool isDirectory, string? dataHomeOverride = null)
    {
        try
        {
            var dataHome = dataHomeOverride ?? GetDataHome();
            var homeTrash = Path.Combine(dataHome, "Trash");
            var itemMount = FindMountPoint(fullPath);
            var homeMount = FindMountPoint(dataHome);

            var trashDirectory = homeTrash;
            var recordedPath = fullPath;
            if (itemMount is not null && homeMount is not null && !string.Equals(itemMount, homeMount, StringComparison.Ordinal))
            {
                var topdirTrash = Path.Combine(itemMount, ".Trash-" + UnixInterop.geteuid().ToString(CultureInfo.InvariantCulture));
                if (TryPrepareTopdirTrash(topdirTrash))
                {
                    trashDirectory = topdirTrash;
                    recordedPath = Path.GetRelativePath(itemMount, fullPath);
                }
            }

            var filesDirectory = Path.Combine(trashDirectory, "files");
            var infoDirectory = Path.Combine(trashDirectory, "info");
            EnsurePrivateDirectory(trashDirectory);
            EnsurePrivateDirectory(filesDirectory);
            EnsurePrivateDirectory(infoDirectory);

            var (name, infoPath) = ReserveName(fullPath, recordedPath, filesDirectory, infoDirectory);
            try
            {
                var destination = Path.Combine(filesDirectory, name);
                if (isDirectory)
                {
                    Directory.Move(fullPath, destination);
                }
                else
                {
                    File.Move(fullPath, destination);
                }
            }
            catch
            {
                File.Delete(infoPath);
                throw;
            }

            return NativeResult.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return NativeError.Failed(s_capability, DesktopErrorCodes.TrashUnavailable, $"The item could not be moved to the trash: {ex.Message}", ex);
        }
    }

    internal static string BuildTrashInfo(string recordedPath, DateTime deletionDate) =>
        "[Trash Info]\n" +
        "Path=" + EncodePath(recordedPath) + "\n" +
        "DeletionDate=" + deletionDate.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "\n";

    // The specification requires the Path value to be URL-escaped (RFC 2396); '/' separators stay literal.
    internal static string EncodePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    [SupportedOSPlatform("linux")]
    private static (string Name, string InfoPath) ReserveName(string fullPath, string recordedPath, string filesDirectory, string infoDirectory)
    {
        var fileName = Path.GetFileName(fullPath);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var content = Encoding.UTF8.GetBytes(BuildTrashInfo(recordedPath, DateTime.Now));
        for (var attempt = 1; attempt < 10_000; attempt++)
        {
            var name = attempt == 1 ? fileName : $"{stem}.{attempt}{extension}";
            var infoPath = Path.Combine(infoDirectory, name + ".trashinfo");
            if (Path.Exists(Path.Combine(filesDirectory, name)))
            {
                continue;
            }

            try
            {
                using var stream = new FileStream(infoPath, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                });
                stream.Write(content);
                return (name, infoPath);
            }
            catch (IOException) when (File.Exists(infoPath))
            {
                // Taken by a concurrent trash operation; try the next name.
            }
        }

        throw new IOException("No free name is available in the trash.");
    }

    /// <summary>
    /// Returns whether <paramref name="path"/> (a <c>$topdir/.Trash-$uid</c> directory) can hold the user's trashed items:
    /// it is created when missing, and must be a real directory owned by the effective user with mode 0700.
    /// </summary>
    [SupportedOSPlatform("linux")]
    internal static bool TryPrepareTopdirTrash(string path)
    {
        var errno = UnixInterop.LStat(path, out var status);
        if (errno == UnixInterop.ErrnoNoEntry)
        {
            try
            {
                Directory.CreateDirectory(path, PrivateDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A read-only or foreign mount: use the home trash.
                return false;
            }

            errno = UnixInterop.LStat(path, out status);
            if (errno == 0 && status.IsDirectory && status.Uid == UnixInterop.geteuid() && !UnixInterop.HasPrivatePermissions(status))
            {
                // Our new directory, narrowed by an unusual umask.
                File.SetUnixFileMode(path, PrivateDirectory);
                errno = UnixInterop.LStat(path, out status);
            }
        }

        return errno == 0 && UnixInterop.IsPrivateDirectory(status);
    }

    [SupportedOSPlatform("linux")]
    private static void EnsurePrivateDirectory(string path)
    {
        if (UnixInterop.LStat(path, out _) == UnixInterop.ErrnoNoEntry)
        {
            Directory.CreateDirectory(path, PrivateDirectory);
        }

        var errno = UnixInterop.LStat(path, out var status);
        if (errno != 0)
        {
            throw new IOException($"The trash directory '{path}' could not be inspected (errno {errno}).");
        }

        if (status.IsSymbolicLink || !status.IsDirectory)
        {
            throw new IOException($"The trash directory '{path}' is not a directory (or is a symbolic link).");
        }

        if (status.Uid != UnixInterop.geteuid())
        {
            throw new IOException($"The trash directory '{path}' belongs to another user.");
        }
    }

    private static string GetDataHome() =>
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg && Path.IsPathFullyQualified(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

    private static string? FindMountPoint(string path)
    {
        try
        {
            string? best = null;
            foreach (var drive in DriveInfo.GetDrives())
            {
                var root = drive.RootDirectory.FullName;
                var isPrefix = root == "/" ||
                               path.Equals(root, StringComparison.Ordinal) ||
                               path.StartsWith(Path.TrimEndingDirectorySeparator(root) + "/", StringComparison.Ordinal);
                if (isPrefix && (best is null || root.Length > best.Length))
                {
                    best = root;
                }
            }

            return best is null ? null : Path.TrimEndingDirectorySeparator(best) is { Length: > 0 } trimmed ? trimmed : "/";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
