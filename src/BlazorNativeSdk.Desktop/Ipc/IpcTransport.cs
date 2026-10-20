namespace BlazorNativeSdk.Desktop.Ipc;

/// <summary>A connected byte stream plus what the OS reports about the peer process.</summary>
internal sealed class IpcRawConnection : IAsyncDisposable
{
    public IpcRawConnection(Stream stream, int? processId, string? processPath, bool? peerIsCurrentUser)
    {
        Stream = stream;
        ProcessId = processId;
        ProcessPath = processPath;
        PeerIsCurrentUser = peerIsCurrentUser;
    }

    public Stream Stream { get; }

    public int? ProcessId { get; }

    public string? ProcessPath { get; }

    /// <summary>Whether the OS reports that the peer runs as the current user; <see langword="null"/> when unknown.</summary>
    public bool? PeerIsCurrentUser { get; }

    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

/// <summary>A platform listener that owns an IPC endpoint name for its lifetime.</summary>
internal interface IIpcListener : IAsyncDisposable
{
    /// <summary>Waits for the next client.</summary>
    ValueTask<IpcRawConnection> AcceptAsync(CancellationToken cancellationToken);
}

/// <summary>Creates listeners and client connections for the current OS.</summary>
internal static class IpcTransport
{
    /// <summary>Whether local IPC is implemented on this OS.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>Claims the endpoint for <paramref name="channel"/>; fails with <see cref="NativeErrorCodes.Conflict"/> when it is taken.</summary>
    public static NativeResult<IIpcListener> Listen(string appId, string channel, bool currentUserOnly)
    {
        if (OperatingSystem.IsWindows())
        {
            return NamedPipeTransport.Listen(IpcEndpoints.GetPipeName(appId, channel, currentUserOnly), currentUserOnly);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return UnixSocketTransport.Listen(appId, channel, currentUserOnly);
        }

        return NativeError.Unsupported(NativeCapabilities.Ipc);
    }

    /// <summary>Connects to the endpoint for <paramref name="channel"/>, retrying until <paramref name="timeout"/> while no server listens.</summary>
    /// <exception cref="IpcException">No server is listening, or the server failed identity verification.</exception>
    public static Task<IpcRawConnection> ConnectAsync(string appId, string channel, bool currentUserOnly, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return NamedPipeTransport.ConnectAsync(IpcEndpoints.GetPipeName(appId, channel, currentUserOnly), channel, currentUserOnly, timeout, cancellationToken);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return UnixSocketTransport.ConnectAsync(appId, channel, currentUserOnly, timeout, cancellationToken);
        }

        throw new IpcException(NativeResultStatus.Unsupported, NativeErrorCodes.NotSupported, "Local IPC is not supported on this platform.");
    }

    internal static IpcException NotListening(string channel, TimeSpan timeout) =>
        new(NativeResultStatus.Unavailable, NativeErrorCodes.NotFound,
            $"No IPC server is listening on channel '{channel}' (waited {timeout.TotalMilliseconds:0} ms).");
}
