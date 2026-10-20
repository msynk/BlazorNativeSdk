namespace BlazorNativeSdk.Browser.Interop;

/// <summary>Constants shared by the adapter and its JavaScript module.</summary>
internal static class BrowserConstants
{
    /// <summary>The adapter name reported in <see cref="CapabilityStatus.Adapter"/>.</summary>
    public const string AdapterName = "browser";

    /// <summary>The query parameter used by <c>blazor-native-sdk-sw.js</c> when a notification tap opens a new window.</summary>
    public const string NotificationClickQueryParameter = "bns-notification-click";

    /// <summary>The key prefix of the localStorage state store.</summary>
    public const string StateStorePrefix = "bns:";
}

/// <summary>A read-only stream that disposes an additional owner (a JS stream reference) together with the inner stream.</summary>
internal sealed class OwningStream : Stream
{
    private readonly Stream _inner;
    private readonly IAsyncDisposable _owner;
    private int _disposed;

    public OwningStream(Stream inner, IAsyncDisposable owner)
    {
        _inner = inner;
        _owner = owner;
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            await DisposeOwnerAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _inner.Dispose();
            _ = DisposeOwnerAsync().AsTask();
        }

        base.Dispose(disposing);
    }

    private async ValueTask DisposeOwnerAsync()
    {
        try
        {
            await _owner.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Microsoft.JSInterop.JSDisconnectedException or InvalidOperationException or Microsoft.JSInterop.JSException or OperationCanceledException)
        {
            // The browser is gone; the reference is released with it.
        }
    }
}
