using System.Windows;
using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.Logging;

namespace BlazorNativeSdk.Wpf;

/// <summary>
/// <see cref="IAppLifecycle"/> for WPF apps, driven by <see cref="Application"/> events and the windows tracked by
/// <see cref="WpfWindowManager"/>.
/// </summary>
/// <remarks>
/// <list type="table">
/// <listheader><term>State</term><description>WPF signal</description></listheader>
/// <item><term><see cref="AppLifecycleState.Active"/></term><description><see cref="Application.Activated"/> (an app window is the foreground window).</description></item>
/// <item><term><see cref="AppLifecycleState.Inactive"/></term><description><see cref="Application.Deactivated"/>, or a window restored while another app has the focus.</description></item>
/// <item><term><see cref="AppLifecycleState.Background"/></term><description>Every tracked window is minimized or hidden, or all of them closed while the process keeps running (tray apps).</description></item>
/// <item><term><see cref="AppLifecycleState.Terminating"/></term><description><see cref="Application.SessionEnding"/> (unless cancelled) and <see cref="Application.Exit"/>.</description></item>
/// </list>
/// The state stays <see cref="AppLifecycleState.Launching"/> until a tracked window is shown or the app is activated.
/// Desktop processes keep running in the background (<see cref="BackgroundExecutionGuarantee.NotRequired"/>); WPF has no
/// memory-pressure notification, so <see cref="IAppLifecycle.MemoryPressure"/> is never raised.
/// </remarks>
public sealed class WpfAppLifecycle : AppLifecycleBase, IDisposable, IAsyncDisposable
{
    private readonly WpfWindowManager _windows;
    private readonly Lock _sync = new();
    private readonly HashSet<WpfNativeWindow> _observed = [];
    private Application? _application;
    private bool _isActive;
    private bool _hasBeenActivated;
    private bool _hasShownWindow;
    private bool _sessionEnding;
    private bool _exiting;
    private int _attachRequested;
    private int _disposed;

    /// <summary>Creates the lifecycle and attaches to <see cref="Application.Current"/> when it exists.</summary>
    /// <param name="windows">The window manager whose windows determine visibility.</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="logger">The logger.</param>
    public WpfAppLifecycle(WpfWindowManager windows, TimeProvider? timeProvider = null, ILogger<WpfAppLifecycle>? logger = null)
        : base(timeProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(windows);
        _windows = windows;
        _windows.WindowOpened += OnWindowOpened;
        _windows.WindowClosed += OnWindowClosed;
        foreach (var window in _windows.Windows)
        {
            Observe(window);
        }

        AttachToApplication(Application.Current);
        Evaluate("created");
    }

    /// <inheritdoc />
    protected override string AdapterName => WpfAdapter.Name;

    /// <inheritdoc />
    public override BackgroundExecutionGuarantee BackgroundGuarantee => BackgroundExecutionGuarantee.NotRequired;

    /// <summary>Subscribes to <paramref name="application"/>'s events (once). Safe to call from any thread.</summary>
    internal void AttachToApplication(Application? application)
    {
        if (application is null || Volatile.Read(ref _disposed) != 0 || Interlocked.CompareExchange(ref _attachRequested, 1, 0) != 0)
        {
            return;
        }

        WpfDispatch.RunOrPost(application.Dispatcher, () =>
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            application.Activated += OnApplicationActivated;
            application.Deactivated += OnApplicationDeactivated;
            application.SessionEnding += OnSessionEnding;
            application.Exit += OnExit;
            var active = false;
            foreach (Window window in application.Windows)
            {
                active |= window.IsActive;
            }

            lock (_sync)
            {
                _application = application;
                _isActive = active;
                _hasBeenActivated |= active;
            }

            Evaluate("attached");
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _windows.WindowOpened -= OnWindowOpened;
        _windows.WindowClosed -= OnWindowClosed;
        WpfNativeWindow[] observed;
        Application? application;
        lock (_sync)
        {
            observed = [.. _observed];
            _observed.Clear();
            application = _application;
            _application = null;
        }

        foreach (var window in observed)
        {
            window.StateChanged -= OnWindowChanged;
            window.VisibilityChanged -= OnWindowChanged;
        }

        if (application is not null && !application.Dispatcher.HasShutdownStarted)
        {
            WpfDispatch.RunOrPost(application.Dispatcher, () =>
            {
                application.Activated -= OnApplicationActivated;
                application.Deactivated -= OnApplicationDeactivated;
                application.SessionEnding -= OnSessionEnding;
                application.Exit -= OnExit;
            });
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void OnWindowOpened(object? sender, INativeWindow window)
    {
        AttachToApplication(Application.Current);
        Observe(window);
        Evaluate("WindowOpened");
    }

    private void OnWindowClosed(object? sender, INativeWindow window)
    {
        if (window is WpfNativeWindow native)
        {
            lock (_sync)
            {
                _ = _observed.Remove(native);
            }

            native.StateChanged -= OnWindowChanged;
            native.VisibilityChanged -= OnWindowChanged;
        }

        Evaluate("WindowClosed");
    }

    private void Observe(INativeWindow window)
    {
        if (window is not WpfNativeWindow native)
        {
            return;
        }

        lock (_sync)
        {
            if (!_observed.Add(native))
            {
                return;
            }
        }

        native.StateChanged += OnWindowChanged;
        native.VisibilityChanged += OnWindowChanged;
    }

    private void OnWindowChanged(object? sender, EventArgs e) => Evaluate("WindowStateChanged");

    private void OnApplicationActivated(object? sender, EventArgs e)
    {
        lock (_sync)
        {
            _isActive = true;
            _hasBeenActivated = true;
        }

        Evaluate("Activated");
    }

    private void OnApplicationDeactivated(object? sender, EventArgs e)
    {
        lock (_sync)
        {
            _isActive = false;
        }

        Evaluate("Deactivated");
    }

    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        if (e.Cancel)
        {
            return;
        }

        lock (_sync)
        {
            _sessionEnding = true;
        }

        Evaluate("SessionEnding");

        // Handlers registered after this one may still cancel; re-check once they all ran.
        if (sender is Application application)
        {
            WpfDispatch.Post(application.Dispatcher, () =>
            {
                if (!e.Cancel)
                {
                    return;
                }

                lock (_sync)
                {
                    _sessionEnding = false;
                }

                Evaluate("SessionEndingCancelled");
            });
        }
    }

    private void OnExit(object? sender, ExitEventArgs e)
    {
        lock (_sync)
        {
            _exiting = true;
        }

        Evaluate("Exit");
    }

    private void Evaluate(string reason)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var shown = 0;
        var anyVisible = false;
        foreach (var window in _windows.Windows)
        {
            if (!window.IsVisible)
            {
                continue;
            }

            anyVisible = true;
            if (window.State != NativeWindowState.Minimized)
            {
                shown++;
            }
        }

        AppLifecycleState? state;
        lock (_sync)
        {
            _hasShownWindow |= anyVisible;
            state = WpfLifecycleStateMapper.Map(new WpfLifecycleSnapshot(
                IsTerminating: _exiting || _sessionEnding,
                IsApplicationActive: _isActive,
                HasBeenActivated: _hasBeenActivated,
                HasShownWindow: _hasShownWindow,
                ShownWindowCount: shown));
        }

        if (state is { } next)
        {
            _ = ReportState(next, reason);
        }
    }
}
