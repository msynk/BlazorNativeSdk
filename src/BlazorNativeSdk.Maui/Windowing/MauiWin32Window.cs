#if WINDOWS
using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.Maui.Interop;
using BlazorNativeSdk.WindowsPlatform;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using MauiApplication = Microsoft.Maui.Controls.Application;

namespace BlazorNativeSdk.Maui;

/// <summary>
/// <see cref="INativeWindow"/> for a MAUI window on Windows. State, bounds, focus and visibility use the Win32 controller of the
/// Windows adapter (DIP coordinates consistent with <see cref="IScreenService"/>); the title goes through the MAUI window, and
/// full screen and always-on-top use the WinUI <see cref="AppWindow"/> presenters so the framework keeps its own state.
/// </summary>
/// <remarks>
/// Closing is cancelable for every close: user closes arrive through <see cref="AppWindow.Closing"/>, programmatic closes
/// through <see cref="CloseAsync"/>, which raises <see cref="INativeWindow.Closing"/> before asking MAUI to close the window.
/// </remarks>
internal sealed partial class MauiWin32Window : Win32NativeWindowBase, IMauiNativeWindow
{
    private readonly MauiUIDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly AppWindow? _appWindow;
    private readonly OverlappedPresenter? _overlapped;
    private nint _hwnd;
    private int _closeApproved;
    private int _closed;
    private volatile bool _isFullScreen;
    private WindowPlacement? _beforeFullScreen;
    private NativeWindowState? _lastReportedState;
    private bool _lastReportedVisible;

    /// <summary>Creates the window wrapper. Must be called on the UI thread.</summary>
    public MauiWin32Window(TrackedWindow tracked, MauiUIDispatcher dispatcher, ILogger logger)
        : base(tracked.Id, tracked.IsMain)
    {
        Tracked = tracked;
        _dispatcher = dispatcher;
        _logger = logger;
        _hwnd = tracked.PlatformHandle;
        _lastReportedVisible = IsVisible;
        if (tracked.Window.Handler?.PlatformView is Microsoft.UI.Xaml.Window platformWindow && platformWindow.AppWindow is { } appWindow)
        {
            _appWindow = appWindow;
            _overlapped = appWindow.Presenter as OverlappedPresenter;
            _isFullScreen = appWindow.Presenter?.Kind == AppWindowPresenterKind.FullScreen;
            appWindow.Closing += OnAppWindowClosing;
            appWindow.Changed += OnAppWindowChanged;
        }
    }

    /// <inheritdoc />
    public TrackedWindow Tracked { get; }

    /// <inheritdoc />
    public event EventHandler? CloseApproved;

    /// <inheritdoc />
    public override string Title => Tracked.Window.Title is { Length: > 0 } title ? title : base.Title;

    /// <inheritdoc />
    public override NativeWindowState State
    {
        get
        {
            var state = base.State;
            return _isFullScreen && state != NativeWindowState.Minimized ? NativeWindowState.FullScreen : state;
        }
    }

    /// <inheritdoc />
    protected override nint WindowHandle => Volatile.Read(ref _hwnd);

    /// <inheritdoc />
    public override ValueTask<NativeResult> SetTitleAsync(string title, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(title);
        return RunAsync(_ =>
        {
            // MAUI owns the title (it re-applies Window.Title), so set it there rather than with SetWindowText.
            Tracked.Window.Title = title;
            return NativeResult.Ok;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public override ValueTask<NativeResult> SetStateAsync(NativeWindowState state, CancellationToken cancellationToken = default) =>
        RunAsync(controller => ApplyState(controller, state), cancellationToken);

    /// <inheritdoc />
    public override ValueTask<NativeResult> SetAlwaysOnTopAsync(bool alwaysOnTop, CancellationToken cancellationToken = default) =>
        RunAsync(controller =>
        {
            if (_overlapped is null)
            {
                return controller.SetAlwaysOnTop(alwaysOnTop);
            }

            _overlapped.IsAlwaysOnTop = alwaysOnTop;
            return NativeResult.Ok;
        }, cancellationToken);

    /// <inheritdoc />
    public override ValueTask<NativeResult> CloseAsync(CancellationToken cancellationToken = default) =>
        RunAsync(controller =>
        {
            if (Volatile.Read(ref _closeApproved) == 0)
            {
                if (OnClosing())
                {
                    return NativeError.Cancelled(NativeCapabilities.Windows, $"A Closing handler kept window '{Id}' open.");
                }

                ApproveClose();
            }

            if (MauiApplication.Current is not { } application)
            {
                return controller.Close();
            }

            application.CloseWindow(Tracked.Window);
            return NativeResult.Ok;
        }, cancellationToken);

    /// <inheritdoc />
    public override WindowPlacement? GetPlacement() => CapturePlacement();

    /// <inheritdoc />
    /// <remarks>A full-screen placement restores the bounds first and then uses the WinUI full-screen presenter.</remarks>
    public override ValueTask<NativeResult> ApplyPlacementAsync(WindowPlacement placement, bool applyState = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return RunAsync(controller =>
        {
            var fullScreen = placement.State == NativeWindowState.FullScreen;
            var result = controller.ApplyPlacement(fullScreen ? placement with { State = NativeWindowState.Normal } : placement, applyState);
            return result.IsSuccess && applyState && fullScreen ? ApplyState(controller, NativeWindowState.FullScreen) : result;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public WindowPlacement? CapturePlacement()
    {
        if (WindowHandle == 0 || !NativeMethods.IsWindow(WindowHandle))
        {
            return null;
        }

        if (_isFullScreen)
        {
            var basis = _beforeFullScreen ?? base.GetPlacement();
            return basis is null ? null : basis with { State = NativeWindowState.FullScreen };
        }

        return base.GetPlacement();
    }

    /// <summary>Moves and resizes the window without changing its state or showing it. UI thread.</summary>
    public NativeResult ApplyRestoredBounds(WindowPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return Controller is { } controller
            ? controller.ApplyPlacement(placement with { State = NativeWindowState.Normal }, applyState: false)
            : Gone();
    }

    /// <summary>Moves and resizes the window. UI thread.</summary>
    public NativeResult ApplyBounds(WindowBounds bounds) => Controller is { } controller ? controller.SetBounds(bounds) : Gone();

    /// <summary>Applies frame options before the window is first shown. UI thread.</summary>
    /// <param name="resizable">Whether the user can resize (and maximize) the window.</param>
    /// <param name="alwaysOnTop">Whether the window stays above other windows.</param>
    /// <param name="owner">The HWND of the owner window, or <c>0</c>.</param>
    public void ConfigureFrame(bool resizable, bool alwaysOnTop, nint owner)
    {
        if (_overlapped is not null)
        {
            if (!resizable)
            {
                _overlapped.IsResizable = false;
                _overlapped.IsMaximizable = false;
            }

            if (alwaysOnTop)
            {
                _overlapped.IsAlwaysOnTop = true;
            }
        }
        else if (alwaysOnTop && Controller is { } controller)
        {
            _ = controller.SetAlwaysOnTop(true);
        }

        if (owner != 0 && WindowHandle != 0)
        {
            // An owned top-level window stays above its owner and is minimized with it (the documented Win32 mechanism,
            // also the supported way to own WinUI windows).
            NativeMethods.SetWindowLongPtr(WindowHandle, NativeMethods.GWLP_HWNDPARENT, owner);
        }
    }

    /// <summary>Removes the owner relationship (before the owner closes, so Windows does not destroy this window behind WinUI's back). UI thread.</summary>
    public void ClearOwner()
    {
        if (WindowHandle != 0)
        {
            NativeMethods.SetWindowLongPtr(WindowHandle, NativeMethods.GWLP_HWNDPARENT, 0);
        }
    }

    /// <inheritdoc />
    public void NotifyStateChanged()
    {
        var state = State;
        if (_lastReportedState != state)
        {
            _lastReportedState = state;
            OnStateChanged();
        }
    }

    /// <inheritdoc />
    public void NotifyVisibilityChanged()
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            return;
        }

        var visible = IsVisible;
        if (_lastReportedVisible != visible)
        {
            _lastReportedVisible = visible;
            OnVisibilityChanged();
        }
    }

    /// <inheritdoc />
    public void NotifyBoundsChanged() => OnBoundsChanged();

    /// <inheritdoc />
    public void NotifyFocusChanged() => OnFocusChanged();

    /// <inheritdoc />
    public void NotifyClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        if (_appWindow is not null)
        {
            _appWindow.Closing -= OnAppWindowClosing;
            _appWindow.Changed -= OnAppWindowChanged;
        }

        Volatile.Write(ref _hwnd, 0);
        OnClosed();
    }

    /// <inheritdoc />
    protected override Task InvokeOnUIThreadAsync(Func<Task> action) => _dispatcher.InvokeAsync(action);

    /// <inheritdoc />
    protected override void OnStateChanged() => Guard(base.OnStateChanged, nameof(StateChanged));

    /// <inheritdoc />
    protected override void OnBoundsChanged() => Guard(base.OnBoundsChanged, nameof(BoundsChanged));

    /// <inheritdoc />
    protected override void OnFocusChanged() => Guard(base.OnFocusChanged, nameof(FocusChanged));

    /// <inheritdoc />
    protected override void OnVisibilityChanged() => Guard(base.OnVisibilityChanged, nameof(VisibilityChanged));

    /// <inheritdoc />
    protected override void OnClosed() => Guard(base.OnClosed, nameof(Closed));

    /// <inheritdoc />
    protected override bool OnClosing()
    {
        try
        {
            return base.OnClosing();
        }
        catch (Exception ex)
        {
            LogHandlerFailed(_logger, nameof(Closing), Id, ex);
            return false;
        }
    }

    private NativeResult ApplyState(Win32WindowController controller, NativeWindowState state)
    {
        if (_appWindow is null)
        {
            return controller.SetState(state);
        }

        switch (state)
        {
            case NativeWindowState.FullScreen:
                if (!_isFullScreen)
                {
                    if (controller.State == NativeWindowState.Minimized)
                    {
                        _ = controller.SetState(NativeWindowState.Normal);
                    }

                    _beforeFullScreen = controller.ToPlacement();
                    _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                    _isFullScreen = true;
                    NotifyStateChanged();
                }

                return NativeResult.Ok;

            case NativeWindowState.Minimized:
                // Restoring a minimized full-screen window returns it to full screen, as in other apps.
                return controller.SetState(NativeWindowState.Minimized);

            default:
                if (_isFullScreen)
                {
                    ExitFullScreen();
                }

                return controller.SetState(state);
        }
    }

    private void ExitFullScreen()
    {
        if (_appWindow is null)
        {
            return;
        }

        if (_overlapped is not null)
        {
            _appWindow.SetPresenter(_overlapped);
        }
        else
        {
            _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        }

        _isFullScreen = false;
        _beforeFullScreen = null;
    }

    private void ApproveClose()
    {
        Volatile.Write(ref _closeApproved, 1);
        Guard(() => CloseApproved?.Invoke(this, EventArgs.Empty), nameof(CloseApproved));
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (Volatile.Read(ref _closeApproved) != 0)
        {
            return;
        }

        if (OnClosing())
        {
            args.Cancel = true;
            return;
        }

        ApproveClose();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPresenterChange)
        {
            return;
        }

        var isFullScreen = sender.Presenter?.Kind == AppWindowPresenterKind.FullScreen;
        if (isFullScreen != _isFullScreen)
        {
            _isFullScreen = isFullScreen;
            if (!isFullScreen)
            {
                _beforeFullScreen = null;
            }

            NotifyStateChanged();
        }
    }

    private void Guard(Action action, string eventName)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            LogHandlerFailed(_logger, eventName, Id, ex);
        }
    }

    private static NativeError Gone() =>
        NativeError.Unavailable(NativeCapabilities.Windows, NativeErrorCodes.NotFound, "The window no longer exists.");

    [LoggerMessage(EventId = 8301, Level = LogLevel.Error, Message = "A {Event} handler of window '{WindowId}' failed.")]
    private static partial void LogHandlerFailed(ILogger logger, string @event, string windowId, Exception exception);
}
#endif
