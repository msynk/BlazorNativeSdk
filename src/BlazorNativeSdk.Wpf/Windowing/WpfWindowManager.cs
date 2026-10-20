using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BlazorNativeSdk.Components;
using BlazorNativeSdk.Desktop;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk.Wpf;

/// <summary>
/// <see cref="IWindowManager"/> for WPF Blazor Hybrid apps. Opens <see cref="Window"/>s that host a
/// <see cref="BlazorWebView"/>, adopts windows the app creates itself (<see cref="AttachMainWindow"/>,
/// <see cref="AttachWindow"/>), tracks them and persists their placement.
/// </summary>
/// <remarks>
/// <para>
/// Windows opened by <see cref="OpenAsync"/> load <see cref="WpfNativeOptions.HostPage"/> with the app's service provider and
/// render <see cref="WindowOptions.RootComponent"/> (with <see cref="WindowOptions.Parameters"/>) or, for
/// <see cref="WindowOptions.Route"/>, <see cref="WpfNativeOptions.DefaultRootComponent"/> starting at that route. Each
/// <c>BlazorWebView</c> has its own DI scope and is disposed when its window closes. The root component is wrapped in
/// <see cref="NativeWindowRoot"/>, which associates that scope with the window, so a <c>&lt;NativeActivationOutlet /&gt;</c> in
/// a secondary window doesn't take deep-link navigation away from the main window
/// (see <see cref="WpfNativeOptions.RouteActivationsToMainWindow"/>). Windows the app attaches are not wrapped.
/// </para>
/// <para>
/// Placement persistence (<see cref="WindowOptions.PersistPlacement"/> with an explicit <see cref="WindowOptions.Id"/>)
/// restores the saved placement, validated against the connected displays by <see cref="IWindowPlacementStore"/>, before
/// the window is shown, and saves the restored (normal) bounds and state shortly after the window moves or resizes and when
/// it closes. A persisted state wins over <see cref="WindowOptions.InitialState"/> except
/// <see cref="NativeWindowState.Minimized"/>; windows never reopen minimized.
/// </para>
/// <para>
/// Members may be called from any thread. Windows are created on the WPF application's dispatcher, and
/// <see cref="WindowOpened"/> and <see cref="WindowClosed"/> are raised on it.
/// </para>
/// </remarks>
public sealed partial class WpfWindowManager : IWindowManager, IDisposable, IAsyncDisposable
{
    /// <summary>The default id of the main window (<see cref="AttachMainWindow"/>).</summary>
    public const string DefaultMainWindowId = "main";

    private static readonly TimeSpan s_placementWaitBudget = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan s_disposeFlushTimeout = TimeSpan.FromSeconds(2);

    private readonly IServiceProvider _services;
    private readonly WpfNativeOptions _options;
    private readonly NativeSdkOptions _sdkOptions;
    private readonly IWindowPlacementStore _placements;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WpfWindowManager> _logger;
    private readonly Lock _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<string, Task<WindowPlacement?>> _placementLoads = new(StringComparer.Ordinal);
    private readonly Lock _savesGate = new();
    private readonly HashSet<Task> _pendingSaves = [];
    private Entry? _main;
    private ILogger? _windowLogger;
    private int _disposed;

    /// <summary>Creates the window manager.</summary>
    /// <param name="services">The application's service provider, passed to each window's <see cref="BlazorWebView"/>.</param>
    /// <param name="options">The WPF options.</param>
    /// <param name="sdkOptions">The SDK options (the app name is the default window title).</param>
    /// <param name="placements">Persists window placement.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The time provider used to debounce placement saves.</param>
    public WpfWindowManager(
        IServiceProvider services,
        IOptions<WpfNativeOptions> options,
        IOptions<NativeSdkOptions> sdkOptions,
        IWindowPlacementStore placements,
        ILogger<WpfWindowManager> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sdkOptions);
        ArgumentNullException.ThrowIfNull(placements);
        _services = services;
        _options = options.Value;
        _sdkOptions = sdkOptions.Value;
        _placements = placements;
        _logger = logger ?? NullLogger<WpfWindowManager>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.Windows;

    /// <inheritdoc />
    public IReadOnlyList<INativeWindow> Windows
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Select(static e => e.Window)];
            }
        }
    }

    /// <inheritdoc />
    public INativeWindow? MainWindow => MainNativeWindow;

    /// <summary>The main window as a <see cref="WpfNativeWindow"/>, or <see langword="null"/> when none is open.</summary>
    public WpfNativeWindow? MainNativeWindow
    {
        get
        {
            lock (_gate)
            {
                return _main?.Window;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<INativeWindow>? WindowOpened;

    /// <inheritdoc />
    public event EventHandler<INativeWindow>? WindowClosed;

    private ILogger WindowLogger => _windowLogger ??= _services.GetService<ILogger<WpfNativeWindow>>() ?? NullLogger<WpfNativeWindow>.Instance;

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return ValueTask.FromResult(CapabilityStatus.Unavailable(Capability, NativeErrorCodes.HostNotReady, "The window manager has been disposed.", WpfAdapter.Name));
        }

        return ValueTask.FromResult(ResolveDispatcher() is null
            ? CapabilityStatus.Unavailable(Capability, NativeErrorCodes.HostNotReady, "No WPF Application is running and no window has been attached.", WpfAdapter.Name)
            : CapabilityStatus.Available(Capability, WpfAdapter.Name, "multi-window", "placement-persistence", "full-screen", "owned-windows"));
    }

    /// <inheritdoc />
    public INativeWindow? FindWindow(string id) => FindNativeWindow(id);

    /// <summary>Finds an open window by id.</summary>
    public WpfNativeWindow? FindNativeWindow(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            return _entries.Find(e => string.Equals(e.Window.Id, id, StringComparison.Ordinal))?.Window;
        }
    }

    /// <inheritdoc />
    public async ValueTask<NativeResult<INativeWindow>> OpenAsync(WindowOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0)
        {
            return Disposed();
        }

        if (WpfWindowSettings.Validate(options) is { } invalid)
        {
            return invalid;
        }

        var dispatcher = ResolveDispatcher();
        if (dispatcher is null)
        {
            return NativeError.Unavailable(Capability, NativeErrorCodes.HostNotReady, "No WPF Application is running; windows can only be opened on its dispatcher.");
        }

        var explicitId = options.Id is not null;
        var id = options.Id ?? Guid.NewGuid().ToString("N");
        try
        {
            // Fast path: focus an open window without touching the placement store. A refused foreground change (the
            // taskbar button flashes instead) is not a failure of opening.
            if (explicitId && FindNativeWindow(id) is { } open)
            {
                _ = await open.FocusAsync(cancellationToken).ConfigureAwait(false);
                return NativeResult.Success<INativeWindow>(open);
            }

            WindowPlacement? placement = null;
            if (explicitId && options.PersistPlacement)
            {
                placement = await TakePlacementLoad(id).WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await WpfDispatch.InvokeAsync(dispatcher, () => CreateWindow(id, explicitId, options, placement)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogOpenFailed(_logger, id, ex);
            return NativeError.FromException(Capability, ex);
        }
    }

    /// <summary>
    /// Adopts the app's main window (typically from its constructor, before it is shown) so it is reported by
    /// <see cref="MainWindow"/>, raises <see cref="INativeWindow"/> events, parents pickers and dialogs, and drives the app
    /// lifecycle. With <paramref name="persistPlacement"/>, its saved placement is restored when its native window is created
    /// (or immediately, if it already exists) and saved as it changes.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="id">The window id, also the placement key. Default: <c>main</c>.</param>
    /// <param name="persistPlacement">Restore and save the window's placement.</param>
    /// <returns>
    /// The adopted window; the same instance when <paramref name="window"/> is already attached with this id.
    /// <see cref="NativeErrorCodes.Conflict"/> when another main window is open or <paramref name="id"/> is in use.
    /// </returns>
    /// <remarks>Call it on the window's thread; calls from other threads are marshalled synchronously.</remarks>
    public NativeResult<WpfNativeWindow> AttachMainWindow(Window window, string id = DefaultMainWindowId, bool persistPlacement = true) =>
        Attach(window, id, persistPlacement, isMainWindow: true);

    /// <summary>
    /// Adopts a secondary window the app created itself (for example a XAML dialog), so it is tracked, raises
    /// <see cref="INativeWindow"/> events and optionally persists its placement. See <see cref="AttachMainWindow"/>.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="id">The window id, also the placement key.</param>
    /// <param name="persistPlacement">Restore and save the window's placement.</param>
    public NativeResult<WpfNativeWindow> AttachWindow(Window window, string id, bool persistPlacement = true) =>
        Attach(window, id, persistPlacement, isMainWindow: false);

    /// <summary>Saves the placement of every tracked window now and waits for writes in progress.</summary>
    public async Task FlushPlacementsAsync(CancellationToken cancellationToken = default)
    {
        Entry[] entries;
        lock (_gate)
        {
            entries = [.. _entries];
        }

        foreach (var entry in entries)
        {
            _ = entry.Tracker?.SaveNow();
        }

        Task[] pending;
        lock (_savesGate)
        {
            pending = [.. _pendingSaves];
        }

        await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts loading the saved placement of <paramref name="id"/> so it is ready when the window is created.</summary>
    internal void PreloadPlacement(string id)
    {
        lock (_gate)
        {
            if (!_placementLoads.ContainsKey(id) && !_entries.Exists(e => e.Window.Id == id))
            {
                _placementLoads[id] = Task.Run(() => LoadPlacementAsync(id));
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var pending = Shutdown();
        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(s_disposeFlushTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                LogFlushTimedOut(_logger);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var pending = Shutdown();
        if (pending.Length > 0 && !Task.WaitAll(pending, s_disposeFlushTimeout))
        {
            LogFlushTimedOut(_logger);
        }
    }

    private Task[] Shutdown()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return [];
        }

        Entry[] entries;
        lock (_gate)
        {
            entries = [.. _entries];
            _entries.Clear();
            _placementLoads.Clear();
            _main = null;
        }

        foreach (var entry in entries)
        {
            if (entry.Tracker is { } tracker)
            {
                _ = tracker.SaveNow();
                tracker.Dispose();
            }

            var window = entry.Window;
            if (!window.Dispatcher.HasShutdownStarted)
            {
                WpfDispatch.RunOrPost(window.Dispatcher, window.Detach);
            }
        }

        lock (_savesGate)
        {
            return [.. _pendingSaves];
        }
    }

    private NativeResult<WpfNativeWindow> Attach(Window window, string id, bool persistPlacement, bool isMainWindow)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!window.Dispatcher.CheckAccess())
        {
            return window.Dispatcher.Invoke(() => Attach(window, id, persistPlacement, isMainWindow));
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            return NativeResult.Failure<WpfNativeWindow>(Disposed());
        }

        if (id.Length > WpfWindowSettings.MaxIdLength)
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, $"Window ids must be at most {WpfWindowSettings.MaxIdLength} characters.");
        }

        lock (_gate)
        {
            if (_entries.Find(e => ReferenceEquals(e.Window.Window, window)) is { } existing)
            {
                return existing.Window.Id == id && existing.Window.IsMainWindow == isMainWindow
                    ? existing.Window
                    : Conflict($"The window is already attached as '{existing.Window.Id}'.");
            }

            if (_entries.Exists(e => e.Window.Id == id))
            {
                return Conflict($"A window with the id '{id}' is already open.");
            }

            if (isMainWindow && _main is not null)
            {
                return Conflict($"The main window '{_main.Window.Id}' is already attached.");
            }
        }

        var native = new WpfNativeWindow(window, id, isMainWindow, WindowLogger);
        var entry = Track(native, persistPlacement, ownedWebView: null);
        if (entry.Tracker is not null)
        {
            RestoreAttachedPlacement(entry, TakePlacementLoad(id));
        }

        LogAttached(_logger, id, isMainWindow);
        RaiseOpened(native);
        return native;
    }

    private static NativeResult<INativeWindow> FocusExisting(WpfNativeWindow window)
    {
        // Opening an id that is open focuses the window (synchronously: this runs on the UI thread); a refused foreground
        // change (taskbar flash) is not a failure.
        _ = window.FocusAsync().AsTask();
        return NativeResult.Success<INativeWindow>(window);
    }

    private NativeResult<INativeWindow> CreateWindow(string id, bool explicitId, WindowOptions options, WindowPlacement? placement)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return Disposed();
        }

        if (FindNativeWindow(id) is { } open)
        {
            return FocusExisting(open);
        }

        WpfNativeWindow? owner = null;
        if (options.OwnerId is { } ownerId && (owner = FindNativeWindow(ownerId)) is null)
        {
            return NativeError.Unavailable(Capability, NativeErrorCodes.NotFound, $"No open window has the id '{ownerId}'.");
        }

        if (owner is not null && !owner.Dispatcher.CheckAccess())
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, $"The owner window '{owner.Id}' belongs to another UI thread.");
        }

        var rootComponent = options.RootComponent ?? _options.DefaultRootComponent ?? DiscoverRootComponent();
        if (rootComponent is null)
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument,
                "The window has no root component: set WindowOptions.RootComponent, WpfNativeOptions.DefaultRootComponent, or attach a main window that hosts a BlazorWebView.");
        }

        var created = WpfWindowSettings.Create(options, placement, _sdkOptions.DisplayName, owner is not null);
        if (!created.TryGetValue(out var settings))
        {
            return created.Error!;
        }

        var webView = new BlazorWebView
        {
            HostPage = _options.HostPage,
            Services = _services,
        };
        var root = CreateRootComponent(id, rootComponent, options, _options);
        webView.RootComponents.Add(root);
        if (settings.StartPath is not null)
        {
            webView.StartPath = settings.StartPath;
        }

        var window = new Window
        {
            Content = webView,
            Title = settings.Title,
            MinWidth = settings.MinWidth,
            MinHeight = settings.MinHeight,
            Width = settings.Width,
            Height = settings.Height,
            Left = settings.Left,
            Top = settings.Top,
            WindowStartupLocation = settings.StartupLocation,
            ResizeMode = settings.ResizeMode,
            Topmost = settings.Topmost,
            WindowState = settings.WindowState,
        };

        WpfNativeWindow? native = null;
        Entry? entry = null;
        try
        {
            if (owner is not null)
            {
                window.Owner = owner.Window;
            }

            _options.ConfigureWindow?.Invoke(window, webView, options);
            if (!window.ShowActivated && window.WindowState == WindowState.Maximized)
            {
                // WPF cannot show a maximized window without activating it.
                window.WindowState = WindowState.Normal;
            }

            var isMainWindow = MainNativeWindow is null && ReferenceEquals(Application.Current?.MainWindow, window);
            if (isMainWindow)
            {
                // ConfigureWindow made this the application's main window; it receives activations like any main window.
                root.Parameters![nameof(NativeWindowRoot.RoutesActivations)] = true;
            }

            native = new WpfNativeWindow(window, id, isMainWindow, WindowLogger);

            // Create the native window hidden, place it, then show it: no flicker, and positions use the SDK's per-monitor DIPs.
            _ = new WindowInteropHelper(window).EnsureHandle();
            ApplyCreationPlacement(native, settings);
            entry = Track(native, explicitId && options.PersistPlacement, webView);
            entry.Tracker?.SetBaseline(settings.Placement);

            window.Show();
            if (settings.EnterFullScreen && native.NativeController is { } controller)
            {
                var fullScreen = controller.SetState(NativeWindowState.FullScreen);
                if (!fullScreen.IsSuccess)
                {
                    LogPlacementFailed(_logger, id, fullScreen.Error!.Message);
                }

                native.SyncState();
            }
        }
        catch
        {
            if (entry is not null)
            {
                Untrack(entry);
            }

            native?.Detach();
            try
            {
                window.Close();
            }
            catch (InvalidOperationException)
            {
            }

            _ = DisposeWebViewAsync(id, webView);
            throw;
        }

        LogOpened(_logger, id, native.IsMainWindow);
        RaiseOpened(native);
        return NativeResult.Success<INativeWindow>(native);
    }

    /// <summary>
    /// Creates the root component of a window opened by <see cref="OpenAsync"/>: <paramref name="component"/> wrapped in
    /// <see cref="NativeWindowRoot"/>, so the web view's scope knows its window and whether activations may navigate it.
    /// </summary>
    internal static RootComponent CreateRootComponent(
        string windowId,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type component,
        WindowOptions options,
        WpfNativeOptions wpfOptions)
    {
        var componentParameters = options.RootComponent is not null && options.Parameters is { Count: > 0 } parameters
            ? new Dictionary<string, object?>(parameters, StringComparer.Ordinal)
            : null;
        return new RootComponent
        {
            Selector = wpfOptions.RootComponentSelector,
            ComponentType = typeof(NativeWindowRoot),
            Parameters = NativeWindowRoot.CreateParameters(windowId, component, componentParameters, routesActivations: !wpfOptions.RouteActivationsToMainWindow),
        };
    }

    private void ApplyCreationPlacement(WpfNativeWindow native, WpfWindowSettings settings)
    {
        if (native.NativeController is not { } controller)
        {
            return;
        }

        NativeResult result;
        if (settings.Placement is { } placement)
        {
            result = controller.ApplyPlacement(placement, applyState: false);
        }
        else if (settings.ExplicitBounds is { } bounds)
        {
            var current = controller.Bounds;
            result = controller.SetBounds(new WindowBounds(
                bounds.X,
                bounds.Y,
                double.IsNaN(bounds.Width) ? current.Width : bounds.Width,
                double.IsNaN(bounds.Height) ? current.Height : bounds.Height));
        }
        else
        {
            return;
        }

        if (!result.IsSuccess)
        {
            LogPlacementFailed(_logger, native.Id, result.Error!.Message);
        }
    }

    private void RestoreAttachedPlacement(Entry entry, Task<WindowPlacement?> load)
    {
        var native = entry.Window;
        if (native.NativeController is not null)
        {
            // The native window already exists (the app attached it late): apply once loaded.
            ApplyWhenLoaded(entry, load, beforeShow: false);
            return;
        }

        void OnHandleCreated(object? sender, EventArgs e)
        {
            native.HandleCreated -= OnHandleCreated;

            // Still hidden: apply synchronously so the window appears at its saved position. The load started when the window
            // was attached (or at startup), so it is normally complete; never block the UI thread for long.
            if (!load.IsCompleted)
            {
                _ = load.Wait(s_placementWaitBudget);
            }

            if (load.IsCompleted)
            {
                ApplyAttachedPlacement(entry, load.Result, beforeShow: true);
            }
            else
            {
                LogPlacementDeferred(_logger, native.Id);
                ApplyWhenLoaded(entry, load, beforeShow: false);
            }
        }

        native.HandleCreated += OnHandleCreated;
    }

    private void ApplyWhenLoaded(Entry entry, Task<WindowPlacement?> load, bool beforeShow) =>
        _ = load.ContinueWith(
            t => WpfDispatch.RunOrPost(entry.Window.Dispatcher, () => ApplyAttachedPlacement(entry, t.Result, beforeShow)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private void ApplyAttachedPlacement(Entry entry, WindowPlacement? placement, bool beforeShow)
    {
        var native = entry.Window;
        if (placement is null || native.IsClosed || Volatile.Read(ref _disposed) != 0 || native.NativeController is not { } controller)
        {
            return;
        }

        var window = native.Window;
        var state = WpfWindowSettings.ResolveInitialState(NativeWindowState.Normal, placement);

        // A saved placement wins over sizing to content and over a startup location set in XAML.
        window.SizeToContent = SizeToContent.Manual;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        var result = controller.ApplyPlacement(placement, applyState: !beforeShow && controller.IsVisible);
        if (!result.IsSuccess)
        {
            LogPlacementFailed(_logger, native.Id, result.Error!.Message);
            return;
        }

        entry.Tracker?.SetBaseline(placement with { State = state });
        if (!beforeShow)
        {
            native.SyncState();
            return;
        }

        // WPF refuses to show a maximized window without activating it (Win32 cannot maximize without activation), so such
        // windows reopen with their restored bounds.
        if (state == NativeWindowState.Maximized && window.ShowActivated)
        {
            window.WindowState = WindowState.Maximized;
        }

        if (state == NativeWindowState.FullScreen)
        {
            // WPF has no full-screen state; switch once the window has been shown.
            WpfDispatch.Post(native.Dispatcher, () => EnterFullScreenWhenShown(native));
        }
    }

    private void EnterFullScreenWhenShown(WpfNativeWindow native)
    {
        if (native.IsClosed || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (native.NativeController is { IsVisible: true } controller)
        {
            var result = controller.SetState(NativeWindowState.FullScreen);
            if (!result.IsSuccess)
            {
                LogPlacementFailed(_logger, native.Id, result.Error!.Message);
            }

            native.SyncState();
            return;
        }

        void OnVisibilityChanged(object? sender, EventArgs e)
        {
            if (!native.IsVisible)
            {
                return;
            }

            native.VisibilityChanged -= OnVisibilityChanged;

            // Leave the window message that reported the change before restyling the window.
            WpfDispatch.Post(native.Dispatcher, () => EnterFullScreenWhenShown(native));
        }

        native.VisibilityChanged += OnVisibilityChanged;
    }

    private Entry Track(WpfNativeWindow native, bool persistPlacement, BlazorWebView? ownedWebView)
    {
        var tracker = persistPlacement
            ? new WindowPlacementTracker(native, _placements, _timeProvider, _options.PlacementSaveDelay, TrackSave, _logger)
            : null;
        var entry = new Entry(native, tracker, ownedWebView);
        lock (_gate)
        {
            _entries.Add(entry);
            if (native.IsMainWindow)
            {
                _main = entry;
            }
        }

        native.Closing += (_, _) =>
        {
            _ = entry.Tracker?.SaveNow();
        };
        native.Closed += (_, _) => OnWindowClosed(entry);
        return entry;
    }

    private bool Untrack(Entry entry)
    {
        bool removed;
        lock (_gate)
        {
            removed = _entries.Remove(entry);
            if (ReferenceEquals(_main, entry))
            {
                _main = null;
            }
        }

        entry.Tracker?.Dispose();
        return removed;
    }

    private void OnWindowClosed(Entry entry)
    {
        var removed = Untrack(entry);
        if (entry.OwnedWebView is { } webView)
        {
            _ = DisposeWebViewAsync(entry.Window.Id, webView);
        }

        if (!removed)
        {
            return;
        }

        LogClosed(_logger, entry.Window.Id);
        try
        {
            WindowClosed?.Invoke(this, entry.Window);
        }
        catch (Exception ex)
        {
            LogHandlerFailed(_logger, nameof(WindowClosed), ex);
        }
    }

    private void RaiseOpened(WpfNativeWindow window)
    {
        try
        {
            WindowOpened?.Invoke(this, window);
        }
        catch (Exception ex)
        {
            LogHandlerFailed(_logger, nameof(WindowOpened), ex);
        }
    }

    private void TrackSave(Task save)
    {
        if (save.IsCompleted)
        {
            return;
        }

        lock (_savesGate)
        {
            _ = _pendingSaves.Add(save);
        }

        _ = save.ContinueWith(
            t =>
            {
                lock (_savesGate)
                {
                    _ = _pendingSaves.Remove(t);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private Task<WindowPlacement?> TakePlacementLoad(string id)
    {
        lock (_gate)
        {
            if (_placementLoads.Remove(id, out var preloaded))
            {
                return preloaded;
            }
        }

        // Run on the thread pool so a store that captures the synchronization context cannot deadlock with the UI thread.
        return Task.Run(() => LoadPlacementAsync(id));
    }

    private async Task<WindowPlacement?> LoadPlacementAsync(string id)
    {
        try
        {
            return await _placements.LoadAsync(id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogPlacementLoadFailed(_logger, id, ex);
            return null;
        }
    }

    private Type? DiscoverRootComponent()
    {
        // Reuse the root component of the main window's BlazorWebView (typically the app's Routes component).
        if (MainNativeWindow is not { } main || !main.Dispatcher.CheckAccess())
        {
            return null;
        }

        return FindWebView(main.Window, depth: 0)?.RootComponents.FirstOrDefault()?.ComponentType;

        static BlazorWebView? FindWebView(DependencyObject element, int depth)
        {
            if (element is BlazorWebView webView)
            {
                return webView;
            }

            if (depth > 32)
            {
                return null;
            }

            foreach (var child in LogicalTreeHelper.GetChildren(element))
            {
                if (child is DependencyObject d && FindWebView(d, depth + 1) is { } found)
                {
                    return found;
                }
            }

            return null;
        }
    }

    private async Task DisposeWebViewAsync(string windowId, BlazorWebView webView)
    {
        try
        {
            // BlazorWebView must be disposed on its UI thread; the continuation stays there.
            await webView.DisposeAsync();
        }
        catch (Exception ex)
        {
            LogWebViewDisposeFailed(_logger, windowId, ex);
        }
    }

    private Dispatcher? ResolveDispatcher() => WpfDispatch.ApplicationDispatcher ?? MainNativeWindow?.Dispatcher;

    private NativeError Disposed() =>
        NativeError.Unavailable(Capability, NativeErrorCodes.HostNotReady, "The window manager has been disposed.");

    private NativeError Conflict(string message) => NativeError.Failed(Capability, NativeErrorCodes.Conflict, message);

    private sealed class Entry(WpfNativeWindow window, WindowPlacementTracker? tracker, BlazorWebView? ownedWebView)
    {
        public WpfNativeWindow Window { get; } = window;

        public WindowPlacementTracker? Tracker { get; } = tracker;

        public BlazorWebView? OwnedWebView { get; } = ownedWebView;
    }

    [LoggerMessage(EventId = 7101, Level = LogLevel.Debug, Message = "Opened window '{WindowId}' (main: {IsMainWindow}).")]
    private static partial void LogOpened(ILogger logger, string windowId, bool isMainWindow);

    [LoggerMessage(EventId = 7102, Level = LogLevel.Debug, Message = "Attached window '{WindowId}' (main: {IsMainWindow}).")]
    private static partial void LogAttached(ILogger logger, string windowId, bool isMainWindow);

    [LoggerMessage(EventId = 7103, Level = LogLevel.Debug, Message = "Window '{WindowId}' closed.")]
    private static partial void LogClosed(ILogger logger, string windowId);

    [LoggerMessage(EventId = 7104, Level = LogLevel.Warning, Message = "Opening window '{WindowId}' failed.")]
    private static partial void LogOpenFailed(ILogger logger, string windowId, Exception exception);

    [LoggerMessage(EventId = 7105, Level = LogLevel.Warning, Message = "Loading the placement of window '{WindowId}' failed; it opens at its default position.")]
    private static partial void LogPlacementLoadFailed(ILogger logger, string windowId, Exception exception);

    [LoggerMessage(EventId = 7106, Level = LogLevel.Warning, Message = "Placing window '{WindowId}' failed: {Reason}")]
    private static partial void LogPlacementFailed(ILogger logger, string windowId, string reason);

    [LoggerMessage(EventId = 7107, Level = LogLevel.Debug, Message = "The placement of window '{WindowId}' was not loaded in time; it is applied once available.")]
    private static partial void LogPlacementDeferred(ILogger logger, string windowId);

    [LoggerMessage(EventId = 7108, Level = LogLevel.Error, Message = "A {Event} handler failed.")]
    private static partial void LogHandlerFailed(ILogger logger, string @event, Exception exception);

    [LoggerMessage(EventId = 7109, Level = LogLevel.Warning, Message = "Disposing the BlazorWebView of window '{WindowId}' failed.")]
    private static partial void LogWebViewDisposeFailed(ILogger logger, string windowId, Exception exception);

    [LoggerMessage(EventId = 7110, Level = LogLevel.Warning, Message = "Pending window placement saves did not complete in time.")]
    private static partial void LogFlushTimedOut(ILogger logger);
}
