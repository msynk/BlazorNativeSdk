using BlazorNativeSdk.Browser.Interop;
using BlazorNativeSdk.Devices;
using Microsoft.JSInterop;

namespace BlazorNativeSdk.Browser;

/// <summary>
/// A <see cref="NativeFile"/> whose content lives in the browser as a JavaScript <c>Blob</c> or <c>File</c> (picked,
/// captured or recorded). The bytes stay in the browser until <see cref="OpenReadAsync"/> streams them to .NET.
/// </summary>
/// <remarks>
/// Dispose the file when it is no longer needed: disposal releases the JavaScript reference so the browser can free the
/// memory. With Blazor Server, reading streams the content from the user's browser over the circuit.
/// </remarks>
public sealed class BrowserNativeFile : NativeFile
{
    private readonly BrowserInterop _interop;
    private readonly IJSObjectReference _blob;
    private int _disposed;

    internal BrowserNativeFile(BrowserInterop interop, IJSObjectReference blob, string name, string? contentType, long? size, DateTimeOffset? lastModified)
        : base(name, contentType, size, lastModified)
    {
        _interop = interop;
        _blob = blob;
    }

    /// <summary>
    /// The reference to the JavaScript <c>Blob</c>/<c>File</c>. Pass it to your own JavaScript (for example to upload it with
    /// <c>fetch</c> directly from the browser). It becomes invalid when the file is disposed.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The file has been disposed.</exception>
    public IJSObjectReference JSReference
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _blob;
        }
    }

    /// <inheritdoc />
    public override async ValueTask<Stream> OpenReadAsync(long maxAllowedSize = DefaultMaxReadSize, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (Size is { } size && size > maxAllowedSize)
        {
            throw new IOException($"File '{Name}' ({size} bytes) exceeds the maximum allowed size of {maxAllowedSize} bytes.");
        }

        try
        {
            return await _interop.OpenBlobStreamAsync(_blob, maxAllowedSize, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            throw new IOException($"File '{Name}' could not be read from the browser: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                await _blob.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JSDisconnectedException or InvalidOperationException or JSException or OperationCanceledException)
            {
                // The browser is gone; the Blob was released with the page.
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
