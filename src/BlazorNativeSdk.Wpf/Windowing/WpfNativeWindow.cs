using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.WindowsPlatform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlazorNativeSdk.Wpf;

/// <summary>
/// An <see cref="INativeWindow"/> over a WPF <see cref="System.Windows.Window"/>. Created by <see cref="WpfWindowManager"/>
/// for the windows it opens and for windows the app attaches (<see cref="WpfWindowManager.AttachMainWindow"/>).
/// </summary>
/// <remarks>
/// <para>
/// Members may be called from any thread; operations run on the window's <see cref="Dispatcher"/>. Bounds, state, focus
/// and visibility are read from the native window, so they are also accurate from background threads, and bounds use the
/// SDK's DIP convention (physical pixels divided by the scale of the window's monitor), comparable with
/// <see cref="IScreenService"/> displays.
/// </para>
/// <para>
/// Title, topmost, visibility and minimize/maximize go through WPF (<see cref="Window.Title"/>, <see cref="Window.Topmost"/>,
/// <see cref="Window.Show"/>/<see cref="Window.Hide"/>, <see cref="Window.WindowState"/>) so WPF's own state stays
/// consistent. WPF has no full-screen state: <see cref="NativeWindowState.FullScreen"/> switches the window to a borderless
/// popup covering its monitor (see <see cref="Win32WindowController"/>), while <see cref="Window.WindowState"/> stays
/// <see cref="WindowState.Normal"/>.
/// </para>
/// <para>
/// Events are raised on the UI thread. Exceptions thrown by handlers are logged instead of crashing the dispatcher.
/// </para>
/// </remarks>
public sealed partial class WpfNativeWindow : Win32NativeWindowBase
{
    private const int WM_WINDOWPOSCHANGED = 0x0047;

    private readonly ILogger _logger;
    private nint _handle;
    private int _closed;
    private int _detached;
    private string _title;
    private HwndSource? _source;
    private NativeWindowState _reportedState;
    private bool _reportedVisible;

    internal WpfNativeWindow(Window window, string id, bool isMainWindow, ILogger? logger)
        : base(id, isMainWindow)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.VerifyAccess();
        Window = window;
        Dispatcher = window.Dispatcher;
        _logger = logger ?? NullLogger.Instance;
        _title = window.Title ?? string.Empty;
        _handle = new WindowInteropHelper(window).Handle;
        if (_handle != 0)
        {
            var controller = new Win32WindowController(_handle);
            _reportedState = controller.State;
            _reportedVisible = controller.IsVisible;
            HookSource(_handle);
        }

        window.SourceInitialized += OnSourceInitialized;
        window.StateChanged += OnWpfStateChanged;
        window.LocationChanged += OnWpfLocationChanged;
        window.SizeChanged += OnWpfSizeChanged;
        window.Activated += OnWpfActivationChanged;
        window.Deactivated += OnWpfActivationChanged;
        window.IsVisibleChanged += OnWpfIsVisibleChanged;
        window.Closing += OnWpfClosing;
        window.Closed += OnWpfClosed;
    }

    /// <summary>The WPF window. Access it on its <see cref="Dispatcher"/> thread only.</summary>
    public Window Window { get; }

    /// <summary>The dispatcher that owns <see cref="Window"/>.</summary>
    public Dispatcher Dispatcher { get; }

    /// <summary><see langword="true"/> once the window has closed. Operations then return <see cref="NativeErrorCodes.NotFound"/>.</summary>
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <inheritdoc />
    /// <remarks>Before the native window exists, this is the title the WPF window was created with.</remarks>
    public override string Title => WindowHandle != 0 ? base.Title : Volatile.Read(ref _title);

    /// <inheritdoc />
    protected override nint WindowHandle => Volatile.Read(ref _handle);

    /// <summary>Raised on the UI thread once the native window exists (WPF <c>SourceInitialized</c>), before it is shown.</summary>
    internal event EventHandler? HandleCreated;

    /// <inheritdoc />
    public override ValueTask<NativeResult> SetTitleAsync(string title, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(title);
        return RunOnWindowAsync((window, _) =>
        {
            window.Title = title;
            Volatile.Write(ref _title, title);
            return NativeResult.Ok;
        }, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>Before the native window exists, the bounds are applied through <see cref="Window.Left"/>, <see cref="Window.Top"/>,
    /// <see cref="FrameworkElement.Width"/> and <see cref="FrameworkElement.Height"/>.</remarks>
    public override ValueTask<NativeResult> SetBoundsAsync(WindowBounds bounds, CancellationToken cancellationToken = default) =>
        RunOnWindowAsync((window, controller) =>
        {
            if (controller is not null)
            {
                return controller.SetBounds(bounds);
            }

            if (bounds.Width <= 0 || bounds.Height <= 0 || !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
                !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height))
            {
                return NativeError.Failed(NativeCapabilities.Windows, NativeErrorCodes.InvalidArgument, "Window bounds must be finite with a positive width and height.");
            }

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.SizeToContent = SizeToContent.Manual;
            window.Left = bounds.X;
            window.Top = bounds.Y;
            window.Width = bounds.Width;
            window.Height = bounds.Height;
            return NativeResult.Ok;
        }, cancellationToken);

    /// <inheritdoc />
    /// <remarks>A hidden window is shown through WPF in the requested state.</remarks>
    public override ValueTask<NativeResult> SetStateAsync(NativeWindowState state, CancellationToken cancellationToken = default) =>
        RunOnWindowAsync((window, controller) =>
        {
            if (!Enum.IsDefined(state))
            {
                return NativeError.Failed(NativeCapabilities.Windows, NativeErrorCodes.InvalidArgument, $"Unknown window state '{state}'.");
            }

            if (!window.IsVisible)
            {
                if (state != NativeWindowState.FullScreen && controller?.IsFullScreen != true)
                {
                    window.WindowState = WpfWindowSettings.ToWpfState(state);
                }

                window.Show();
                controller = Controller;
            }

            if (controller is null)
            {
                return NotReady();
            }

            // The controller restores a window that was maximized before it was minimized straight to its normal bounds.
            return controller.State == state ? NativeResult.Ok : controller.SetState(state);
        }, cancellationToken);

    /// <inheritdoc />
    public override ValueTask<NativeResult> SetAlwaysOnTopAsync(bool alwaysOnTop, CancellationToken cancellationToken = default) =>
        RunOnWindowAsync((window, _) =>
        {
            window.Topmost = alwaysOnTop;
            return NativeResult.Ok;
        }, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Shows a hidden window, restores a minimized one and brings it to the front. When Windows' foreground lock refuses
    /// activation, the taskbar button flashes and <see cref="NativeResultStatus.Unavailable"/> is returned.
    /// </remarks>
    public override ValueTask<NativeResult> FocusAsync(CancellationToken cancellationToken = default) =>
        RunOnWindowAsync((window, controller) =>
        {
            if (!window.IsVisible)
            {
                window.Show();
                controller = Controller;
            }

            return controller?.Focus() ?? NotReady();
        }, cancellationToken);

    /// <inheritdoc />
    public override ValueTask<NativeResult> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) =>
        RunOnWindowAsync((window, _) =>
        {
            if (visible && !window.IsVisible)
            {
                window.Show();
            }
            else if (!visible && window.IsVisible)
            {
                window.Hide();
            }

            return NativeResult.Ok;
        }, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Posts <c>WM_CLOSE</c>, so WPF raises <see cref="Window.Closing"/> (and <see cref="INativeWindow.Closing"/>) on the next
    /// message loop iteration and handlers may cancel. A window that was never shown is closed through <see cref="Window.Close"/>.
    /// </remarks>
    public override ValueTask<NativeResult> CloseAsync(CancellationToken cancellationToken = default) =>
        RunOnWindowAsync((window, controller) =>
        {
            if (controller is not null)
            {
                return controller.Close();
            }

            WpfDispatch.Post(Dispatcher, window.Close);
            return NativeResult.Ok;
        }, cancellationToken);

    /// <inheritdoc />
    protected override Task InvokeOnUIThreadAsync(Func<Task> action) => WpfDispatch.InvokeAsync(Dispatcher, action);

    /// <inheritdoc />
    protected override void OnStateChanged()
    {
        try
        {
            base.OnStateChanged();
        }
        catch (Exception ex)
        {
            LogHandlerFailed(_logger, nameof(StateChanged), Id, ex);
        }
    }

    /// <inheritdoc />
    protected override void OnBoundsChanged()
    {
        try
        {
            base.OnBoundsChanged();
        }
        catch (Exception ex)
        {
            LogHandlerFailed(_logger, nameof(BoundsChanged), Id, ex);
        }
    }

    /// <inheritdoc />
    protected override void OnFocusChanged()
    {
        try
        {
            base.OnFocusChanged();
        }
        catch (Exception ex)
        {
            LogHandlerFailed(_logger, nameof(FocusChanged), Id, ex);
        }
    }

    /// <inheritdoc />
    protected override void OnVisibilityChanged()
    {
        try
        {
            base.OnVisibilityChanged();
        }
        catch (Exception ex)
        {
            LogHandlerFailed(_logger, nameof(VisibilityChanged), Id, ex);
        }
    }

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

    /// <inheritdoc />
    protected override void OnClosed()
    {
        try
        {
            base.OnClosed();
        }
        catch (Exception ex)
        {
            LogHandlerFailed(_logger, nameof(Closed), Id, ex);
        }
    }

    /// <summary>The controller of the native window (UI thread), or <see langword="null"/> before it exists or after it closed.</summary>
    internal Win32WindowController? NativeController => IsClosed ? null : Controller;

    /// <summary>Raises <see cref="INativeWindow.StateChanged"/> when the observed state differs from the last reported one (UI thread).</summary>
    internal void SyncState()
    {
        if (IsClosed || WindowHandle == 0)
        {
            return;
        }

        var state = State;
        if (state == _reportedState)
        {
            return;
        }

        _reportedState = state;
        OnStateChanged();
    }

    /// <summary>Raises <see cref="INativeWindow.VisibilityChanged"/> when the native visibility differs from the last reported one (UI thread).</summary>
    internal void SyncVisibility()
    {
        if (IsClosed || WindowHandle == 0)
        {
            return;
        }

        var visible = IsVisible;
        if (visible == _reportedVisible)
        {
            return;
        }

        _reportedVisible = visible;
        OnVisibilityChanged();
    }

    /// <summary>Unsubscribes from the WPF window (UI thread). Called when the window closes or the manager is disposed.</summary>
    internal void Detach()
    {
        if (Interlocked.Exchange(ref _detached, 1) != 0)
        {
            return;
        }

        // The source is disposed with the native window; only an open window still has the hook to remove.
        if (Interlocked.Exchange(ref _source, null) is { IsDisposed: false } source)
        {
            source.RemoveHook(OnWindowMessage);
        }

        var window = Window;
        window.SourceInitialized -= OnSourceInitialized;
        window.StateChanged -= OnWpfStateChanged;
        window.LocationChanged -= OnWpfLocationChanged;
        window.SizeChanged -= OnWpfSizeChanged;
        window.Activated -= OnWpfActivationChanged;
        window.Deactivated -= OnWpfActivationChanged;
        window.IsVisibleChanged -= OnWpfIsVisibleChanged;
        window.Closing -= OnWpfClosing;
        window.Closed -= OnWpfClosed;
    }

    private async ValueTask<NativeResult> RunOnWindowAsync(Func<Window, Win32WindowController?, NativeResult> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = NativeResult.Ok;
        try
        {
            await InvokeOnUIThreadAsync(() =>
            {
                result = IsClosed ? WindowClosed() : operation(Window, Controller);
                SyncVisibility();
                SyncState();
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return NativeError.FromException(NativeCapabilities.Windows, ex);
        }

        return result;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(Window).Handle;
        Volatile.Write(ref _handle, handle);
        HookSource(handle);
        try
        {
            HandleCreated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            LogHandlerFailed(_logger, nameof(HandleCreated), Id, ex);
        }

        SyncState();
    }

    private void OnWpfStateChanged(object? sender, EventArgs e) => SyncState();

    private void OnWpfLocationChanged(object? sender, EventArgs e) => OnBoundsChanged();

    private void OnWpfSizeChanged(object sender, SizeChangedEventArgs e)
    {
        OnBoundsChanged();

        // Entering or leaving full screen resizes the window without changing Window.WindowState.
        SyncState();
    }

    private void OnWpfActivationChanged(object? sender, EventArgs e) => OnFocusChanged();

    private void OnWpfIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        SyncVisibility();
        SyncState();
    }

    // WPF raises IsVisibleChanged before the native window is shown, and its StateChanged does not cover full screen. The
    // native WM_WINDOWPOSCHANGED follows every show, hide, move, resize, minimize and maximize, so visibility and state
    // notifications always match what Win32 reports.
    private void HookSource(nint handle)
    {
        if (_source is not null || Volatile.Read(ref _detached) != 0)
        {
            return;
        }

        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(OnWindowMessage);
    }

    private nint OnWindowMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_WINDOWPOSCHANGED)
        {
            SyncVisibility();
            SyncState();
        }

        return 0;
    }

    private void OnWpfClosing(object? sender, CancelEventArgs e)
    {
        if (OnClosing())
        {
            e.Cancel = true;
        }
    }

    private void OnWpfClosed(object? sender, EventArgs e)
    {
        Volatile.Write(ref _closed, 1);
        Volatile.Write(ref _handle, 0);
        Detach();
        OnClosed();
    }

    private NativeError NotReady() =>
        NativeError.Unavailable(NativeCapabilities.Windows, NativeErrorCodes.HostNotReady, $"Window '{Id}' has no native window yet.");

    private NativeError WindowClosed() =>
        NativeError.Unavailable(NativeCapabilities.Windows, NativeErrorCodes.NotFound, $"Window '{Id}' has been closed.");

    [LoggerMessage(EventId = 7121, Level = LogLevel.Error, Message = "A {Event} handler of window '{WindowId}' failed.")]
    private static partial void LogHandlerFailed(ILogger logger, string @event, string windowId, Exception exception);
}
