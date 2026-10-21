using System.Collections.Concurrent;
using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.Lifecycle;

namespace BlazorNativeSdk.Testing;

/// <summary>
/// The channels of one simulated machine. <see cref="FakeIpcService"/>s that share a network can reach each other's servers,
/// which simulates separate processes; each fake creates its own network by default.
/// </summary>
public sealed class FakeIpcNetwork
{
    private readonly ConcurrentDictionary<string, FakeIpcServer> _servers = new(StringComparer.Ordinal);

    /// <summary>The channels that currently have a server.</summary>
    public IReadOnlyCollection<string> Channels => [.. _servers.Keys];

    internal bool TryAdd(FakeIpcServer server) => _servers.TryAdd(server.Channel, server);

    internal void Remove(FakeIpcServer server) => _servers.TryRemove(new KeyValuePair<string, FakeIpcServer>(server.Channel, server));

    internal FakeIpcServer? Find(string channel) => _servers.GetValueOrDefault(channel);
}

/// <summary>
/// An in-process <see cref="IIpcService"/>: clients connect directly to servers started on the same <see cref="FakeIpcNetwork"/>.
/// Results and error codes follow the desktop adapter: <see cref="NativeErrorCodes.Conflict"/> for a channel that already has
/// a server, <see cref="NativeResultStatus.Unavailable"/> when nobody listens, <see cref="NativeErrorCodes.Timeout"/> for
/// unanswered requests, and the adapter's <c>ipc.*</c> codes for authentication failures, handler failures and closed connections.
/// </summary>
/// <remarks>
/// To keep tests deterministic, <see cref="IIpcConnection.SendAsync"/> returns after the server handler ran and
/// <see cref="IIpcServer.BroadcastAsync"/> returns after every client's <see cref="IIpcConnection.MessageReceived"/> ran.
/// As with the real transport, exceptions thrown by one-way handlers and event subscribers are not reported to the sender.
/// </remarks>
public sealed class FakeIpcService : FakeCapability, IIpcService, IAsyncDisposable, IDisposable
{
    // The desktop adapter's DesktopErrorCodes (BlazorNativeSdk.Desktop is not referenced by this package).
    internal const string AuthenticationFailedCode = "ipc.authentication-failed";
    internal const string DisconnectedCode = "ipc.disconnected";
    internal const string HandlerFailedCode = "ipc.handler-failed";

    private readonly ConcurrentDictionary<IAsyncDisposable, byte> _owned = new(ReferenceEqualityComparer.Instance);

    /// <summary>Creates the fake.</summary>
    /// <param name="network">The simulated machine; share one between fakes to simulate several processes.</param>
    public FakeIpcService(FakeIpcNetwork? network = null)
        : base(NativeCapabilities.Ipc, "shared-secret", "peer-process-id", "broadcast")
    {
        Network = network ?? new FakeIpcNetwork();
    }

    /// <summary>The simulated machine this service belongs to.</summary>
    public FakeIpcNetwork Network { get; }

    /// <summary>The timeout of <see cref="IIpcConnection.RequestAsync"/> when none is passed. Default: 30 seconds.</summary>
    public TimeSpan DefaultRequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The running servers started by this service.</summary>
    public IReadOnlyList<FakeIpcServer> Servers => [.. _owned.Keys.OfType<FakeIpcServer>()];

    /// <summary>The open client connections created by this service.</summary>
    public IReadOnlyList<FakeIpcConnection> Connections => [.. _owned.Keys.OfType<FakeIpcConnection>()];

    /// <inheritdoc />
    public ValueTask<NativeResult<IIpcServer>> StartServerAsync(string channel, IpcMessageHandler handler, IpcSecurityOptions? security = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        cancellationToken.ThrowIfCancellationRequested();
        Record(nameof(StartServerAsync));
        if (Validate(channel, security) is { } invalid)
        {
            return ValueTask.FromResult<NativeResult<IIpcServer>>(invalid);
        }

        var server = new FakeIpcServer(this, channel, handler, security ?? new IpcSecurityOptions());
        if (!Network.TryAdd(server))
        {
            return ValueTask.FromResult<NativeResult<IIpcServer>>(
                NativeError.Failed(Capability, NativeErrorCodes.Conflict, $"Another process already serves the IPC channel '{channel}'."));
        }

        _owned.TryAdd(server, 0);
        return ValueTask.FromResult(NativeResult.Success<IIpcServer>(server));
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<IIpcConnection>> ConnectAsync(string channel, IpcSecurityOptions? security = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Record(nameof(ConnectAsync));
        if (Validate(channel, security) is { } invalid)
        {
            return ValueTask.FromResult<NativeResult<IIpcConnection>>(invalid);
        }

        if (timeout < TimeSpan.Zero)
        {
            return ValueTask.FromResult<NativeResult<IIpcConnection>>(NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "The connect timeout must not be negative."));
        }

        if (Network.Find(channel) is not { } server)
        {
            return ValueTask.FromResult<NativeResult<IIpcConnection>>(
                NativeError.Unavailable(Capability, NativeErrorCodes.NotFound, $"No server is listening on the IPC channel '{channel}'."));
        }

        if (!SecretsMatch(server.Security.SharedSecret, security?.SharedSecret))
        {
            return ValueTask.FromResult<NativeResult<IIpcConnection>>(new NativeError(NativeResultStatus.Failed, AuthenticationFailedCode, "Authentication failed.") { Capability = Capability });
        }

        var connection = server.Accept(this);
        _owned.TryAdd(connection, 0);
        return ValueTask.FromResult(NativeResult.Success<IIpcConnection>(connection));
    }

    /// <summary>Stops every server and closes every connection created by this service.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var owned in _owned.Keys.ToArray())
        {
            await owned.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Stops every server and closes every connection created by this service (containers may dispose synchronously).</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    internal void Forget(IAsyncDisposable owned) => _owned.TryRemove(owned, out _);

    internal static bool IsValidType(string? type) => !string.IsNullOrEmpty(type) && type.Length <= 256;

    internal static NativeError InvalidType() =>
        NativeError.Failed(NativeCapabilities.Ipc, NativeErrorCodes.InvalidArgument, "The message type must be 1-256 characters.");

    internal static NativeError Disconnected() =>
        new(NativeResultStatus.Failed, DisconnectedCode, "The IPC connection is closed.") { Capability = NativeCapabilities.Ipc };

    private static bool SecretsMatch(ReadOnlyMemory<byte>? server, ReadOnlyMemory<byte>? client) =>
        server is null ? client is null : client is { } c && c.Span.SequenceEqual(server.Value.Span);

    private NativeError? Validate(string channel, IpcSecurityOptions? security)
    {
        if (StatusError() is { } error)
        {
            return error;
        }

        if (string.IsNullOrWhiteSpace(channel) || channel.Length > 128 || channel.Any(char.IsControl))
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "The channel name must be 1-128 characters without control characters.");
        }

        if (security?.SharedSecret is { Length: < 16 })
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "SharedSecret must be at least 16 bytes.");
        }

        return security is { CurrentUserOnly: false, SharedSecret: null }
            ? NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "CurrentUserOnly = false requires a SharedSecret.")
            : null;
    }
}

/// <summary>A server started by <see cref="FakeIpcService"/>.</summary>
public sealed class FakeIpcServer : IIpcServer
{
    private readonly FakeIpcService _service;
    private readonly IpcMessageHandler _handler;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, FakeIpcConnection> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<IpcMessage> _broadcasts = new();
    private int _disposed;

    internal FakeIpcServer(FakeIpcService service, string channel, IpcMessageHandler handler, IpcSecurityOptions security)
    {
        _service = service;
        Channel = channel;
        _handler = handler;
        Security = security;
    }

    /// <inheritdoc />
    public string Channel { get; }

    /// <summary>The security options the server was started with.</summary>
    public IpcSecurityOptions Security { get; }

    /// <inheritdoc />
    public IReadOnlyList<IpcPeer> Peers => [.. _connections.Values.Where(static c => c.IsConnected).Select(static c => c.Peer)];

    /// <summary>The messages broadcast so far.</summary>
    public IReadOnlyList<IpcMessage> Broadcasts => [.. _broadcasts];

    /// <summary>Whether the server has stopped.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal CancellationToken Lifetime => _lifetime.Token;

    /// <inheritdoc />
    public ValueTask<NativeResult> BroadcastAsync(IpcMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        if (!FakeIpcService.IsValidType(message.Type))
        {
            return ValueTask.FromResult<NativeResult>(FakeIpcService.InvalidType());
        }

        if (IsDisposed)
        {
            return ValueTask.FromResult<NativeResult>(FakeIpcService.Disconnected());
        }

        _broadcasts.Enqueue(message);
        foreach (var connection in _connections.Values)
        {
            connection.Push(new IpcMessage { Type = message.Type, Payload = message.Payload });
        }

        return ValueTask.FromResult(NativeResult.Ok);
    }

    /// <summary>Stops the server: every connected client is disconnected and pending requests fail.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        // Disconnect clients before cancelling handlers, so pending requests report a closed connection.
        _service.Network.Remove(this);
        foreach (var connection in _connections.Values)
        {
            connection.Disconnect();
        }

        _lifetime.Cancel();
        _service.Forget(this);
        return ValueTask.CompletedTask;
    }

    internal FakeIpcConnection Accept(FakeIpcService clientService)
    {
        var peer = new IpcPeer { ConnectionId = Guid.NewGuid().ToString("N"), ProcessId = Environment.ProcessId, ProcessPath = Environment.ProcessPath };
        var connection = new FakeIpcConnection(this, clientService, peer);
        _connections[peer.ConnectionId] = connection;
        return connection;
    }

    internal void Detach(FakeIpcConnection connection) => _connections.TryRemove(connection.Peer.ConnectionId, out _);

    // Runs the handler as a task so synchronous exceptions surface like asynchronous ones.
    internal async Task<IpcMessage?> HandleAsync(IpcMessage message, IpcPeer peer, CancellationToken cancellationToken) =>
        await _handler(message, peer, cancellationToken).ConfigureAwait(false);
}

/// <summary>A client connection created by <see cref="FakeIpcService.ConnectAsync"/>.</summary>
public sealed class FakeIpcConnection : IIpcConnection
{
    private readonly FakeIpcServer _server;
    private readonly FakeIpcService _service;
    private readonly CancellationTokenSource _closed = new();
    private readonly TaskCompletionSource _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state;

    internal FakeIpcConnection(FakeIpcServer server, FakeIpcService service, IpcPeer peer)
    {
        _server = server;
        _service = service;
        Peer = peer;
    }

    /// <inheritdoc />
    public event EventHandler<IpcMessage>? MessageReceived;

    /// <inheritdoc />
    public event EventHandler? Disconnected;

    /// <inheritdoc />
    public string Channel => _server.Channel;

    /// <summary>How the server sees this client (the peer passed to its handler).</summary>
    public IpcPeer Peer { get; }

    /// <inheritdoc />
    public bool IsConnected => Volatile.Read(ref _state) == 0;

    /// <inheritdoc />
    public async ValueTask<NativeResult> SendAsync(IpcMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        if (!FakeIpcService.IsValidType(message.Type))
        {
            return FakeIpcService.InvalidType();
        }

        if (!IsConnected)
        {
            return FakeIpcService.Disconnected();
        }

        using var handlerToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _server.Lifetime, _closed.Token);
        try
        {
            await _server.HandleAsync(new IpcMessage { Type = message.Type, Payload = message.Payload }, Peer, handlerToken.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // One-way handler failures are not reported to the sender, as with the real transport.
        }

        return NativeResult.Ok;
    }

    /// <inheritdoc />
    public async ValueTask<NativeResult<IpcMessage>> RequestAsync(IpcMessage message, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        if (!FakeIpcService.IsValidType(message.Type))
        {
            return FakeIpcService.InvalidType();
        }

        var wait = timeout ?? _service.DefaultRequestTimeout;
        if (wait <= TimeSpan.Zero && wait != Timeout.InfiniteTimeSpan)
        {
            return NativeError.Failed(NativeCapabilities.Ipc, NativeErrorCodes.InvalidArgument, "The request timeout must be positive.");
        }

        if (!IsConnected)
        {
            return FakeIpcService.Disconnected();
        }

        var correlationId = Guid.NewGuid().ToString("N");
        using var handlerToken = CancellationTokenSource.CreateLinkedTokenSource(_server.Lifetime, _closed.Token);
        var handling = _server.HandleAsync(new IpcMessage { Type = message.Type, Payload = message.Payload, CorrelationId = correlationId }, Peer, handlerToken.Token);
        Task completed;
        try
        {
            completed = await Task.WhenAny(handling, _disconnected.Task).WaitAsync(wait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await handlerToken.CancelAsync().ConfigureAwait(false);
            return NativeError.Failed(NativeCapabilities.Ipc, NativeErrorCodes.Timeout, $"The server did not answer '{message.Type}' within {wait.TotalMilliseconds:0} ms.");
        }
        catch (OperationCanceledException)
        {
            await handlerToken.CancelAsync().ConfigureAwait(false);
            throw;
        }

        if (completed != handling)
        {
            return FakeIpcService.Disconnected();
        }

        try
        {
            var response = await handling.ConfigureAwait(false);
            return new IpcMessage
            {
                Type = response is not null && FakeIpcService.IsValidType(response.Type) ? response.Type : message.Type,
                Payload = response?.Payload,
                CorrelationId = correlationId,
            };
        }
        catch (Exception) when (!IsConnected || handlerToken.IsCancellationRequested)
        {
            // The handler was cancelled because the server stopped or the connection closed.
            return FakeIpcService.Disconnected();
        }
        catch (Exception)
        {
            // Handler details stay on the server, as with the real transport.
            return new NativeError(NativeResultStatus.Failed, FakeIpcService.HandlerFailedCode, $"The server failed to handle '{message.Type}'.") { Capability = NativeCapabilities.Ipc };
        }
    }

    /// <summary>Closes the connection; <see cref="Disconnected"/> is raised once.</summary>
    public ValueTask DisposeAsync()
    {
        Disconnect();
        return ValueTask.CompletedTask;
    }

    internal void Push(IpcMessage message)
    {
        if (!IsConnected)
        {
            return;
        }

        try
        {
            MessageReceived?.Invoke(this, message);
        }
        catch (Exception)
        {
            // A faulty subscriber must not affect the server, as with the real transport.
        }
    }

    internal void Disconnect()
    {
        if (Interlocked.Exchange(ref _state, 1) != 0)
        {
            return;
        }

        _closed.Cancel();
        _disconnected.TrySetResult();
        _server.Detach(this);
        _service.Forget(this);
        try
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // As with the real transport, subscriber failures are not propagated.
        }
    }
}

/// <summary>
/// An <see cref="ISingleInstanceService"/> for app-level tests. By default this process becomes the primary instance; set
/// <see cref="AnotherInstanceIsRunning"/> to make it a secondary instance that forwards its arguments. Acquiring again while
/// holding the primary lease behaves like a second process: the arguments are forwarded to this (primary) instance.
/// </summary>
/// <remarks>
/// Arguments received by the primary instance (from <see cref="SimulateSecondaryInstanceAsync"/> or a second acquire) are
/// published through <see cref="IActivationPublisher"/> like the desktop adapter does: an allow-listed deep link becomes a
/// <see cref="ActivationKind.DeepLink"/> activation (a URL with an app scheme as the first argument is treated as a protocol
/// launch and published alone); anything else becomes one <see cref="ActivationKind.SecondaryInstance"/> activation. File
/// arguments are not detected.
/// </remarks>
public sealed class FakeSingleInstanceService : FakeCapability, ISingleInstanceService
{
    private readonly IActivationPublisher? _publisher;
    private readonly DeepLinkOptions _deepLinks;
    private readonly Lock _gate = new();
    private readonly List<IReadOnlyList<string>> _forwarded = [];
    private readonly List<IReadOnlyList<string>> _received = [];
    private FakeSingleInstanceLease? _primary;

    /// <summary>Creates the fake.</summary>
    /// <param name="publisher">Receives the activations of the primary instance; without it, arguments are only recorded.</param>
    /// <param name="deepLinks">The allow-listed deep-link schemes and hosts (<c>NativeSdkOptions.DeepLinks</c>).</param>
    public FakeSingleInstanceService(IActivationPublisher? publisher = null, DeepLinkOptions? deepLinks = null)
        : base(NativeCapabilities.SingleInstance, "argument-forwarding", "deep-links")
    {
        _publisher = publisher;
        _deepLinks = deepLinks ?? new DeepLinkOptions();
    }

    /// <summary>When <see langword="true"/>, <see cref="AcquireAsync"/> behaves as if another (simulated) instance is the primary.</summary>
    public bool AnotherInstanceIsRunning { get; set; }

    /// <summary>Whether a secondary instance manages to deliver its arguments to the simulated primary. Default: <see langword="true"/>.</summary>
    public bool ForwardingSucceeds { get; set; } = true;

    /// <summary>
    /// Whether the primary instance publishes its own launch arguments as cold-start activations, like the desktop adapter's
    /// default. Default: <see langword="true"/>.
    /// </summary>
    public bool PublishLaunchActivation { get; set; } = true;

    /// <summary>The primary lease currently held by this process, if any.</summary>
    public ISingleInstanceLease? PrimaryLease
    {
        get
        {
            lock (_gate)
            {
                return _primary;
            }
        }
    }

    /// <summary>The arguments this process forwarded as a secondary instance, one entry per <see cref="AcquireAsync"/>.</summary>
    public IReadOnlyList<IReadOnlyList<string>> ForwardedArguments
    {
        get
        {
            lock (_gate)
            {
                return [.. _forwarded];
            }
        }
    }

    /// <summary>The arguments the primary instance received from secondary instances.</summary>
    public IReadOnlyList<IReadOnlyList<string>> ReceivedArguments
    {
        get
        {
            lock (_gate)
            {
                return [.. _received];
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<NativeResult<ISingleInstanceLease>> AcquireAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();
        Record(nameof(AcquireAsync));
        if (StatusError() is { } error)
        {
            return error;
        }

        IReadOnlyList<string> copy = [.. arguments];
        FakeSingleInstanceLease? primary = null;
        bool forwardToSelf;
        lock (_gate)
        {
            // A primary lease held by this process is "the other instance" for a second acquire.
            forwardToSelf = _primary is not null;
            if (forwardToSelf || AnotherInstanceIsRunning)
            {
                _forwarded.Add(copy);
            }
            else
            {
                primary = _primary = new FakeSingleInstanceLease(this, isPrimary: true, argumentsForwarded: false);
            }
        }

        if (forwardToSelf)
        {
            await DeliverAsync(copy, cancellationToken).ConfigureAwait(false);
            return new FakeSingleInstanceLease(this, isPrimary: false, argumentsForwarded: true);
        }

        if (primary is null)
        {
            return new FakeSingleInstanceLease(this, isPrimary: false, ForwardingSucceeds);
        }

        if (PublishLaunchActivation)
        {
            await PublishAsync(copy, ActivationKind.Launch, isColdStart: true, cancellationToken).ConfigureAwait(false);
        }

        return primary;
    }

    /// <summary>Simulates a second process forwarding its arguments to this primary instance.</summary>
    /// <returns><see langword="true"/> when the arguments were delivered; <see langword="false"/> when this process holds no primary lease.</returns>
    public async ValueTask<bool> SimulateSecondaryInstanceAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (PrimaryLease is null)
        {
            return false;
        }

        await DeliverAsync([.. arguments], cancellationToken).ConfigureAwait(false);
        return true;
    }

    internal void Release(FakeSingleInstanceLease lease)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_primary, lease))
            {
                _primary = null;
            }
        }
    }

    private async ValueTask DeliverAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _received.Add(arguments);
        }

        await PublishAsync(arguments, ActivationKind.SecondaryInstance, isColdStart: false, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PublishAsync(IReadOnlyList<string> arguments, ActivationKind fallbackKind, bool isColdStart, CancellationToken cancellationToken)
    {
        if (_publisher is null)
        {
            return;
        }

        foreach (var activation in CreateActivations(arguments, fallbackKind, isColdStart))
        {
            await _publisher.PublishAsync(activation, cancellationToken).ConfigureAwait(false);
        }
    }

    private List<AppActivation> CreateActivations(IReadOnlyList<string> arguments, ActivationKind fallbackKind, bool isColdStart)
    {
        if (arguments.Count > 0 && TryGetDeepLink(arguments[0], out var protocolUri) && _deepLinks.Schemes.Contains(protocolUri.Scheme))
        {
            return [new AppActivation { Kind = ActivationKind.DeepLink, Uri = protocolUri, Arguments = [arguments[0]], IsColdStart = isColdStart, Source = "fake" }];
        }

        var activations = new List<AppActivation>();
        foreach (var argument in arguments)
        {
            if (TryGetDeepLink(argument, out var uri))
            {
                activations.Add(new AppActivation { Kind = ActivationKind.DeepLink, Uri = uri, Arguments = arguments, IsColdStart = isColdStart, Source = "fake" });
            }
        }

        if (activations.Count == 0)
        {
            activations.Add(new AppActivation { Kind = fallbackKind, Arguments = arguments, IsColdStart = isColdStart, Source = "fake" });
        }

        return activations;
    }

    private bool TryGetDeepLink(string? argument, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrEmpty(argument) || !Uri.TryCreate(argument, UriKind.Absolute, out var candidate) ||
            !argument.StartsWith(candidate.Scheme + ":", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_deepLinks.Schemes.Contains(candidate.Scheme) ||
            (candidate.Scheme == Uri.UriSchemeHttps && candidate.Host.Length > 0 && _deepLinks.Hosts.Contains(candidate.Host)))
        {
            uri = candidate;
            return true;
        }

        return false;
    }
}

/// <summary>A lease returned by <see cref="FakeSingleInstanceService.AcquireAsync"/>.</summary>
public sealed class FakeSingleInstanceLease : ISingleInstanceLease
{
    private readonly FakeSingleInstanceService _owner;
    private int _disposed;

    internal FakeSingleInstanceLease(FakeSingleInstanceService owner, bool isPrimary, bool argumentsForwarded)
    {
        _owner = owner;
        IsPrimary = isPrimary;
        ArgumentsForwarded = argumentsForwarded;
    }

    /// <inheritdoc />
    public bool IsPrimary { get; }

    /// <inheritdoc />
    public bool ArgumentsForwarded { get; }

    /// <summary>Whether the lease has been released.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Releases the lease; a released primary lease lets the next <see cref="FakeSingleInstanceService.AcquireAsync"/> become primary.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _owner.Release(this);
        }

        return ValueTask.CompletedTask;
    }
}
