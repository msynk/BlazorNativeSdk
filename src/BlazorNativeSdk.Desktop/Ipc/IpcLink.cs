using System.Text.Json;

namespace BlazorNativeSdk.Desktop.Ipc;

/// <summary>
/// An established, handshaken connection: framing, sequence/MAC protection, a write lock for concurrent senders and a
/// lifetime token. Used by both server-side peers and client connections.
/// </summary>
internal sealed class IpcLink : IAsyncDisposable
{
    private readonly IpcRawConnection _raw;
    private readonly IpcProtector _protector;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly int _maxMessageBytes;
    private readonly TimeSpan _writeTimeout;
    private int _closed;
    private int _disposed;

    public IpcLink(IpcRawConnection raw, IpcProtector protector, int maxMessageBytes, TimeSpan writeTimeout)
    {
        _raw = raw;
        _protector = protector;
        _maxMessageBytes = maxMessageBytes;
        _writeTimeout = writeTimeout;
        Lifetime = _lifetime.Token;
    }

    /// <summary>Cancelled when the link closes.</summary>
    public CancellationToken Lifetime { get; }

    /// <summary>Whether the link is open.</summary>
    public bool IsOpen => Volatile.Read(ref _closed) == 0;

    /// <summary>
    /// Sends an envelope. Concurrent callers are serialized; <paramref name="cancellationToken"/> only applies while waiting
    /// for the write lock, because abandoning a half-written frame would corrupt the stream. A write that fails or exceeds
    /// the write timeout closes the link.
    /// </summary>
    /// <exception cref="IpcException">The message is too large, the link is closed, or the write failed.</exception>
    public async ValueTask SendAsync(IpcEnvelope envelope, CancellationToken cancellationToken)
    {
        // Reject oversized messages before taking a sequence number. The 160-byte allowance covers seq and MAC.
        if (EstimateSize(envelope) + 160 > _maxMessageBytes)
        {
            throw TooLarge();
        }

        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            throw Disconnected();
        }

        try
        {
            if (!IsOpen)
            {
                throw Disconnected();
            }

            _protector.Protect(envelope);
            var bytes = IpcFraming.Serialize(envelope);
            if (bytes.Length > _maxMessageBytes)
            {
                // The sequence number was consumed without sending; the stream can no longer stay in sync.
                Close();
                throw TooLarge();
            }

            using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
            writeCts.CancelAfter(_writeTimeout);
            try
            {
                await IpcFraming.WriteFrameAsync(_raw.Stream, bytes, _maxMessageBytes, writeCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                var timedOut = ex is OperationCanceledException && !Lifetime.IsCancellationRequested;
                Close();
                throw timedOut
                    ? new IpcException(NativeResultStatus.Failed, NativeErrorCodes.Timeout, "The peer stopped reading; the connection was closed.", ex)
                    : Disconnected(ex);
            }
        }
        finally
        {
            try
            {
                _writeLock.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    /// Writes an unprotected handshake frame under the write lock, running <paramref name="whileLocked"/> first. No other
    /// frame can be written until it completes, so state published by the callback (peer registration) never lets a
    /// broadcast overtake the handshake.
    /// </summary>
    public async ValueTask SendHandshakeAsync(IpcEnvelope envelope, Action whileLocked, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            whileLocked();
            await IpcFraming.WriteFrameAsync(_raw.Stream, IpcFraming.Serialize(envelope), IpcFraming.HandshakeMaxBytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Receives and verifies the next envelope. Only one reader may call this.</summary>
    /// <returns>The envelope, or <see langword="null"/> when the peer closed the connection.</returns>
    /// <exception cref="IpcException">The frame is malformed, too large, forged, replayed or out of order.</exception>
    public async ValueTask<IpcEnvelope?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var envelope = await IpcFraming.ReadFrameAsync(_raw.Stream, _maxMessageBytes, cancellationToken).ConfigureAwait(false);
        if (envelope is null)
        {
            return null;
        }

        if (!_protector.TryAccept(envelope, out var reason))
        {
            throw IpcException.Protocol(reason);
        }

        return envelope;
    }

    /// <summary>Closes the link: cancels <see cref="Lifetime"/> and closes the stream, unblocking pending reads and writes.</summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        try
        {
            _lifetime.Cancel();
        }
        catch (AggregateException)
        {
            // Callbacks registered by handlers threw; the link is closing regardless.
        }

        try
        {
            _raw.Stream.Dispose();
        }
        catch (IOException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Close();
        await _raw.DisposeAsync().ConfigureAwait(false);
        _protector.Dispose();
        _lifetime.Dispose();
        _writeLock.Dispose();
    }

    internal static IpcException Disconnected(Exception? inner = null) =>
        new(NativeResultStatus.Failed, DesktopErrorCodes.IpcDisconnected, "The IPC connection is closed.", inner);

    private IpcException TooLarge() =>
        new(NativeResultStatus.Failed, NativeErrorCodes.TooLarge, $"The message exceeds the limit of {_maxMessageBytes} bytes (IpcSecurityOptions.MaxMessageBytes).");

    private long EstimateSize(IpcEnvelope envelope)
    {
        // JSON escaping expands a UTF-8 byte to at most 6 bytes (\u00XX). When even the worst case fits, skip the exact
        // measurement; otherwise serialize once to measure precisely (strings over the limit are rejected without that).
        var raw = 64L + Utf8Length(envelope.Kind) + Utf8Length(envelope.Type) + Utf8Length(envelope.Payload) + Utf8Length(envelope.CorrelationId);
        if (raw * 6 <= _maxMessageBytes || raw > _maxMessageBytes)
        {
            return raw;
        }

        return JsonSerializer.SerializeToUtf8Bytes(envelope, DesktopJsonContext.Default.IpcEnvelope).LongLength;

        static long Utf8Length(string? value) => value is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(value);
    }
}
