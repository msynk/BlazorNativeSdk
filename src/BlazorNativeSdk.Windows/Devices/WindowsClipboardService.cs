using System.Runtime.InteropServices;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.WindowsPlatform.Interop;
using Microsoft.Extensions.Logging;

namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>
/// <see cref="IClipboardService"/> over the Win32 clipboard (<c>CF_UNICODETEXT</c>), executed on the adapter's STA message
/// thread with the message-only window as clipboard owner. This avoids the WinRT clipboard's requirements (STA UI thread,
/// foreground window). <see cref="ContentChanged"/> uses <c>AddClipboardFormatListener</c>.
/// </summary>
/// <remarks>The clipboard is a shared resource; opening it is retried with backoff while another process holds it.</remarks>
internal sealed partial class WindowsClipboardService : IClipboardService, IDisposable, IAsyncDisposable
{
    private const int MaxOpenAttempts = 10;
    private readonly MessageWindowThread _thread;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private EventHandler? _contentChanged;
    private IDisposable? _handler;
    private volatile bool _listening;
    private int _disposed;

    public WindowsClipboardService(MessageWindowThread thread, ILogger<WindowsClipboardService> logger)
    {
        _thread = thread;
        _logger = logger;
    }

    public CapabilityId Capability => NativeCapabilities.Clipboard;

    public event EventHandler? ContentChanged
    {
        add
        {
            bool start;
            lock (_gate)
            {
                start = _contentChanged is null && value is not null;
                _contentChanged += value;
            }

            if (start)
            {
                _ = StartListeningAsync();
            }
        }

        remove
        {
            bool stop;
            lock (_gate)
            {
                _contentChanged -= value;
                stop = _contentChanged is null;
            }

            if (stop)
            {
                _ = StopListeningAsync();
            }
        }
    }

    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Available(
            Capability, WindowsAdapter.Name, ClipboardFeatures.ReadText, ClipboardFeatures.WriteText, ClipboardFeatures.ChangeNotification));

    public async ValueTask<NativeResult<string?>> GetTextAsync(CancellationToken cancellationToken = default)
    {
        var result = await WithClipboardAsync(ReadText, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<NativeResult> SetTextAsync(string? text, CancellationToken cancellationToken = default)
    {
        var result = await WithClipboardAsync(() => WriteText(text), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NativeResult.Ok : NativeResult.Failure(result.Error);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_gate)
        {
            _contentChanged = null;
        }

        if (_listening && _thread.IsRunning)
        {
            try
            {
                _ = _thread.Invoke(() => NativeMethods.RemoveClipboardFormatListener(_thread.MessageWindowHandle));
            }
            catch (Exception ex)
            {
                LogListenerFailed(_logger, ex);
            }
        }

        _handler?.Dispose();
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    // Opens the clipboard on the message thread, retrying with backoff (between attempts the message thread stays free).
    private async ValueTask<NativeResult<T>> WithClipboardAsync<T>(Func<NativeResult<T>> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        try
        {
            var delay = 10;
            for (var attempt = 1; ; attempt++)
            {
                var (opened, result) = await _thread.InvokeAsync(() =>
                {
                    if (!NativeMethods.OpenClipboard(_thread.MessageWindow))
                    {
                        return (false, default(NativeResult<T>));
                    }

                    try
                    {
                        return (true, operation());
                    }
                    finally
                    {
                        _ = NativeMethods.CloseClipboard();
                    }
                }).ConfigureAwait(false);

                if (opened)
                {
                    return result;
                }

                if (attempt >= MaxOpenAttempts)
                {
                    return NativeError.Unavailable(Capability, NativeErrorCodes.Timeout, "The clipboard is in use by another application.");
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = Math.Min(delay * 2, 200);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return NativeError.FromException(Capability, ex);
        }
    }

    private unsafe NativeResult<string?> ReadText()
    {
        if (!NativeMethods.IsClipboardFormatAvailable(Win32.CF_UNICODETEXT))
        {
            return NativeResult.Success<string?>(null);
        }

        var handle = NativeMethods.GetClipboardData(Win32.CF_UNICODETEXT);
        if (handle == 0)
        {
            return NativeError.Failed(Capability, NativeErrorCodes.PlatformError, $"GetClipboardData failed with Win32 error {Marshal.GetLastPInvokeError()}.");
        }

        var data = (char*)NativeMethods.GlobalLock(handle);
        if (data is null)
        {
            return NativeError.Failed(Capability, NativeErrorCodes.PlatformError, $"GlobalLock failed with Win32 error {Marshal.GetLastPInvokeError()}.");
        }

        try
        {
            // Never read past the allocation, even when the data is not null-terminated.
            var maxChars = (int)Math.Min((ulong)NativeMethods.GlobalSize(handle) / sizeof(char), int.MaxValue);
            var span = new ReadOnlySpan<char>(data, maxChars);
            var terminator = span.IndexOf('\0');
            return NativeResult.Success<string?>(new string(terminator >= 0 ? span[..terminator] : span));
        }
        finally
        {
            _ = NativeMethods.GlobalUnlock(handle);
        }
    }

    private unsafe NativeResult<bool> WriteText(string? text)
    {
        if (!NativeMethods.EmptyClipboard())
        {
            return NativeError.Failed(Capability, NativeErrorCodes.PlatformError, $"EmptyClipboard failed with Win32 error {Marshal.GetLastPInvokeError()}.");
        }

        if (text is null)
        {
            return true;
        }

        var bytes = ((nuint)text.Length + 1) * sizeof(char);
        using var memory = new SafeGlobalMemoryHandle(NativeMethods.GlobalAlloc(Win32.GMEM_MOVEABLE, bytes));
        if (memory.IsInvalid)
        {
            return NativeError.Failed(Capability, NativeErrorCodes.TooLarge, "Could not allocate clipboard memory.");
        }

        var destination = (char*)NativeMethods.GlobalLock(memory.DangerousGetHandle());
        if (destination is null)
        {
            return NativeError.Failed(Capability, NativeErrorCodes.PlatformError, $"GlobalLock failed with Win32 error {Marshal.GetLastPInvokeError()}.");
        }

        try
        {
            text.AsSpan().CopyTo(new Span<char>(destination, text.Length));
            destination[text.Length] = '\0';
        }
        finally
        {
            _ = NativeMethods.GlobalUnlock(memory.DangerousGetHandle());
        }

        if (NativeMethods.SetClipboardData(Win32.CF_UNICODETEXT, memory.DangerousGetHandle()) == 0)
        {
            return NativeError.Failed(Capability, NativeErrorCodes.PlatformError, $"SetClipboardData failed with Win32 error {Marshal.GetLastPInvokeError()}.");
        }

        // The system owns the memory now.
        memory.SetHandleAsInvalid();
        return true;
    }

    private async Task StartListeningAsync()
    {
        try
        {
            lock (_gate)
            {
                _handler ??= _thread.AddHandler(OnMessage);
            }

            var added = await _thread.InvokeAsync(() => _listening || (_listening = NativeMethods.AddClipboardFormatListener(_thread.MessageWindow))).ConfigureAwait(false);
            if (!added)
            {
                LogListenerFailed(_logger, null);
            }
        }
        catch (Exception ex)
        {
            LogListenerFailed(_logger, ex);
        }
    }

    private async Task StopListeningAsync()
    {
        if (!_thread.IsRunning)
        {
            return;
        }

        try
        {
            await _thread.InvokeAsync(() =>
            {
                // Re-check on the message thread: a subscriber may have been added meanwhile.
                lock (_gate)
                {
                    if (_contentChanged is not null || !_listening)
                    {
                        return;
                    }
                }

                _ = NativeMethods.RemoveClipboardFormatListener(_thread.MessageWindowHandle);
                _listening = false;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogListenerFailed(_logger, ex);
        }
    }

    private bool OnMessage(nint hwnd, uint message, nint wParam, nint lParam, out nint result)
    {
        result = 0;
        if (message != Win32.WM_CLIPBOARDUPDATE || hwnd != _thread.MessageWindowHandle)
        {
            return false;
        }

        EventHandler? handler;
        lock (_gate)
        {
            handler = _contentChanged;
        }

        if (handler is not null)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    handler(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    LogHandlerFailed(_logger, ex);
                }
            });
        }

        return true;
    }

    [LoggerMessage(EventId = 3501, Level = LogLevel.Warning, Message = "The clipboard change listener could not be updated.")]
    private static partial void LogListenerFailed(ILogger logger, Exception? exception);

    [LoggerMessage(EventId = 3502, Level = LogLevel.Error, Message = "A clipboard ContentChanged handler failed.")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception);
}
