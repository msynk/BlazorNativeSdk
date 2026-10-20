#if WINDOWS || MACCATALYST
using BlazorNativeSdk.Desktop;
using Microsoft.Extensions.Logging;

namespace BlazorNativeSdk.Maui;

/// <summary>
/// Saves a window's placement through <see cref="IWindowPlacementStore"/>: debounced while the window moves or resizes, and
/// immediately when it closes. Placements are captured on the UI thread; writes run on the thread pool, one at a time, and
/// unchanged placements are not written again.
/// </summary>
internal sealed partial class WindowPlacementSaver : IDisposable
{
    private readonly string _windowId;
    private readonly IMauiNativeWindow _window;
    private readonly IWindowPlacementStore _store;
    private readonly MauiUIDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _delay;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Lock _gate = new();
    private ITimer? _timer;
    private WindowPlacement? _latest;
    private WindowPlacement? _saved;
    private int _disposed;

    /// <summary>Creates the saver.</summary>
    public WindowPlacementSaver(
        string windowId,
        IMauiNativeWindow window,
        IWindowPlacementStore store,
        MauiUIDispatcher dispatcher,
        TimeProvider timeProvider,
        TimeSpan delay,
        ILogger logger)
    {
        _windowId = windowId;
        _window = window;
        _store = store;
        _dispatcher = dispatcher;
        _timeProvider = timeProvider;
        _delay = delay;
        _logger = logger;
        window.StateChanged += OnChanged;
        window.BoundsChanged += OnChanged;
        window.CloseApproved += OnCloseApproved;
    }

    /// <summary>Remembers the placement that was restored, so it is not written back unchanged.</summary>
    public void SetBaseline(WindowPlacement? placement)
    {
        lock (_gate)
        {
            _saved = placement;
        }
    }

    /// <summary>
    /// Captures the final placement (when the window still exists) and saves it immediately. UI thread.
    /// </summary>
    public Task FlushAsync()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }

        Capture();
        return SaveLatestAsync();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _window.StateChanged -= OnChanged;
        _window.BoundsChanged -= OnChanged;
        _window.CloseApproved -= OnCloseApproved;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Capture();
        lock (_gate)
        {
            if (_timer is null)
            {
                _timer = _timeProvider.CreateTimer(static state => ((WindowPlacementSaver)state!).OnTimer(), this, _delay, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _timer.Change(_delay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    // The window is about to close and still exists: this is the last reliable moment to read its placement.
    private void OnCloseApproved(object? sender, EventArgs e) => Capture();

    private void OnTimer()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (!_dispatcher.Post(() =>
        {
            Capture();
            _ = SaveLatestAsync();
        }))
        {
            _ = SaveLatestAsync();
        }
    }

    private void Capture()
    {
        WindowPlacement? placement;
        try
        {
            placement = _window.CapturePlacement();
        }
        catch (Exception ex)
        {
            LogCaptureFailed(_logger, _windowId, ex);
            return;
        }

        if (placement is { Bounds: { Width: > 0, Height: > 0 } bounds } &&
            double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height))
        {
            lock (_gate)
            {
                _latest = placement;
            }
        }
    }

    private async Task SaveLatestAsync()
    {
        WindowPlacement? placement;
        lock (_gate)
        {
            placement = _latest;
        }

        if (placement is null)
        {
            return;
        }

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                // A newer capture may have been saved while this call waited for the lock.
                placement = _latest;
                if (placement is null || placement == _saved)
                {
                    return;
                }
            }

            await _store.SaveAsync(_windowId, placement).ConfigureAwait(false);
            lock (_gate)
            {
                _saved = placement;
            }
        }
        catch (Exception ex)
        {
            LogSaveFailed(_logger, _windowId, ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    [LoggerMessage(EventId = 8401, Level = LogLevel.Warning, Message = "Saving the placement of window '{WindowId}' failed.")]
    private static partial void LogSaveFailed(ILogger logger, string windowId, Exception exception);

    [LoggerMessage(EventId = 8402, Level = LogLevel.Debug, Message = "Reading the placement of window '{WindowId}' failed.")]
    private static partial void LogCaptureFailed(ILogger logger, string windowId, Exception exception);
}
#endif
