using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.Logging;

namespace BlazorNativeSdk.Maui;

/// <summary>Maps the windows of a MAUI app to an application lifecycle state. Pure.</summary>
internal static class MauiLifecycleMapper
{
    /// <summary>
    /// Returns the application state for the given windows, or <see langword="null"/> when no window has been shown yet
    /// (windows that are still being created must not make a launching app look backgrounded).
    /// </summary>
    /// <param name="windows">For each open window: whether it was shown at least once, whether it is active, whether it is hidden or minimized.</param>
    public static AppLifecycleState? Map(IEnumerable<(bool HasBeenShown, bool IsActive, bool IsHidden)> windows)
    {
        var anyShown = false;
        var anyVisible = false;
        foreach (var (hasBeenShown, isActive, isHidden) in windows)
        {
            if (!hasBeenShown)
            {
                continue;
            }

            anyShown = true;
            if (isActive && !isHidden)
            {
                return AppLifecycleState.Active;
            }

            anyVisible |= !isHidden;
        }

        if (!anyShown)
        {
            return null;
        }

        return anyVisible ? AppLifecycleState.Inactive : AppLifecycleState.Background;
    }
}

/// <summary>
/// <see cref="IAppLifecycle"/> driven by the events of the app's MAUI windows. Used on Windows, and as a fallback on platforms
/// whose adapter does not provide a lifecycle.
/// </summary>
/// <remarks>
/// <list type="table">
/// <item><term><see cref="AppLifecycleState.Active"/></term><description>one of the app's windows is activated.</description></item>
/// <item><term><see cref="AppLifecycleState.Inactive"/></term><description>a window is visible but none is activated (another app has focus).</description></item>
/// <item><term><see cref="AppLifecycleState.Background"/></term><description>every window is minimized or hidden (MAUI <c>Stopped</c>).</description></item>
/// <item><term><see cref="AppLifecycleState.Terminating"/></term><description>the last window is being destroyed.</description></item>
/// </list>
/// Transitions are coalesced on the UI thread, so switching between two windows of the app does not report a transient
/// <see cref="AppLifecycleState.Inactive"/>. Desktop processes keep running in the background:
/// <see cref="BackgroundGuarantee"/> is <see cref="BackgroundExecutionGuarantee.NotRequired"/> on Windows and Mac Catalyst.
/// </remarks>
internal sealed class MauiWindowLifecycle : AppLifecycleBase, IDisposable, IAsyncDisposable
{
    private readonly MauiWindowTracker _tracker;
    private readonly MauiUIDispatcher _dispatcher;
    private int _recomputeQueued;
    private int _disposed;

    /// <summary>Creates the lifecycle.</summary>
    public MauiWindowLifecycle(MauiWindowTracker tracker, MauiUIDispatcher dispatcher, ILogger<MauiWindowLifecycle> logger, TimeProvider? timeProvider = null)
        : base(timeProvider, logger)
    {
        _tracker = tracker;
        _dispatcher = dispatcher;
        tracker.Enable();
        tracker.WindowAdded += OnWindowAdded;
        tracker.WindowRemoved += OnWindowRemoved;
        tracker.WindowChanged += OnWindowChanged;
    }

    /// <inheritdoc />
    protected override string AdapterName => "maui";

    /// <inheritdoc />
    public override BackgroundExecutionGuarantee BackgroundGuarantee =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS()
            ? BackgroundExecutionGuarantee.NotRequired
            : BackgroundExecutionGuarantee.BestEffort;

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _tracker.WindowAdded -= OnWindowAdded;
        _tracker.WindowRemoved -= OnWindowRemoved;
        _tracker.WindowChanged -= OnWindowChanged;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void OnWindowAdded(object? sender, TrackedWindow e) => QueueRecompute();

    private void OnWindowChanged(object? sender, TrackedWindowChange e)
    {
        if (e.Change != WindowChange.Bounds)
        {
            QueueRecompute();
        }
    }

    private void OnWindowRemoved(object? sender, TrackedWindow e)
    {
        if (_tracker.GetWindows().Count == 0)
        {
            ReportState(AppLifecycleState.Terminating, "last window destroyed");
        }
        else
        {
            QueueRecompute();
        }
    }

    private void QueueRecompute()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _recomputeQueued, 1) != 0)
        {
            return;
        }

        // Coalesce: deactivating one window and activating another arrive as separate events in the same dispatch cycle.
        if (!_dispatcher.Post(Recompute))
        {
            Recompute();
        }
    }

    private void Recompute()
    {
        Interlocked.Exchange(ref _recomputeQueued, 0);
        var windows = _tracker.GetWindows();
        if (windows.Count == 0)
        {
            return;
        }

        var state = MauiLifecycleMapper.Map(windows.Select(static w => (w.HasBeenShown, w.IsActive, w.IsHidden)));
        if (state is { } current)
        {
            ReportState(current, "window events");
        }
    }
}
