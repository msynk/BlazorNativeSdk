using System.ComponentModel;
using BlazorNativeSdk.Desktop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Maui.Controls;
using MauiApplication = Microsoft.Maui.Controls.Application;
using MauiWindow = Microsoft.Maui.Controls.Window;

namespace BlazorNativeSdk.Maui;

/// <summary>What changed about a tracked window.</summary>
internal enum WindowChange
{
    /// <summary>The window was activated or deactivated.</summary>
    Activation,

    /// <summary>The window was shown, hidden, stopped or resumed.</summary>
    Visibility,

    /// <summary>The window was minimized, maximized, restored or switched to full screen.</summary>
    State,

    /// <summary>The window moved or was resized.</summary>
    Bounds,
}

/// <summary>A window open request made by the window manager, matched to the MAUI window when the platform creates it.</summary>
/// <param name="Id">The window id.</param>
/// <param name="Options">The options passed to <see cref="IWindowManager.OpenAsync"/>.</param>
/// <param name="Placement">The persisted placement to restore, when any.</param>
internal sealed record WindowRequest(string Id, WindowOptions Options, WindowPlacement? Placement)
{
    /// <summary>Completed with the native window once the platform created the window.</summary>
    public TaskCompletionSource<INativeWindow> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>A MAUI window known to the tracker. Flags are written on the UI thread and may be read from any thread.</summary>
internal sealed class TrackedWindow
{
    private volatile bool _isActive;
    private volatile bool _isStopped;
    private volatile bool _hasBeenShown;
    private volatile bool _isClosed;

    internal TrackedWindow(MauiWindow window, string id, bool isMain, WindowRequest? request, nint platformHandle)
    {
        Window = window;
        Id = id;
        IsMain = isMain;
        Request = request;
        PlatformHandle = platformHandle;
    }

    /// <summary>The MAUI window.</summary>
    public MauiWindow Window { get; }

    /// <summary>The window id.</summary>
    public string Id { get; }

    /// <summary>Whether this is the app's first window.</summary>
    public bool IsMain { get; }

    /// <summary>The window manager request that opened the window, when any.</summary>
    public WindowRequest? Request { get; }

    /// <summary>The HWND on Windows, the <c>UIWindow</c> handle on Apple platforms, <c>0</c> elsewhere.</summary>
    public nint PlatformHandle { get; }

    /// <summary>Whether the window is activated (has focus).</summary>
    public bool IsActive { get => _isActive; internal set => _isActive = value; }

    /// <summary>Whether MAUI reported the window as stopped (hidden, minimized, scene in the background).</summary>
    public bool IsStopped { get => _isStopped; internal set => _isStopped = value; }

    /// <summary>Whether the window has been activated or shown at least once (windows that are still being created are ignored by the lifecycle).</summary>
    public bool HasBeenShown { get => _hasBeenShown; internal set => _hasBeenShown = value; }

    /// <summary>Whether the window was destroyed.</summary>
    public bool IsClosed { get => _isClosed; internal set => _isClosed = value; }

    /// <summary>Whether the window is hidden or minimized (Windows probes the native window; other platforms rely on MAUI events).</summary>
    public bool IsHidden
    {
        get
        {
            if (IsClosed || IsStopped)
            {
                return true;
            }

#if WINDOWS
            var hwnd = PlatformHandle;
            return hwnd != 0 && (Interop.NativeMethods.IsIconic(hwnd) || !Interop.NativeMethods.IsWindowVisible(hwnd));
#else
            return false;
#endif
        }
    }

#if WINDOWS
    /// <summary>The last <c>WM_SIZE</c> type seen (<c>SIZE_RESTORED</c>, <c>SIZE_MINIMIZED</c>, <c>SIZE_MAXIMIZED</c>), or -1.</summary>
    internal int LastSizeType { get; set; } = -1;
#endif

    /// <inheritdoc />
    public override string ToString() => Id;
}

/// <summary>Event data for <see cref="MauiWindowTracker.WindowChanged"/>.</summary>
/// <param name="Window">The window.</param>
/// <param name="Change">What changed.</param>
internal sealed record TrackedWindowChange(TrackedWindow Window, WindowChange Change);

/// <summary>
/// Discovers every MAUI window of the application (the main window, windows opened through <see cref="IWindowManager"/> and
/// windows the app opens itself), assigns ids and translates MAUI window events into a single stream. All events are raised
/// on the UI thread.
/// </summary>
/// <remarks>
/// Windows are discovered through <see cref="Element.ChildAdded"/> on the application and are reported once their handler
/// (and therefore their platform window) exists, which on Windows is before the window is first activated, so placement can
/// be applied without flicker. Platform lifecycle hooks call <see cref="EnsureAttached"/> as soon as the application exists.
/// </remarks>
internal sealed partial class MauiWindowTracker : IDisposable, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly List<TrackedWindow> _windows = [];
    private readonly Dictionary<MauiWindow, TrackedWindow> _byWindow = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<nint, TrackedWindow> _byHandle = [];
    private readonly HashSet<MauiWindow> _observed = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MauiWindow, WindowRequest> _requests = new(ReferenceEqualityComparer.Instance);
    private readonly IOptions<MauiNativeOptions> _options;
    private readonly ILogger<MauiWindowTracker> _logger;
    private MauiApplication? _application;
    private bool _mainAssigned;
    private int _sequence;
    private int _enabled;
    private int _disposed;

    /// <summary>Creates the tracker.</summary>
    public MauiWindowTracker(IOptions<MauiNativeOptions> options, ILogger<MauiWindowTracker> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>Raised when a window's platform window exists.</summary>
    public event EventHandler<TrackedWindow>? WindowAdded;

    /// <summary>Raised when a window is destroyed.</summary>
    public event EventHandler<TrackedWindow>? WindowRemoved;

    /// <summary>Raised when a window's activation, visibility, state or bounds change.</summary>
    public event EventHandler<TrackedWindowChange>? WindowChanged;

    /// <summary><see langword="true"/> once at least one window has been tracked.</summary>
    public bool HasTrackedWindows
    {
        get
        {
            lock (_gate)
            {
                return _mainAssigned;
            }
        }
    }

    /// <summary>Turns tracking on. Called by the components that consume it (lifecycle, window manager, host bridge).</summary>
    public void Enable() => Interlocked.Exchange(ref _enabled, 1);

    /// <summary>Returns the open windows in creation order.</summary>
    public IReadOnlyList<TrackedWindow> GetWindows()
    {
        lock (_gate)
        {
            return [.. _windows];
        }
    }

    /// <summary>Finds an open window by its MAUI window.</summary>
    public TrackedWindow? Find(MauiWindow window)
    {
        lock (_gate)
        {
            return _byWindow.GetValueOrDefault(window);
        }
    }

    /// <summary>Finds an open window by id.</summary>
    public TrackedWindow? Find(string id)
    {
        lock (_gate)
        {
            return _windows.FirstOrDefault(w => string.Equals(w.Id, id, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The native handle of the main window, falling back to the first open window, or <c>0</c> when no window is open.
    /// Safe to call from any thread.
    /// </summary>
    public nint GetMainWindowHandle()
    {
        lock (_gate)
        {
            var main = _windows.FirstOrDefault(static w => w.IsMain && !w.IsClosed) ?? _windows.FirstOrDefault(static w => !w.IsClosed);
            return main?.PlatformHandle ?? 0;
        }
    }

    /// <summary>Registers the request that is about to open <paramref name="window"/>, so it gets the requested id.</summary>
    public void Expect(MauiWindow window, WindowRequest request)
    {
        lock (_gate)
        {
            _requests[window] = request;
        }
    }

    /// <summary>Forgets a request whose window was never created.</summary>
    public void Forget(MauiWindow window)
    {
        lock (_gate)
        {
            _requests.Remove(window);
        }
    }

    /// <summary>
    /// Attaches to <see cref="MauiApplication.Current"/> (once it exists) and tracks its windows. Must be called on the UI
    /// thread; cheap and idempotent.
    /// </summary>
    public void EnsureAttached()
    {
        if (Volatile.Read(ref _enabled) == 0 || Volatile.Read(ref _disposed) != 0 || MauiApplication.Current is not { } application)
        {
            return;
        }

        if (!ReferenceEquals(application, _application))
        {
            if (_application is { } previous)
            {
                previous.ChildAdded -= OnApplicationChildAdded;
                previous.ChildRemoved -= OnApplicationChildRemoved;
            }

            _application = application;
            application.ChildAdded += OnApplicationChildAdded;
            application.ChildRemoved += OnApplicationChildRemoved;
        }

        foreach (var window in application.Windows.ToArray())
        {
            Observe(window);
        }
    }

    /// <summary>Raises <see cref="WindowChanged"/> for a platform notification about a tracked window.</summary>
    public void NotifyChanged(TrackedWindow window, WindowChange change)
    {
        if (!window.IsClosed)
        {
            Raise(WindowChanged, new TrackedWindowChange(window, change));
        }
    }

#if WINDOWS
    private const uint WM_SIZE = 0x0005;
    private const uint WM_SHOWWINDOW = 0x0018;
    private const uint WM_WINDOWPOSCHANGED = 0x0047;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_HIDEWINDOW = 0x0080;

    /// <summary>Handles a window message of a MAUI window (raised by MAUI for every message; must be cheap).</summary>
    public unsafe void OnPlatformMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message is not (WM_SIZE or WM_SHOWWINDOW or WM_WINDOWPOSCHANGED))
        {
            return;
        }

        TrackedWindow? window;
        lock (_gate)
        {
            if (!_byHandle.TryGetValue(hwnd, out window))
            {
                return;
            }
        }

        switch (message)
        {
            case WM_SIZE:
                var sizeType = (int)wParam;
                if (sizeType is 0 or 1 or 2 && sizeType != window.LastSizeType)
                {
                    var wasMinimized = window.LastSizeType == 1;
                    window.LastSizeType = sizeType;
                    NotifyChanged(window, WindowChange.State);
                    if (wasMinimized || sizeType == 1)
                    {
                        NotifyChanged(window, WindowChange.Visibility);
                    }
                }

                break;
            case WM_SHOWWINDOW:
                NotifyChanged(window, WindowChange.Visibility);
                break;
            case WM_WINDOWPOSCHANGED when lParam != 0:
                var flags = ((Interop.WINDOWPOS*)lParam)->flags;
                if ((flags & (SWP_NOMOVE | SWP_NOSIZE)) != (SWP_NOMOVE | SWP_NOSIZE))
                {
                    NotifyChanged(window, WindowChange.Bounds);
                }

                if ((flags & (SWP_SHOWWINDOW | SWP_HIDEWINDOW)) != 0)
                {
                    NotifyChanged(window, WindowChange.Visibility);
                }

                break;
        }
    }
#endif

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        MauiWindow[] observed;
        lock (_gate)
        {
            observed = [.. _observed];
            _observed.Clear();
            _windows.Clear();
            _byWindow.Clear();
            _byHandle.Clear();
            _requests.Clear();
        }

        foreach (var window in observed)
        {
            Unsubscribe(window);
        }

        if (_application is { } application)
        {
            application.ChildAdded -= OnApplicationChildAdded;
            application.ChildRemoved -= OnApplicationChildRemoved;
            _application = null;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void OnApplicationChildAdded(object? sender, ElementEventArgs e)
    {
        if (e.Element is MauiWindow window)
        {
            Observe(window);
        }
    }

    private void OnApplicationChildRemoved(object? sender, ElementEventArgs e)
    {
        if (e.Element is MauiWindow window)
        {
            Remove(window, "removed from the application");
        }
    }

    private void Observe(MauiWindow window)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_observed.Add(window))
            {
                return;
            }
        }

        window.HandlerChanged += OnHandlerChanged;
        window.Activated += OnActivated;
        window.Deactivated += OnDeactivated;
        window.Stopped += OnStopped;
        window.Resumed += OnResumed;
        window.Destroying += OnDestroying;
        window.SizeChanged += OnSizeChanged;
        window.PropertyChanged += OnPropertyChanged;

        if (window.Handler is not null)
        {
            TryAdd(window);
        }
    }

    private void Unsubscribe(MauiWindow window)
    {
        window.HandlerChanged -= OnHandlerChanged;
        window.Activated -= OnActivated;
        window.Deactivated -= OnDeactivated;
        window.Stopped -= OnStopped;
        window.Resumed -= OnResumed;
        window.Destroying -= OnDestroying;
        window.SizeChanged -= OnSizeChanged;
        window.PropertyChanged -= OnPropertyChanged;
    }

    private void TryAdd(MauiWindow window)
    {
        TrackedWindow tracked;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || _byWindow.ContainsKey(window) || !_observed.Contains(window))
            {
                return;
            }

            _requests.Remove(window, out var request);
            var isMain = request is null && !_mainAssigned;
            var id = request?.Id ?? (isMain ? _options.Value.MainWindowId : MauiWindowIds.CreateSequential(++_sequence));
            while (_windows.Any(w => string.Equals(w.Id, id, StringComparison.Ordinal)))
            {
                // An id can only be taken twice when the app opened windows with MAUI directly; never reuse it.
                id = MauiWindowIds.CreateSequential(++_sequence);
            }

            _mainAssigned = true;
            tracked = new TrackedWindow(window, id, isMain, request, GetPlatformHandle(window))
            {
                IsActive = window.IsActivated,
                HasBeenShown = window.IsActivated,
            };
            _windows.Add(tracked);
            _byWindow[window] = tracked;
            if (tracked.PlatformHandle != 0)
            {
                _byHandle[tracked.PlatformHandle] = tracked;
            }
        }

        LogWindowTracked(_logger, tracked.Id, tracked.IsMain);
        Raise(WindowAdded, tracked);
    }

    private void Remove(MauiWindow window, string reason)
    {
        TrackedWindow? tracked;
        bool wasObserved;
        lock (_gate)
        {
            wasObserved = _observed.Remove(window);
            _requests.Remove(window);
            if (_byWindow.Remove(window, out tracked))
            {
                _windows.Remove(tracked);
                if (tracked.PlatformHandle != 0)
                {
                    _byHandle.Remove(tracked.PlatformHandle);
                }
            }
        }

        if (wasObserved)
        {
            Unsubscribe(window);
        }

        if (tracked is null)
        {
            return;
        }

        tracked.IsClosed = true;
        tracked.IsActive = false;
        LogWindowRemoved(_logger, tracked.Id, reason);
        Raise(WindowRemoved, tracked);
    }

    private void OnHandlerChanged(object? sender, EventArgs e)
    {
        if (sender is not MauiWindow window)
        {
            return;
        }

        if (window.Handler is null)
        {
            Remove(window, "handler disconnected");
        }
        else
        {
            TryAdd(window);
        }
    }

    private void OnActivated(object? sender, EventArgs e) => Update(sender, static w =>
    {
        w.IsActive = true;
        w.HasBeenShown = true;
        w.IsStopped = false;
    }, WindowChange.Activation);

    private void OnDeactivated(object? sender, EventArgs e) => Update(sender, static w => w.IsActive = false, WindowChange.Activation);

    private void OnStopped(object? sender, EventArgs e) => Update(sender, static w =>
    {
        w.IsStopped = true;
        w.IsActive = false;
    }, WindowChange.Visibility);

    private void OnResumed(object? sender, EventArgs e) => Update(sender, static w =>
    {
        w.IsStopped = false;
        w.HasBeenShown = true;
    }, WindowChange.Visibility);

    private void OnDestroying(object? sender, EventArgs e)
    {
        if (sender is MauiWindow window)
        {
            Remove(window, "destroyed");
        }
    }

    private void OnSizeChanged(object? sender, EventArgs e) => Update(sender, static _ => { }, WindowChange.Bounds);

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MauiWindow.X) or nameof(MauiWindow.Y))
        {
            Update(sender, static _ => { }, WindowChange.Bounds);
        }
    }

    private void Update(object? sender, Action<TrackedWindow> apply, WindowChange change)
    {
        if (sender is not MauiWindow window)
        {
            return;
        }

        TrackedWindow? tracked;
        lock (_gate)
        {
            tracked = _byWindow.GetValueOrDefault(window);
        }

        if (tracked is null)
        {
            return;
        }

        apply(tracked);
        NotifyChanged(tracked, change);
    }

    private static nint GetPlatformHandle(MauiWindow window)
    {
#if WINDOWS
        return window.Handler?.PlatformView is Microsoft.UI.Xaml.Window platformWindow
            ? WinRT.Interop.WindowNative.GetWindowHandle(platformWindow)
            : 0;
#elif IOS || MACCATALYST
        return window.Handler?.PlatformView is UIKit.UIWindow platformWindow ? platformWindow.Handle.Handle : 0;
#else
        _ = window;
        return 0;
#endif
    }

    private void Raise<T>(EventHandler<T>? handler, T args)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList().Cast<EventHandler<T>>())
        {
            try
            {
                subscriber(this, args);
            }
            catch (Exception ex)
            {
                LogSubscriberFailed(_logger, ex);
            }
        }
    }

    [LoggerMessage(EventId = 8001, Level = LogLevel.Debug, Message = "Tracking MAUI window '{WindowId}' (main: {IsMain}).")]
    private static partial void LogWindowTracked(ILogger logger, string windowId, bool isMain);

    [LoggerMessage(EventId = 8002, Level = LogLevel.Debug, Message = "MAUI window '{WindowId}' is no longer tracked ({Reason}).")]
    private static partial void LogWindowRemoved(ILogger logger, string windowId, string reason);

    [LoggerMessage(EventId = 8003, Level = LogLevel.Error, Message = "A window tracking subscriber failed.")]
    private static partial void LogSubscriberFailed(ILogger logger, Exception exception);
}
