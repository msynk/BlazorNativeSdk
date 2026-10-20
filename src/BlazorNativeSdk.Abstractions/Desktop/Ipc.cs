namespace BlazorNativeSdk.Desktop;

/// <summary>A message exchanged over an IPC channel.</summary>
public sealed record IpcMessage
{
    /// <summary>An application-defined message type, such as <c>open-document</c>.</summary>
    public required string Type { get; init; }

    /// <summary>The payload, typically JSON.</summary>
    public string? Payload { get; init; }

    /// <summary>Correlates a response with its request. Set by the transport.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Creates a message.</summary>
    public static IpcMessage Create(string type, string? payload = null) => new() { Type = type, Payload = payload };
}

/// <summary>Information about the other end of an IPC connection.</summary>
public sealed record IpcPeer
{
    /// <summary>A per-connection id.</summary>
    public required string ConnectionId { get; init; }

    /// <summary>The peer process id when the OS reports it.</summary>
    public int? ProcessId { get; init; }

    /// <summary>The peer executable path when the OS reports it.</summary>
    public string? ProcessPath { get; init; }
}

/// <summary>Security settings shared by IPC servers and clients.</summary>
public sealed record IpcSecurityOptions
{
    /// <summary>
    /// Restrict connections to processes running as the current user (OS-enforced: pipe ACLs on Windows, socket file
    /// permissions on Unix). Enabled by default.
    /// </summary>
    public bool CurrentUserOnly { get; init; } = true;

    /// <summary>
    /// A shared secret (at least 16 bytes). When set, peers must complete an HMAC-SHA256 challenge-response handshake
    /// and every message carries a MAC, which defends against same-user processes that do not know the secret.
    /// </summary>
    public ReadOnlyMemory<byte>? SharedSecret { get; init; }

    /// <summary>When non-empty, the server only accepts clients whose executable path is in this list (Windows only).</summary>
    public IReadOnlyList<string> AllowedClientExecutables { get; init; } = [];

    /// <summary>The maximum message size in bytes.</summary>
    public int MaxMessageBytes { get; init; } = 1024 * 1024;

    /// <summary>The time allowed for the handshake.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>Handles a message received by an IPC server. Return a response for requests or <see langword="null"/> for one-way messages.</summary>
public delegate ValueTask<IpcMessage?> IpcMessageHandler(IpcMessage message, IpcPeer peer, CancellationToken cancellationToken);

/// <summary>A running IPC server. Dispose to stop listening.</summary>
public interface IIpcServer : IAsyncDisposable
{
    /// <summary>The channel name.</summary>
    string Channel { get; }

    /// <summary>The connected peers.</summary>
    IReadOnlyList<IpcPeer> Peers { get; }

    /// <summary>Sends a message to every connected peer.</summary>
    ValueTask<NativeResult> BroadcastAsync(IpcMessage message, CancellationToken cancellationToken = default);
}

/// <summary>A client connection. Dispose to disconnect.</summary>
public interface IIpcConnection : IAsyncDisposable
{
    /// <summary>The channel name.</summary>
    string Channel { get; }

    /// <summary>Whether the connection is open.</summary>
    bool IsConnected { get; }

    /// <summary>Sends a one-way message.</summary>
    ValueTask<NativeResult> SendAsync(IpcMessage message, CancellationToken cancellationToken = default);

    /// <summary>Sends a request and waits for the response.</summary>
    ValueTask<NativeResult<IpcMessage>> RequestAsync(IpcMessage message, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>Raised for messages pushed by the server.</summary>
    event EventHandler<IpcMessage>? MessageReceived;

    /// <summary>Raised when the connection closes.</summary>
    event EventHandler? Disconnected;
}

/// <summary>Authenticated, local-only inter-process messaging (named pipes on Windows, Unix domain sockets elsewhere).</summary>
public interface IIpcService : INativeCapability
{
    /// <summary>Starts a server on <paramref name="channel"/>.</summary>
    /// <returns>The server, or <see cref="NativeErrorCodes.Conflict"/> when another process already serves the channel.</returns>
    ValueTask<NativeResult<IIpcServer>> StartServerAsync(string channel, IpcMessageHandler handler, IpcSecurityOptions? security = null, CancellationToken cancellationToken = default);

    /// <summary>Connects to a server.</summary>
    /// <returns>The connection, or <see cref="NativeResultStatus.Unavailable"/> when no server is listening.</returns>
    ValueTask<NativeResult<IIpcConnection>> ConnectAsync(string channel, IpcSecurityOptions? security = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default);
}

/// <summary>The outcome of claiming single-instance ownership. Keep the primary lease for the lifetime of the app.</summary>
public interface ISingleInstanceLease : IAsyncDisposable
{
    /// <summary><see langword="true"/> when this process is the primary instance.</summary>
    bool IsPrimary { get; }

    /// <summary><see langword="true"/> when this process is secondary and its arguments were delivered to the primary instance.</summary>
    bool ArgumentsForwarded { get; }
}

/// <summary>
/// Enforces a single running instance per user. Secondary instances forward their arguments to the primary instance,
/// which receives them as <see cref="Lifecycle.ActivationKind.SecondaryInstance"/> (or <see cref="Lifecycle.ActivationKind.DeepLink"/>
/// when an argument is a URL) through <see cref="Lifecycle.IAppActivationService"/>. This is how desktop apps receive
/// deep links while already running.
/// </summary>
public interface ISingleInstanceService : INativeCapability
{
    /// <summary>Claims single-instance ownership. A secondary instance should exit after this returns.</summary>
    /// <param name="arguments">The process arguments to forward when this is a secondary instance.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    ValueTask<NativeResult<ISingleInstanceLease>> AcquireAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}
