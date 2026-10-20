using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace BlazorNativeSdk.Desktop.Ipc;

/// <summary>The client end of an authenticated IPC connection.</summary>
internal sealed partial class IpcClientConnection : IIpcConnection
{
    private const int MaxTypeLength = 256;

    private readonly IpcLink _link;
    private readonly IpcSettings _settings;
    private readonly TimeSpan _defaultRequestTimeout;
    private readonly ILogger _logger;
    private readonly Action<IpcClientConnection>? _onDisposed;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<NativeResult<IpcMessage>>> _pending = new(StringComparer.Ordinal);
    private Task _readLoop = Task.CompletedTask;
    private int _disconnectedRaised;
    private int _disposed;

    public IpcClientConnection(string channel, IpcLink link, IpcSettings settings, TimeSpan defaultRequestTimeout, ILogger logger, Action<IpcClientConnection>? onDisposed = null)
    {
        Channel = channel;
        _link = link;
        _settings = settings;
        _defaultRequestTimeout = defaultRequestTimeout;
        _logger = logger;
        _onDisposed = onDisposed;
    }

    /// <inheritdoc />
    public string Channel { get; }

    /// <inheritdoc />
    public bool IsConnected => _link.IsOpen;

    /// <inheritdoc />
    public event EventHandler<IpcMessage>? MessageReceived;

    /// <inheritdoc />
    public event EventHandler? Disconnected;

    internal static bool IsValidType(string? type) => !string.IsNullOrEmpty(type) && type.Length <= MaxTypeLength;

    public void Start() => _readLoop = Task.Run(ReadLoopAsync, CancellationToken.None);

    /// <inheritdoc />
    public async ValueTask<NativeResult> SendAsync(IpcMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        if (Validate(message) is { } invalid)
        {
            return invalid;
        }

        try
        {
            await _link.SendAsync(new IpcEnvelope { Kind = IpcKinds.OneWay, Type = message.Type, Payload = message.Payload }, cancellationToken).ConfigureAwait(false);
            return NativeResult.Ok;
        }
        catch (IpcException ex)
        {
            return ex.ToError();
        }
    }

    /// <inheritdoc />
    public async ValueTask<NativeResult<IpcMessage>> RequestAsync(IpcMessage message, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        if (Validate(message) is { } invalid)
        {
            return invalid;
        }

        var wait = timeout ?? _defaultRequestTimeout;
        if (wait <= TimeSpan.Zero && wait != Timeout.InfiniteTimeSpan)
        {
            return NativeError.Failed(NativeCapabilities.Ipc, NativeErrorCodes.InvalidArgument, "The request timeout must be positive.");
        }

        var correlationId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<NativeResult<IpcMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[correlationId] = completion;

        // The read loop fails pending requests when it ends; re-check so a request registered after that cannot hang.
        if (!IsConnected)
        {
            _pending.TryRemove(correlationId, out _);
            return IpcLink.Disconnected().ToError();
        }

        try
        {
            await _link.SendAsync(new IpcEnvelope
            {
                Kind = IpcKinds.Request,
                Type = message.Type,
                Payload = message.Payload,
                CorrelationId = correlationId,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (IpcException ex)
        {
            _pending.TryRemove(correlationId, out _);
            return ex.ToError();
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(correlationId, out _);
            throw;
        }

        try
        {
            return await completion.Task.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _pending.TryRemove(correlationId, out _);
            return NativeError.Failed(NativeCapabilities.Ipc, NativeErrorCodes.Timeout,
                $"The server did not answer '{message.Type}' within {wait.TotalMilliseconds:0} ms.");
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(correlationId, out _);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _link.Close();
        await _readLoop.ConfigureAwait(false);
        await _link.DisposeAsync().ConfigureAwait(false);
        _settings.Dispose();
        _onDisposed?.Invoke(this);
    }

    private static NativeError? Validate(IpcMessage message) =>
        IsValidType(message.Type)
            ? null
            : NativeError.Failed(NativeCapabilities.Ipc, NativeErrorCodes.InvalidArgument, $"The message type must be 1-{MaxTypeLength} characters.");

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _link.ReceiveAsync(_link.Lifetime).ConfigureAwait(false) is { } envelope)
            {
                switch (envelope.Kind)
                {
                    case IpcKinds.Response:
                        Complete(envelope.CorrelationId, new IpcMessage { Type = envelope.Type ?? string.Empty, Payload = envelope.Payload, CorrelationId = envelope.CorrelationId });
                        break;
                    case IpcKinds.Error when envelope.CorrelationId is not null:
                        Complete(envelope.CorrelationId, ToRemoteError(envelope));
                        break;
                    case IpcKinds.Push when IsValidType(envelope.Type):
                        RaiseMessageReceived(new IpcMessage { Type = envelope.Type!, Payload = envelope.Payload });
                        break;
                    default:
                        throw IpcException.Protocol($"Unexpected '{envelope.Kind}' frame from the server.");
                }
            }
        }
        catch (IpcException ex)
        {
            LogProtocolViolation(_logger, Channel, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or OperationCanceledException)
        {
        }
        finally
        {
            _link.Close();
            foreach (var correlationId in _pending.Keys)
            {
                Complete(correlationId, IpcLink.Disconnected().ToError());
            }

            if (Interlocked.Exchange(ref _disconnectedRaised, 1) == 0)
            {
                try
                {
                    Disconnected?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    LogEventHandlerFailed(_logger, Channel, ex);
                }
            }
        }
    }

    private void Complete(string? correlationId, NativeResult<IpcMessage> result)
    {
        if (correlationId is not null && _pending.TryRemove(correlationId, out var completion))
        {
            completion.TrySetResult(result);
        }
    }

    private void RaiseMessageReceived(IpcMessage message)
    {
        try
        {
            MessageReceived?.Invoke(this, message);
        }
        catch (Exception ex)
        {
            // A faulty subscriber must not tear down the connection.
            LogEventHandlerFailed(_logger, Channel, ex);
        }
    }

    private static NativeError ToRemoteError(IpcEnvelope envelope)
    {
        var code = envelope.Type is NativeErrorCodes.TooLarge or DesktopErrorCodes.IpcHandlerFailed ? envelope.Type : DesktopErrorCodes.IpcHandlerFailed;
        var message = string.IsNullOrEmpty(envelope.Payload) ? "The server failed to handle the request." : envelope.Payload.Length <= 1024 ? envelope.Payload : envelope.Payload[..1024];
        return NativeError.Failed(NativeCapabilities.Ipc, code, message);
    }

    [LoggerMessage(EventId = 5121, Level = LogLevel.Warning, Message = "The IPC server on channel '{Channel}' violated the protocol: {Reason}")]
    private static partial void LogProtocolViolation(ILogger logger, string channel, string reason);

    [LoggerMessage(EventId = 5122, Level = LogLevel.Error, Message = "An IPC event handler for channel '{Channel}' threw.")]
    private static partial void LogEventHandlerFailed(ILogger logger, string channel, Exception exception);
}
