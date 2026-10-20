using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace BlazorNativeSdk.Desktop.Ipc;

/// <summary>A running IPC server: accepts clients, authenticates them and dispatches their messages to the handler.</summary>
internal sealed partial class IpcServer : IIpcServer
{
    private const int MaxTypeLength = 256;
    private const int MaxCorrelationIdLength = 64;
    private static readonly TimeSpan s_rejectTimeout = TimeSpan.FromSeconds(1);

    private readonly IIpcListener _listener;
    private readonly IpcMessageHandler _handler;
    private readonly IpcSettings _settings;
    private readonly ILogger _logger;
    private readonly Action<IpcServer>? _onDisposed;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, Connection> _connections = new(StringComparer.Ordinal);
    private Task? _acceptLoop;
    private int _disposed;

    public IpcServer(string channel, IIpcListener listener, IpcMessageHandler handler, IpcSettings settings, ILogger logger, Action<IpcServer>? onDisposed = null)
    {
        Channel = channel;
        _listener = listener;
        _handler = handler;
        _settings = settings;
        _logger = logger;
        _onDisposed = onDisposed;
    }

    /// <inheritdoc />
    public string Channel { get; }

    /// <inheritdoc />
    public IReadOnlyList<IpcPeer> Peers => [.. _connections.Values.Where(c => c.Peer is not null).Select(c => c.Peer!.Info)];

    public void Start() => _acceptLoop = Task.Run(AcceptLoopAsync, CancellationToken.None);

    /// <inheritdoc />
    public async ValueTask<NativeResult> BroadcastAsync(IpcMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IpcClientConnection.IsValidType(message.Type))
        {
            return NativeError.Failed(NativeCapabilities.Ipc, NativeErrorCodes.InvalidArgument, $"The message type must be 1-{MaxTypeLength} characters.");
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            return NativeError.Failed(NativeCapabilities.Ipc, DesktopErrorCodes.IpcDisconnected, "The IPC server has been stopped.");
        }

        var peers = _connections.Values.Select(c => c.Peer).OfType<ServerPeer>().ToArray();
        var results = await Task.WhenAll(peers.Select(p => p.PushAsync(message, cancellationToken))).ConfigureAwait(false);

        // A peer that disconnects during a broadcast is not an error; an oversized message is.
        foreach (var result in results)
        {
            if (result is { Code: NativeErrorCodes.TooLarge or NativeErrorCodes.InvalidArgument } error)
            {
                return error;
            }
        }

        return NativeResult.Ok;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Stop the accept loop before releasing the listener so it never touches a disposed listener.
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(false);
        }

        await _listener.DisposeAsync().ConfigureAwait(false);

        var connections = _connections.Values.ToArray();
        foreach (var connection in connections)
        {
            connection.Close();
        }

        await Task.WhenAll(connections.Select(c => c.Completion)).ConfigureAwait(false);
        _settings.Dispose();
        _shutdown.Dispose();
        LogServerStopped(_logger, Channel);
        _onDisposed?.Invoke(this);
    }

    internal static bool IsValidCorrelationId(string? value) => !string.IsNullOrEmpty(value) && value.Length <= MaxCorrelationIdLength;

    private async Task AcceptLoopAsync()
    {
        var token = _shutdown.Token;
        while (!token.IsCancellationRequested)
        {
            IpcRawConnection raw;
            try
            {
                raw = await _listener.AcceptAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (token.IsCancellationRequested && ex is OperationCanceledException or ObjectDisposedException or IOException or System.Net.Sockets.SocketException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogAcceptFailed(_logger, Channel, ex);
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            var connection = new Connection(raw);
            _connections[connection.Id] = connection;
            connection.Completion = Task.Run(() => RunConnectionAsync(connection), CancellationToken.None);
        }
    }

    private async Task RunConnectionAsync(Connection connection)
    {
        var raw = connection.Raw;
        try
        {
            if (Admit(connection) is { } rejection)
            {
                LogRejected(_logger, Channel, raw.ProcessId, rejection);
                using var rejectCts = new CancellationTokenSource(s_rejectTimeout);
                await IpcHandshake.TrySendErrorAsync(raw.Stream, DesktopErrorCodes.IpcRejected, rejection, rejectCts.Token).ConfigureAwait(false);
                return;
            }

            IpcProtector protector;
            IpcEnvelope welcome;
            try
            {
                (protector, welcome) = await IpcHandshake.RunServerAsync(raw.Stream, Channel, _settings.Secret, _settings.HandshakeTimeout, _shutdown.Token).ConfigureAwait(false);
            }
            catch (IpcException ex)
            {
                LogHandshakeFailed(_logger, Channel, raw.ProcessId, ex.Message);
                return;
            }

            var info = new IpcPeer { ConnectionId = connection.Id, ProcessId = raw.ProcessId, ProcessPath = raw.ProcessPath };
            var link = new IpcLink(raw, protector, _settings.MaxMessageBytes, _settings.WriteTimeout);
            var peer = new ServerPeer(this, link, info);

            // Become visible to Peers/BroadcastAsync while holding the write lock that sends "welcome": by the time the client's
            // ConnectAsync returns it is a peer, and no push can overtake the end of the handshake.
            using (var welcomeCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token))
            {
                welcomeCts.CancelAfter(_settings.HandshakeTimeout);
                await link.SendHandshakeAsync(welcome, () => connection.Peer = peer, welcomeCts.Token).ConfigureAwait(false);
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                connection.Close();
            }

            LogClientConnected(_logger, Channel, connection.Id, raw.ProcessId);
            await peer.RunAsync().ConfigureAwait(false);
            LogClientDisconnected(_logger, Channel, connection.Id);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or EndOfStreamException)
        {
            // The client went away or the server is stopping.
        }
        catch (Exception ex)
        {
            LogConnectionFailed(_logger, Channel, ex);
        }
        finally
        {
            _connections.TryRemove(connection.Id, out _);
            if (connection.Peer is { } peer)
            {
                await peer.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await raw.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private string? Admit(Connection connection)
    {
        var raw = connection.Raw;
        if (_connections.Count > _settings.MaxConnections)
        {
            return "The server has reached its connection limit.";
        }

        // Windows enforces the user through the pipe DACL; on Unix the kernel-reported peer uid is checked here.
        if (_settings.CurrentUserOnly && !OperatingSystem.IsWindows() && raw.PeerIsCurrentUser != true)
        {
            return "The client does not run as the current user.";
        }

        if (_settings.AllowedClientExecutables.Count > 0 &&
            (raw.ProcessPath is null || !_settings.AllowedClientExecutables.Contains(Path.GetFullPath(raw.ProcessPath), StringComparer.OrdinalIgnoreCase)))
        {
            return "The client executable is not allowed.";
        }

        return null;
    }

    [LoggerMessage(EventId = 5101, Level = LogLevel.Debug, Message = "IPC client {ConnectionId} (pid {ProcessId}) connected to channel '{Channel}'.")]
    private static partial void LogClientConnected(ILogger logger, string channel, string connectionId, int? processId);

    [LoggerMessage(EventId = 5102, Level = LogLevel.Debug, Message = "IPC client {ConnectionId} disconnected from channel '{Channel}'.")]
    private static partial void LogClientDisconnected(ILogger logger, string channel, string connectionId);

    [LoggerMessage(EventId = 5103, Level = LogLevel.Warning, Message = "IPC channel '{Channel}' rejected a client (pid {ProcessId}): {Reason}")]
    private static partial void LogRejected(ILogger logger, string channel, int? processId, string reason);

    [LoggerMessage(EventId = 5104, Level = LogLevel.Warning, Message = "IPC handshake on channel '{Channel}' failed for pid {ProcessId}: {Reason}")]
    private static partial void LogHandshakeFailed(ILogger logger, string channel, int? processId, string reason);

    [LoggerMessage(EventId = 5105, Level = LogLevel.Error, Message = "Accepting an IPC client on channel '{Channel}' failed.")]
    private static partial void LogAcceptFailed(ILogger logger, string channel, Exception exception);

    [LoggerMessage(EventId = 5106, Level = LogLevel.Error, Message = "An IPC connection on channel '{Channel}' failed.")]
    private static partial void LogConnectionFailed(ILogger logger, string channel, Exception exception);

    [LoggerMessage(EventId = 5107, Level = LogLevel.Debug, Message = "IPC server on channel '{Channel}' stopped.")]
    private static partial void LogServerStopped(ILogger logger, string channel);

    [LoggerMessage(EventId = 5108, Level = LogLevel.Warning, Message = "IPC client {ConnectionId} on channel '{Channel}' violated the protocol: {Reason}")]
    private static partial void LogProtocolViolation(ILogger logger, string channel, string connectionId, string reason);

    [LoggerMessage(EventId = 5109, Level = LogLevel.Error, Message = "The IPC handler for '{MessageType}' on channel '{Channel}' failed.")]
    private static partial void LogHandlerFailed(ILogger logger, string channel, string messageType, Exception exception);

    /// <summary>An accepted client, before and after the handshake.</summary>
    private sealed class Connection(IpcRawConnection raw)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");

        public IpcRawConnection Raw { get; } = raw;

        public ServerPeer? Peer { get; set; }

        public Task Completion { get; set; } = Task.CompletedTask;

        public void Close()
        {
            if (Peer is { } peer)
            {
                peer.Link.Close();
            }
            else
            {
                try
                {
                    Raw.Stream.Dispose();
                }
                catch (IOException)
                {
                }
            }
        }
    }

    /// <summary>The server side of an authenticated connection.</summary>
    private sealed class ServerPeer(IpcServer server, IpcLink link, IpcPeer info) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _requestSlots = new(server._settings.MaxConcurrentRequests, server._settings.MaxConcurrentRequests);
        private readonly ConcurrentDictionary<Task, byte> _inFlight = new();
        private int _disposed;

        public IpcLink Link { get; } = link;

        public IpcPeer Info { get; } = info;

        public async Task RunAsync()
        {
            var token = Link.Lifetime;
            try
            {
                while (await Link.ReceiveAsync(token).ConfigureAwait(false) is { } envelope)
                {
                    switch (envelope.Kind)
                    {
                        case IpcKinds.Request when IsValidCorrelationId(envelope.CorrelationId) && IpcClientConnection.IsValidType(envelope.Type):
                            // Bounded concurrency per client: when every slot is busy, stop reading (back-pressure).
                            await _requestSlots.WaitAsync(token).ConfigureAwait(false);
                            var task = Task.Run(() => HandleRequestAsync(envelope), CancellationToken.None);
                            _inFlight.TryAdd(task, 0);
                            _ = task.ContinueWith(t => _inFlight.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                            break;
                        case IpcKinds.OneWay when IpcClientConnection.IsValidType(envelope.Type):
                            // One-way messages are handled in order.
                            await InvokeHandlerAsync(envelope, token).ConfigureAwait(false);
                            break;
                        default:
                            throw IpcException.Protocol($"Unexpected '{envelope.Kind}' frame from a client.");
                    }
                }
            }
            catch (IpcException ex)
            {
                LogProtocolViolation(server._logger, server.Channel, Info.ConnectionId, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or OperationCanceledException)
            {
            }
            finally
            {
                Link.Close();
            }
        }

        public async Task<NativeError?> PushAsync(IpcMessage message, CancellationToken cancellationToken)
        {
            try
            {
                await Link.SendAsync(new IpcEnvelope { Kind = IpcKinds.Push, Type = message.Type, Payload = message.Payload }, cancellationToken).ConfigureAwait(false);
                return null;
            }
            catch (IpcException ex)
            {
                return ex.ToError();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Link.Close();
            try
            {
                await Task.WhenAll(_inFlight.Keys).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A handler ignores cancellation; do not hang shutdown on it.
            }

            await Link.DisposeAsync().ConfigureAwait(false);
            _requestSlots.Dispose();
        }

        private async Task HandleRequestAsync(IpcEnvelope request)
        {
            try
            {
                IpcMessage? response;
                try
                {
                    response = await InvokeHandlerAsync(request, Link.Lifetime).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !Link.Lifetime.IsCancellationRequested)
                {
                    await TrySendAsync(new IpcEnvelope
                    {
                        Kind = IpcKinds.Error,
                        Type = DesktopErrorCodes.IpcHandlerFailed,
                        Payload = $"The server failed to handle '{request.Type}'.",
                        CorrelationId = request.CorrelationId,
                    }).ConfigureAwait(false);
                    return;
                }

                var envelope = new IpcEnvelope
                {
                    Kind = IpcKinds.Response,
                    Type = response is not null && IpcClientConnection.IsValidType(response.Type) ? response.Type : request.Type,
                    Payload = response?.Payload,
                    CorrelationId = request.CorrelationId,
                };
                try
                {
                    await Link.SendAsync(envelope, CancellationToken.None).ConfigureAwait(false);
                }
                catch (IpcException ex) when (ex.Code == NativeErrorCodes.TooLarge)
                {
                    await TrySendAsync(new IpcEnvelope
                    {
                        Kind = IpcKinds.Error,
                        Type = NativeErrorCodes.TooLarge,
                        Payload = "The response exceeds the message size limit.",
                        CorrelationId = request.CorrelationId,
                    }).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IpcException or OperationCanceledException or ObjectDisposedException)
            {
                // The connection closed while the request was being handled.
            }
            finally
            {
                try
                {
                    _requestSlots.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        private async Task<IpcMessage?> InvokeHandlerAsync(IpcEnvelope envelope, CancellationToken token)
        {
            var message = new IpcMessage { Type = envelope.Type!, Payload = envelope.Payload, CorrelationId = envelope.CorrelationId };
            try
            {
                return await server._handler(message, Info, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                LogHandlerFailed(server._logger, server.Channel, message.Type, ex);
                if (envelope.Kind == IpcKinds.OneWay)
                {
                    return null;
                }

                throw;
            }
        }

        private async Task TrySendAsync(IpcEnvelope envelope)
        {
            try
            {
                await Link.SendAsync(envelope, CancellationToken.None).ConfigureAwait(false);
            }
            catch (IpcException)
            {
            }
        }
    }
}
