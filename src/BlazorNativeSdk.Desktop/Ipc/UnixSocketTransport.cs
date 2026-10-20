using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using BlazorNativeSdk.Desktop.Interop;
using BlazorNativeSdk.Desktop.Internal;

namespace BlazorNativeSdk.Desktop.Ipc;

/// <summary>Unix domain socket transport for Linux and macOS.</summary>
/// <remarks>
/// <para>
/// Sockets are used directly rather than through <c>NamedPipeServerStream</c>: its Unix emulation places sockets
/// in the shared temp directory and, unless <c>FirstPipeInstance</c> is set, unlinks whatever is at the path before
/// binding, which would let a second server silently steal a live channel. Here:
/// </para>
/// <list type="bullet">
/// <item>Per-user sockets live in a private <c>bns-{user}</c> directory (mode 0700) under <c>$XDG_RUNTIME_DIR</c>,
/// <c>$TMPDIR</c> or <c>/tmp</c>: the first one that keeps the path within the 104-byte <c>sun_path</c> limit and whose
/// directory does not exist yet or is a real directory owned by the effective user with mode 0700 (checked with
/// <c>lstat</c>). A directory planted by another user, or a symbolic link, is skipped in favor of the next root.</item>
/// <item>Owning a channel means holding an exclusive lock on <c>{socket}.lock</c> for the server's lifetime. The lock
/// makes claiming atomic (<see cref="NativeErrorCodes.Conflict"/> for a second server), and because the OS releases it
/// when a process dies, a socket file left behind by a crashed server is known to be stale and is replaced safely.</item>
/// <item>Both ends verify the peer's effective uid from kernel credentials (<c>SO_PEERCRED</c> / <c>LOCAL_PEERCRED</c>)
/// for per-user channels, independently of the file permissions.</item>
/// </list>
/// </remarks>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal sealed class UnixSocketTransport : IIpcListener
{
    private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private readonly Socket _socket;
    private readonly string _path;
    private readonly uint _uid;
    private ExclusiveFileLock? _lock;
    private int _disposed;

    private UnixSocketTransport(Socket socket, string path, ExclusiveFileLock fileLock)
    {
        _socket = socket;
        _path = path;
        _lock = fileLock;
        _uid = UnixInterop.geteuid();
    }

    public static NativeResult<IIpcListener> Listen(string appId, string channel, bool currentUserOnly)
    {
        var path = IpcEndpoints.GetUnixSocketPath(appId, channel, currentUserOnly);
        if (path is null)
        {
            return NativeError.Failed(NativeCapabilities.Ipc, NativeErrorCodes.InvalidArgument,
                "No usable socket directory: every candidate would exceed the Unix domain socket length limit, or its per-user " +
                "directory belongs to another user, is not private (0700) or is a symbolic link.");
        }

        if (currentUserOnly && EnsurePrivateDirectory(Path.GetDirectoryName(path)!) is { } directoryError)
        {
            return directoryError;
        }

        switch (ExclusiveFileLock.TryAcquire(path + ".lock", out var fileLock, out var failure))
        {
            case FileLockOutcome.Contended:
                return NativeError.Failed(NativeCapabilities.Ipc, NativeErrorCodes.Conflict, $"Another process already serves the IPC channel ('{path}').");
            case FileLockOutcome.Failed:
                return NativeError.Failed(NativeCapabilities.Ipc, NativeErrorCodes.PlatformError,
                    $"The IPC channel lock could not be acquired: {failure?.Message}", failure);
        }

        Socket? socket = null;
        try
        {
            // We hold the channel lock, so anything at the path was left by a crashed server.
            File.Delete(path);
            socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, currentUserOnly
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
            socket.Listen(128);
            return new UnixSocketTransport(socket, path, fileLock!);
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
        {
            socket?.Dispose();
            fileLock!.Dispose();
            return ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } or UnauthorizedAccessException
                ? NativeError.Failed(NativeCapabilities.Ipc, NativeErrorCodes.Conflict, $"The IPC socket '{path}' is in use by another user or process.", ex)
                : NativeError.Failed(NativeCapabilities.Ipc, NativeErrorCodes.PlatformError, $"The IPC socket could not be created: {ex.Message}", ex);
        }
    }

    public async ValueTask<IpcRawConnection> AcceptAsync(CancellationToken cancellationToken)
    {
        var client = await _socket.AcceptAsync(cancellationToken).ConfigureAwait(false);
        bool? isCurrentUser = null;
        int? processId = null;
        string? processPath = null;
        if (UnixInterop.TryGetPeerCredentials(client, out var uid, out var pid))
        {
            isCurrentUser = uid == _uid;
            processId = pid;
            processPath = pid is { } p ? UnixInterop.TryGetProcessPath(p) : null;
        }

        return new IpcRawConnection(new NetworkStream(client, ownsSocket: true), processId, processPath, isCurrentUser);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _socket.Dispose();
        try
        {
            // Still holding the lock, so the socket file is ours to remove.
            File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        Interlocked.Exchange(ref _lock, null)?.Dispose();
        return ValueTask.CompletedTask;
    }

    public static async Task<IpcRawConnection> ConnectAsync(string appId, string channel, bool currentUserOnly, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var path = IpcEndpoints.GetUnixSocketPath(appId, channel, currentUserOnly)
            ?? throw new IpcException(NativeResultStatus.Failed, NativeErrorCodes.InvalidArgument, "The socket path exceeds the Unix domain socket length limit.");
        var endpoint = new UnixDomainSocketEndPoint(path);
        var stopwatch = Stopwatch.StartNew();
        Socket socket;
        while (true)
        {
            socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (SocketException)
            {
                socket.Dispose();
                var remaining = timeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    throw IpcTransport.NotListening(channel, timeout);
                }

                await Task.Delay(remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        bool? isCurrentUser = null;
        int? processId = null;
        string? processPath = null;
        if (UnixInterop.TryGetPeerCredentials(socket, out var uid, out var pid))
        {
            isCurrentUser = uid == UnixInterop.geteuid();
            processId = pid;
            processPath = pid is { } p ? UnixInterop.TryGetProcessPath(p) : null;
        }

        if (currentUserOnly && isCurrentUser != true)
        {
            socket.Dispose();
            throw new IpcException(NativeResultStatus.Failed, DesktopErrorCodes.IpcRejected,
                "The IPC server does not run as the current user; refusing to talk to a possible impostor.");
        }

        return new IpcRawConnection(new NetworkStream(socket, ownsSocket: true), processId, processPath, isCurrentUser);
    }

    private static NativeError? EnsurePrivateDirectory(string directory)
    {
        try
        {
            var created = UnixInterop.LStat(directory, out _) == UnixInterop.ErrnoNoEntry;
            if (created)
            {
                Directory.CreateDirectory(directory, PrivateDirectoryMode);
            }

            // Re-check after creating: another user may have won a race for the name in a shared root such as /tmp.
            var errno = UnixInterop.LStat(directory, out var status);
            if (errno != 0 || status.IsSymbolicLink || !status.IsDirectory || status.Uid != UnixInterop.geteuid())
            {
                return NativeError.Failed(NativeCapabilities.Ipc, NativeErrorCodes.PermissionDenied,
                    $"The IPC socket directory '{directory}' is not a directory owned by the current user; refusing to use it.");
            }

            if (!UnixInterop.HasPrivatePermissions(status))
            {
                if (!created)
                {
                    return NativeError.Failed(NativeCapabilities.Ipc, NativeErrorCodes.PermissionDenied, $"The IPC socket directory '{directory}' is not private (0700).");
                }

                // Our own new directory, narrowed by an unusual umask: fix the mode (only the owner can).
                File.SetUnixFileMode(directory, PrivateDirectoryMode);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return NativeError.Failed(NativeCapabilities.Ipc, NativeErrorCodes.PermissionDenied,
                $"The IPC socket directory '{directory}' is not owned by the current user or cannot be created: {ex.Message}", ex);
        }
    }
}
