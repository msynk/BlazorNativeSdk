using System.Runtime.InteropServices;
using BlazorNativeSdk.Desktop.Interop;

namespace BlazorNativeSdk.Desktop.Internal;

/// <summary>The outcome of <see cref="ExclusiveFileLock.TryAcquire"/>.</summary>
internal enum FileLockOutcome
{
    /// <summary>The lock was acquired.</summary>
    Acquired,

    /// <summary>Another handle (in this or another process) holds the lock.</summary>
    Contended,

    /// <summary>The lock could not be evaluated (I/O error, permissions, file system without lock support).</summary>
    Failed,
}

/// <summary>
/// A per-user, crash-safe exclusive lock backed by a lock file.
/// </summary>
/// <remarks>
/// <para>
/// Windows: the file is opened with <see cref="FileShare.None"/>, a mandatory sharing mode enforced by the kernel.
/// Unix: the file is opened through <see cref="FileStream"/> (which already takes an advisory <c>flock(LOCK_EX)</c>), and the
/// lock is then taken explicitly with <c>flock(LOCK_EX | LOCK_NB)</c> on the same descriptor. The explicit call keeps the
/// lock effective when the runtime's emulation is disabled (<c>DOTNET_SYSTEM_IO_DISABLEFILELOCKING</c>) and turns file
/// systems that cannot lock into a hard failure instead of two primaries.
/// </para>
/// <para>
/// Both mechanisms are tied to the open handle: the OS releases the lock when the process exits or crashes, so there are
/// no abandoned locks to recover. The lock file is intentionally never deleted, because deleting a lock file that another
/// process is about to lock reintroduces the classic unlink race.
/// </para>
/// <para>
/// On Unix the lock file must be a regular file owned by the effective user: a symbolic link at the path is refused before
/// the file is opened (so a planted link cannot make the process create or lock a file elsewhere), and the opened file's
/// owner is verified with <c>fstat</c>. Anything else fails closed with <see cref="FileLockOutcome.Failed"/>.
/// </para>
/// </remarks>
internal sealed class ExclusiveFileLock : IDisposable
{
    private FileStream? _stream;

    private ExclusiveFileLock(FileStream stream, string path)
    {
        _stream = stream;
        Path = path;
    }

    /// <summary>The lock file path.</summary>
    public string Path { get; }

    /// <summary>Tries to acquire the lock without blocking.</summary>
    public static FileLockOutcome TryAcquire(string path, out ExclusiveFileLock? fileLock, out Exception? failure)
    {
        fileLock = null;
        failure = null;
        FileStream stream;
        try
        {
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                if (OperatingSystem.IsWindows())
                {
                    Directory.CreateDirectory(directory);
                }
                else
                {
                    Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }

            var options = new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = 0,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && CheckUnixPath(path) is { } unsafePath)
            {
                failure = unsafePath;
                return FileLockOutcome.Failed;
            }

            stream = new FileStream(path, options);
        }
        catch (IOException ex) when (IsContention(ex))
        {
            return FileLockOutcome.Contended;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            failure = ex;
            return FileLockOutcome.Failed;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            // The path was checked before opening; verify what was actually opened (and that no link was swapped in meanwhile).
            failure = CheckUnixHandle(stream) ?? CheckUnixPath(path);
            var outcome = failure is null ? LockUnix(stream, out failure) : FileLockOutcome.Failed;
            if (outcome != FileLockOutcome.Acquired)
            {
                stream.Dispose();
                return outcome;
            }
        }

        fileLock = new ExclusiveFileLock(stream, path);
        return FileLockOutcome.Acquired;
    }

    /// <summary>Releases the lock.</summary>
    public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();

    private static bool IsContention(IOException exception)
    {
        if (OperatingSystem.IsWindows())
        {
            // ERROR_SHARING_VIOLATION (32) or ERROR_LOCK_VIOLATION (33).
            var code = exception.HResult & 0xFFFF;
            return code is 32 or 33;
        }

        // The runtime reports flock() EWOULDBLOCK as an IOException whose HResult is the raw errno.
        return (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && exception.HResult == UnixInterop.ErrnoWouldBlock;
    }

    /// <summary>Refuses a lock path that is a symbolic link or an entry owned by another user. Nothing at the path is fine.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static IOException? CheckUnixPath(string path)
    {
        var errno = UnixInterop.LStat(path, out var status);
        if (errno == UnixInterop.ErrnoNoEntry)
        {
            return null;
        }

        if (errno != 0)
        {
            return new IOException($"The lock file '{path}' could not be inspected (errno {errno}); refusing to use it.");
        }

        if (status.IsSymbolicLink)
        {
            return new IOException($"The lock file '{path}' is a symbolic link; refusing to follow it.");
        }

        return status.IsRegularFile && status.Uid == UnixInterop.geteuid()
            ? null
            : new IOException($"The lock file '{path}' is not a regular file owned by the current user; refusing to use it.");
    }

    /// <summary>Verifies that an opened lock file is a regular file owned by the effective user.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static IOException? CheckUnixHandle(FileStream stream)
    {
        var errno = UnixInterop.FStat(stream.SafeFileHandle, out var status);
        if (errno != 0)
        {
            return new IOException($"The lock file '{stream.Name}' could not be inspected (errno {errno}); refusing to use it.");
        }

        return status.IsRegularFile && status.Uid == UnixInterop.geteuid()
            ? null
            : new IOException($"The lock file '{stream.Name}' is owned by another user; refusing to use it.");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static FileLockOutcome LockUnix(FileStream stream, out Exception? failure)
    {
        failure = null;
        var handle = stream.SafeFileHandle;
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            var fd = (int)handle.DangerousGetHandle();

            // Re-locking a descriptor that already holds the lock succeeds, so this is a no-op when the runtime locked it.
            if (UnixInterop.flock(fd, UnixInterop.LockExclusive | UnixInterop.LockNonBlocking) == 0)
            {
                return FileLockOutcome.Acquired;
            }

            var errno = Marshal.GetLastPInvokeError();
            if (errno == UnixInterop.ErrnoWouldBlock)
            {
                return FileLockOutcome.Contended;
            }

            failure = new IOException($"The file system does not support advisory locks on '{stream.Name}' (errno {errno}).");
            return FileLockOutcome.Failed;
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }
}
