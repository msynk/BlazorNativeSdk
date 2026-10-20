using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using BlazorNativeSdk.Desktop.Interop;
using Microsoft.Win32.SafeHandles;

namespace BlazorNativeSdk.Desktop.Ipc;

/// <summary>Windows named-pipe transport.</summary>
/// <remarks>
/// <para>
/// Server instances are created with <c>CreateNamedPipeW</c> directly (wrapped in <see cref="NamedPipeServerStream"/>) so
/// the adapter controls every flag: <c>FILE_FLAG_FIRST_PIPE_INSTANCE</c> on the first instance makes claiming a channel
/// atomic (a second server gets <see cref="NativeErrorCodes.Conflict"/> instead of silently sharing the name),
/// <c>PIPE_REJECT_REMOTE_CLIENTS</c> refuses SMB connections from other machines, and an explicit protected DACL grants
/// access to the current user's SID only (or to authenticated users, without the right to create instances, for
/// machine-wide channels). The next instance is always created before a connected one is handed out, so the pipe name is
/// never left unowned for another process to squat.
/// </para>
/// <para>
/// Clients connect with <see cref="TokenImpersonationLevel.Identification"/> so a server can never impersonate them, and
/// for per-user channels verify that the pipe is owned by their own SID before sending anything.
/// </para>
/// <para>
/// The peer's executable path is bound to the connection: a process whose creation time is later than the moment the
/// connection was observed reused the id of a peer that already exited, and gets no path (so
/// <see cref="IpcSecurityOptions.AllowedClientExecutables"/> rejects it).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class NamedPipeTransport : IIpcListener
{
    private const uint BufferSize = 64 * 1024;
    private readonly string _pipePath;
    private nint _securityDescriptor;
    private NamedPipeServerStream? _pending;
    private int _disposed;

    private NamedPipeTransport(string pipePath, nint securityDescriptor, NamedPipeServerStream first)
    {
        _pipePath = pipePath;
        _securityDescriptor = securityDescriptor;
        _pending = first;
    }

    public static NativeResult<IIpcListener> Listen(string pipeName, bool currentUserOnly)
    {
        var pipePath = @"\\.\pipe\" + pipeName;
        if (!WindowsInterop.ConvertStringSecurityDescriptorToSecurityDescriptor(BuildSddl(currentUserOnly), WindowsInterop.SddlRevision1, out var descriptor, out _))
        {
            var error = Marshal.GetLastPInvokeError();
            return NativeError.Failed(NativeCapabilities.Ipc, NativeErrorCodes.PlatformError,
                $"The pipe security descriptor could not be created: {new Win32Exception(error).Message}");
        }

        var first = TryCreateInstance(pipePath, descriptor, isFirst: true, out var createError);
        if (first is null)
        {
            WindowsInterop.LocalFree(descriptor);
            return createError is WindowsInterop.ErrorAccessDenied or WindowsInterop.ErrorPipeBusy
                ? NativeError.Failed(NativeCapabilities.Ipc, NativeErrorCodes.Conflict, $"Another process already serves the IPC channel (pipe '{pipeName}').")
                : NativeError.Failed(NativeCapabilities.Ipc, NativeErrorCodes.PlatformError,
                    $"The named pipe could not be created: {new Win32Exception(createError).Message}");
        }

        return new NamedPipeTransport(pipePath, descriptor, first);
    }

    public async ValueTask<IpcRawConnection> AcceptAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var server = _pending ?? CreateNextInstance();
            _pending = server;
            DateTime connectedAt;
            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                connectedAt = DateTime.UtcNow;
            }
            catch (IOException)
            {
                // The client vanished between connecting and being accepted (ERROR_NO_DATA). Replace the instance,
                // creating the new one first so the name stays owned.
                var replacement = CreateNextInstance();
                _pending = replacement;
                await server.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            // Keep listening before handing out the connected instance.
            try
            {
                _pending = CreateNextInstance();
            }
            catch (IOException)
            {
                _pending = null;
            }

            int? processId = null;
            string? processPath = null;
            if (WindowsInterop.GetNamedPipeClientProcessId(server.SafePipeHandle, out var pid))
            {
                // A client that exited right after connecting frees its id; a process started after the connection was
                // accepted is not the client, so its path must not satisfy AllowedClientExecutables.
                processId = (int)pid;
                processPath = WindowsInterop.TryGetProcessImagePath(pid, connectedAt);
            }

            return new IpcRawConnection(server, processId, processPath, peerIsCurrentUser: null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _pending, null) is { } pending)
        {
            await pending.DisposeAsync().ConfigureAwait(false);
        }

        var descriptor = Interlocked.Exchange(ref _securityDescriptor, 0);
        if (descriptor != 0)
        {
            WindowsInterop.LocalFree(descriptor);
        }
    }

    public static async Task<IpcRawConnection> ConnectAsync(string pipeName, string channel, bool currentUserOnly, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            var milliseconds = (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
            await client.ConnectAsync(milliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw IpcTransport.NotListening(channel, timeout);
        }
        catch (UnauthorizedAccessException ex)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw new IpcException(NativeResultStatus.Failed, DesktopErrorCodes.IpcRejected,
                "Access to the IPC channel was denied (the pipe belongs to another user or session).", ex);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        try
        {
            if (currentUserOnly)
            {
                using var identity = WindowsIdentity.GetCurrent();
                var owner = client.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
                if (owner is null || !owner.Equals(identity.User))
                {
                    throw new IpcException(NativeResultStatus.Failed, DesktopErrorCodes.IpcRejected,
                        "The IPC server is not owned by the current user; refusing to talk to a possible impostor.");
                }
            }

            int? processId = null;
            string? processPath = null;
            if (WindowsInterop.GetNamedPipeServerProcessId(client.SafePipeHandle, out var pid))
            {
                processId = (int)pid;
                processPath = WindowsInterop.TryGetProcessImagePath(pid, DateTime.UtcNow);
            }

            return new IpcRawConnection(client, processId, processPath, peerIsCurrentUser: currentUserOnly ? true : null);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private NamedPipeServerStream CreateNextInstance()
    {
        var instance = TryCreateInstance(_pipePath, _securityDescriptor, isFirst: false, out var error)
            ?? throw new IOException($"A named pipe instance could not be created: {new Win32Exception(error).Message}");
        if (Volatile.Read(ref _disposed) != 0)
        {
            instance.Dispose();
            throw new ObjectDisposedException(nameof(NamedPipeTransport));
        }

        return instance;
    }

    private static unsafe NamedPipeServerStream? TryCreateInstance(string pipePath, nint securityDescriptor, bool isFirst, out int error)
    {
        var attributes = new WindowsInterop.SecurityAttributes
        {
            Length = sizeof(WindowsInterop.SecurityAttributes),
            SecurityDescriptor = securityDescriptor,
            InheritHandle = 0,
        };
        var openMode = WindowsInterop.PipeAccessDuplex | WindowsInterop.FileFlagOverlapped | (isFirst ? WindowsInterop.FileFlagFirstPipeInstance : 0);
        var pipeMode = WindowsInterop.PipeTypeByte | WindowsInterop.PipeReadModeByte | WindowsInterop.PipeWait | WindowsInterop.PipeRejectRemoteClients;
        var raw = WindowsInterop.CreateNamedPipe(pipePath, openMode, pipeMode, WindowsInterop.PipeUnlimitedInstances, BufferSize, BufferSize, 0, ref attributes);
        if (raw == -1 || raw == 0)
        {
            error = Marshal.GetLastPInvokeError();
            return null;
        }

        error = 0;
        var handle = new SafePipeHandle(raw, ownsHandle: true);
        try
        {
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static string BuildSddl(bool currentUserOnly)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("The current user has no SID.");

        // Protected DACL (P) so nothing is inherited. GA = generic all for the owner (needed to create more instances).
        // Machine-wide channels add SYSTEM and Authenticated Users with read/write but without FILE_CREATE_PIPE_INSTANCE
        // (0x0012019b = FILE_GENERIC_READ | FILE_WRITE_DATA | FILE_WRITE_EA | FILE_WRITE_ATTRIBUTES), so other users can
        // connect but cannot add rogue server instances.
        return currentUserOnly
            ? $"O:{sid}D:P(A;;GA;;;{sid})"
            : $"O:{sid}D:P(A;;GA;;;{sid})(A;;GA;;;SY)(A;;0x0012019b;;;AU)";
    }
}
