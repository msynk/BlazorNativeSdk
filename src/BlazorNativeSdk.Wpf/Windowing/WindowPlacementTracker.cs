using BlazorNativeSdk.Desktop;
using Microsoft.Extensions.Logging;

namespace BlazorNativeSdk.Wpf;

/// <summary>
/// Persists the placement of one window: debounced after it moves, resizes or changes state, and immediately when asked
/// (window closing, application exit). Saves are serialized, and unchanged placements are not written again.
/// </summary>
internal sealed partial class WindowPlacementTracker : IDisposable
{
    private readonly WpfNativeWindow _window;
    private readonly IWindowPlacementStore _store;
    private readonly TimeSpan _delay;
    private readonly Action<Task> _trackSave;
    private readonly ILogger _logger;
    private readonly ITimer _timer;
    private readonly Lock _gate = new();
    private WindowPlacement? _lastSaved;
    private NativeWindowState _lastShownState;
    private Task _saveChain = Task.CompletedTask;
    private int _disposed;

    public WindowPlacementTracker(WpfNativeWindow window, IWindowPlacementStore store, TimeProvider timeProvider, TimeSpan delay, Action<Task> trackSave, ILogger logger)
    {
        _window = window;
        _store = store;
        _delay = delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1);
        _trackSave = trackSave;
        _logger = logger;
        _timer = timeProvider.CreateTimer(static state => ((WindowPlacementTracker)state!).OnTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _window.BoundsChanged += OnWindowChanged;
        _window.StateChanged += OnWindowChanged;
    }

    /// <summary>Records <paramref name="placement"/> as already persisted, so restoring it does not trigger a save.</summary>
    public void SetBaseline(WindowPlacement? placement)
    {
        lock (_gate)
        {
            _lastSaved = placement;
            if (placement is { State: not NativeWindowState.Minimized })
            {
                _lastShownState = placement.State;
            }
        }
    }

    /// <summary>Saves the current placement now, unless it equals the last saved one.</summary>
    /// <returns>The save, which never faults (failures are logged).</returns>
    public Task SaveNow()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return Task.CompletedTask;
        }

        _ = _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        WindowPlacement? placement;
        try
        {
            placement = _window.GetPlacement();
        }
        catch (Exception ex)
        {
            LogCaptureFailed(_logger, _window.Id, ex);
            return Task.CompletedTask;
        }

        if (placement is null || placement.Bounds.Width <= 0 || placement.Bounds.Height <= 0)
        {
            return Task.CompletedTask;
        }

        Task save;
        lock (_gate)
        {
            // A minimized window reopens in the state it had before it was minimized (for example maximized).
            placement = placement.State == NativeWindowState.Minimized
                ? placement with { State = _lastShownState }
                : placement;
            if (placement == _lastSaved)
            {
                return Task.CompletedTask;
            }

            _lastSaved = placement;
            save = _saveChain = SaveAfterAsync(_saveChain, placement);
        }

        _trackSave(save);
        return save;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _window.BoundsChanged -= OnWindowChanged;
        _window.StateChanged -= OnWindowChanged;
        _timer.Dispose();
    }

    private void OnWindowChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var state = _window.State;
        if (state != NativeWindowState.Minimized)
        {
            lock (_gate)
            {
                _lastShownState = state;
            }
        }

        try
        {
            _ = _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnTimer() => _ = SaveNow();

    private async Task SaveAfterAsync(Task previous, WindowPlacement placement)
    {
        await previous.ConfigureAwait(false);
        try
        {
            await _store.SaveAsync(_window.Id, placement, CancellationToken.None).ConfigureAwait(false);
            LogSaved(_logger, _window.Id, placement.Bounds, placement.State);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (_lastSaved == placement)
                {
                    _lastSaved = null;
                }
            }

            LogSaveFailed(_logger, _window.Id, ex);
        }
    }

    [LoggerMessage(EventId = 7111, Level = LogLevel.Debug, Message = "Saved the placement of window '{WindowId}': {Bounds} {State}.")]
    private static partial void LogSaved(ILogger logger, string windowId, WindowBounds bounds, NativeWindowState state);

    [LoggerMessage(EventId = 7112, Level = LogLevel.Warning, Message = "Saving the placement of window '{WindowId}' failed.")]
    private static partial void LogSaveFailed(ILogger logger, string windowId, Exception exception);

    [LoggerMessage(EventId = 7113, Level = LogLevel.Warning, Message = "Reading the placement of window '{WindowId}' failed.")]
    private static partial void LogCaptureFailed(ILogger logger, string windowId, Exception exception);
}
