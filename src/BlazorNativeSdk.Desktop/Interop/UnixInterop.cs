using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BlazorNativeSdk.Desktop.Interop;

/// <summary>libc entry points and socket options used on Linux and macOS.</summary>
/// <remarks>
/// The runtime maps the library name <c>libc</c> to the real C library on every Unix platform (<c>libc.so.6</c> on glibc,
/// <c>/usr/lib/libc.dylib</c> on macOS), so these imports need no per-distribution names.
/// </remarks>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal static partial class UnixInterop
{
    internal const int LockExclusive = 2;
    internal const int LockNonBlocking = 4;
    internal const int LockUnlock = 8;

    private const int LinuxSolSocket = 1;
    private const int LinuxSoPeerCred = 17;
    private const int MacSolLocal = 0;
    private const int MacLocalPeerCred = 0x001;
    private const int MacLocalPeerPid = 0x002;

    /// <summary>The errno value for "no such file or directory" (the same on Linux and macOS).</summary>
    internal const int ErrnoNoEntry = 2;

    /// <summary>Returned by <see cref="LStat"/> and <see cref="FStat"/> when the C library provides no usable stat function.</summary>
    internal const int StatUnavailable = -1;

    private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode AccessBits = PrivateDirectoryMode |
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    // statx(2) (Linux 4.11, glibc 2.28): struct statx has the same layout on every architecture.
    private const int LinuxAtFdCwd = -100;
    private const int LinuxAtSymlinkNoFollow = 0x100;
    private const int LinuxAtEmptyPath = 0x1000;
    private const uint LinuxStatxTypeModeUid = 0x1 | 0x2 | 0x8;
    private const int LinuxStatxSize = 256;
    private const int LinuxStatxUidOffset = 20;
    private const int LinuxStatxModeOffset = 28;

    // macOS struct stat with 64-bit inodes (the only layout on arm64; the "$INODE64" symbols on x64): st_dev int32 @0,
    // st_mode uint16 @4, st_nlink uint16 @6, st_ino uint64 @8, st_uid uint32 @16; 144 bytes (the buffer holds 256).
    private const int MacStatModeOffset = 4;
    private const int MacStatUidOffset = 16;

    [LibraryImport("libc")]
    internal static partial uint geteuid();

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int flock(int fd, int operation);

    /// <summary>The errno value for "operation would block".</summary>
    internal static int ErrnoWouldBlock => OperatingSystem.IsMacOS() ? 35 : 11;

    /// <summary>
    /// Reads the type, permissions and owner of <paramref name="path"/> without following a final symbolic link (<c>lstat</c>).
    /// </summary>
    /// <returns>0 on success, the <c>errno</c> value on failure (<see cref="ErrnoNoEntry"/> when nothing exists at the path),
    /// or <see cref="StatUnavailable"/> when the C library has no usable stat function.</returns>
    internal static unsafe int LStat(string path, out UnixFileStatus status)
    {
        ArgumentNullException.ThrowIfNull(path);
        status = default;
        var buffer = stackalloc byte[LinuxStatxSize];
        try
        {
            int result;
            if (OperatingSystem.IsLinux())
            {
                result = LinuxStatx(LinuxAtFdCwd, path, LinuxAtSymlinkNoFollow, LinuxStatxTypeModeUid, buffer);
            }
            else if (OperatingSystem.IsMacOS())
            {
                result = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? MacLStatX64(path, buffer) : MacLStat(path, buffer);
            }
            else
            {
                return StatUnavailable;
            }

            return result == 0 ? Read(buffer, out status) : Marshal.GetLastPInvokeError();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return StatUnavailable;
        }
    }

    /// <summary>Reads the type, permissions and owner of an open file (<c>fstat</c>).</summary>
    /// <returns>0 on success, the <c>errno</c> value on failure, or <see cref="StatUnavailable"/>.</returns>
    internal static unsafe int FStat(SafeHandle handle, out UnixFileStatus status)
    {
        ArgumentNullException.ThrowIfNull(handle);
        status = default;
        var buffer = stackalloc byte[LinuxStatxSize];
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            var fd = (int)handle.DangerousGetHandle();
            int result;
            if (OperatingSystem.IsLinux())
            {
                result = LinuxStatx(fd, string.Empty, LinuxAtEmptyPath, LinuxStatxTypeModeUid, buffer);
            }
            else if (OperatingSystem.IsMacOS())
            {
                result = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? MacFStatX64(fd, buffer) : MacFStat(fd, buffer);
            }
            else
            {
                return StatUnavailable;
            }

            return result == 0 ? Read(buffer, out status) : Marshal.GetLastPInvokeError();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return StatUnavailable;
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Returns whether <paramref name="status"/> describes a real directory (not a symbolic link) owned by the effective user
    /// with mode 0700, the only kind of directory the SDK keeps sockets, locks or trashed items in.
    /// </summary>
    internal static bool IsPrivateDirectory(in UnixFileStatus status) =>
        status.IsDirectory && status.Uid == geteuid() && HasPrivatePermissions(status);

    /// <summary>
    /// Returns whether only the owner has access (rwx bits 0700). Set-gid and sticky bits, which a new directory can inherit
    /// from its parent, grant nobody access and are ignored.
    /// </summary>
    internal static bool HasPrivatePermissions(in UnixFileStatus status) =>
        (status.Permissions & AccessBits) == PrivateDirectoryMode;

    private static unsafe int Read(byte* buffer, out UnixFileStatus status)
    {
        status = OperatingSystem.IsLinux()
            ? new UnixFileStatus(*(uint*)(buffer + LinuxStatxUidOffset), *(ushort*)(buffer + LinuxStatxModeOffset))
            : new UnixFileStatus(*(uint*)(buffer + MacStatUidOffset), *(ushort*)(buffer + MacStatModeOffset));
        return 0;
    }

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int LinuxStatx(int dirfd, string path, int flags, uint mask, byte* buffer);

    [LibraryImport("libc", EntryPoint = "lstat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int MacLStat(string path, byte* buffer);

    [LibraryImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int MacLStatX64(string path, byte* buffer);

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static unsafe partial int MacFStat(int fd, byte* buffer);

    [LibraryImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static unsafe partial int MacFStatX64(int fd, byte* buffer);

    /// <summary>
    /// Reads the credentials of the process on the other end of a connected Unix domain socket
    /// (<c>SO_PEERCRED</c> on Linux, <c>LOCAL_PEERCRED</c>/<c>LOCAL_PEERPID</c> on macOS). The kernel records them at
    /// <c>connect()</c>/<c>listen()</c> time, so the peer cannot forge them.
    /// </summary>
    internal static bool TryGetPeerCredentials(Socket socket, out uint uid, out int? pid)
    {
        uid = 0;
        pid = null;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                // struct ucred { pid_t pid; uid_t uid; gid_t gid; }
                Span<byte> ucred = stackalloc byte[12];
                var length = socket.GetRawSocketOption(LinuxSolSocket, LinuxSoPeerCred, ucred);
                if (length < 8)
                {
                    return false;
                }

                pid = MemoryMarshal.Read<int>(ucred);
                uid = MemoryMarshal.Read<uint>(ucred[4..]);
                return true;
            }

            if (OperatingSystem.IsMacOS())
            {
                // struct xucred { u_int cr_version; uid_t cr_uid; short cr_ngroups; gid_t cr_groups[16]; }
                Span<byte> xucred = stackalloc byte[76];
                var length = socket.GetRawSocketOption(MacSolLocal, MacLocalPeerCred, xucred);
                if (length < 8)
                {
                    return false;
                }

                uid = MemoryMarshal.Read<uint>(xucred[4..]);
                Span<byte> pidBuffer = stackalloc byte[4];
                if (socket.GetRawSocketOption(MacSolLocal, MacLocalPeerPid, pidBuffer) == 4)
                {
                    pid = MemoryMarshal.Read<int>(pidBuffer);
                }

                return true;
            }
        }
        catch (SocketException)
        {
        }

        return false;
    }

    /// <summary>Returns the executable path of a process when the OS exposes it, otherwise <see langword="null"/>.</summary>
    internal static string? TryGetProcessPath(int pid)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                return new FileInfo($"/proc/{pid}/exe").LinkTarget;
            }

            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>The type, permission bits and owner of a file system entry, as reported by <c>lstat</c>/<c>fstat</c>.</summary>
/// <param name="Uid">The owner's user id.</param>
/// <param name="Mode">The raw <c>st_mode</c>: file type bits (<c>S_IFMT</c>) and permission bits.</param>
internal readonly record struct UnixFileStatus(uint Uid, uint Mode)
{
    private const uint TypeMask = 0xF000;

    /// <summary>Whether the entry is a directory.</summary>
    public bool IsDirectory => (Mode & TypeMask) == 0x4000;

    /// <summary>Whether the entry is a regular file.</summary>
    public bool IsRegularFile => (Mode & TypeMask) == 0x8000;

    /// <summary>Whether the entry is a symbolic link (only reported by <c>lstat</c>).</summary>
    public bool IsSymbolicLink => (Mode & TypeMask) == 0xA000;

    /// <summary>The permission bits, including set-uid, set-gid and sticky.</summary>
    public UnixFileMode Permissions => (UnixFileMode)(Mode & 0xFFF);
}
