using System.Collections.Concurrent;
using BlazorNativeSdk.Desktop;

namespace BlazorNativeSdk.Testing;

/// <summary>
/// An in-memory <see cref="IWindowManager"/>. It starts with a main window (id <c>main</c>), opens <see cref="FakeNativeWindow"/>s,
/// focuses an already open window when its id is opened again, and raises <see cref="WindowOpened"/> and
/// <see cref="WindowClosed"/>.
/// </summary>
/// <remarks>Option validation and error codes match the WPF window manager. Placement persistence is not simulated.</remarks>
public sealed class FakeWindowManager : FakeCapability, IWindowManager
{
    /// <summary>The id of the main window created by the constructor.</summary>
    public const string MainWindowId = "main";

    private readonly Lock _gate = new();
    private readonly List<FakeNativeWindow> _windows = [];
    private readonly ConcurrentQueue<WindowOptions> _openRequests = new();

    /// <summary>Creates the fake with a focused, visible main window.</summary>
    public FakeWindowManager()
        : base(NativeCapabilities.Windows, "multi-window", "placement-persistence", "full-screen", "owned-windows")
    {
        var main = new FakeNativeWindow(this, MainWindowId, new WindowOptions { Id = MainWindowId, Title = "Main window" }, isMainWindow: true, new WindowBounds(0, 0, 1280, 800));
        main.SetFocused(true);
        _windows.Add(main);
    }

    /// <inheritdoc />
    public event EventHandler<INativeWindow>? WindowOpened;

    /// <inheritdoc />
    public event EventHandler<INativeWindow>? WindowClosed;

    /// <inheritdoc />
    public IReadOnlyList<INativeWindow> Windows => FakeWindows;

    /// <summary>The open windows as their fake type.</summary>
    public IReadOnlyList<FakeNativeWindow> FakeWindows
    {
        get
        {
            lock (_gate)
            {
                return [.. _windows];
            }
        }
    }

    /// <inheritdoc />
    public INativeWindow? MainWindow => FakeWindows.FirstOrDefault(static w => w.IsMainWindow);

    /// <summary>Results for <see cref="OpenAsync"/>; a queued failure is returned instead of opening a window. Defaults to success.</summary>
    public ResultQueue OpenResults { get; } = new();

    /// <summary>The options passed to <see cref="OpenAsync"/>, in order.</summary>
    public IReadOnlyList<WindowOptions> OpenRequests => [.. _openRequests];

    /// <inheritdoc />
    public ValueTask<NativeResult<INativeWindow>> OpenAsync(WindowOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        Record(nameof(OpenAsync));
        _openRequests.Enqueue(options);
        if (StatusError() is { } error)
        {
            return Result(error);
        }

        if (Validate(options) is { } invalid)
        {
            return Result(invalid);
        }

        if (OpenResults.Next() is { IsSuccess: false } scripted)
        {
            return Result(scripted.Error!);
        }

        FakeNativeWindow? existing;
        FakeNativeWindow? window = null;
        lock (_gate)
        {
            existing = options.Id is { } id ? _windows.Find(w => w.Id == id) : null;
            if (existing is null)
            {
                if (options.OwnerId is { } ownerId && !_windows.Exists(w => w.Id == ownerId))
                {
                    return Result(NativeError.Unavailable(Capability, NativeErrorCodes.NotFound, $"No open window has the id '{ownerId}'."));
                }

                var width = options.Width ?? 800;
                var height = options.Height ?? 600;
                var bounds = options.X is null && options.Y is null
                    ? new WindowBounds((1920 - width) / 2, (1080 - height) / 2, width, height)
                    : new WindowBounds(options.X ?? 0, options.Y ?? 0, width, height);
                window = new FakeNativeWindow(this, options.Id ?? Guid.NewGuid().ToString("N"), options, isMainWindow: false, bounds);
                _windows.Add(window);
            }
        }

        if (existing is not null)
        {
            // Opening an id that is already open focuses the existing window.
            existing.SimulateUserFocus();
            return ValueTask.FromResult(NativeResult.Success<INativeWindow>(existing));
        }

        Activate(window!);
        WindowOpened?.Invoke(this, window!);
        return ValueTask.FromResult(NativeResult.Success<INativeWindow>(window!));
    }

    /// <inheritdoc />
    public INativeWindow? FindWindow(string id) => FindFakeWindow(id);

    /// <summary>Finds an open window by id as its fake type.</summary>
    public FakeNativeWindow? FindFakeWindow(string id)
    {
        lock (_gate)
        {
            return _windows.Find(w => w.Id == id);
        }
    }

    internal void Activate(FakeNativeWindow window)
    {
        foreach (var other in FakeWindows)
        {
            other.SetFocused(ReferenceEquals(other, window));
        }
    }

    internal void OnClosed(FakeNativeWindow window)
    {
        lock (_gate)
        {
            if (!_windows.Remove(window))
            {
                return;
            }
        }

        WindowClosed?.Invoke(this, window);
    }

    private static ValueTask<NativeResult<INativeWindow>> Result(NativeError error) => ValueTask.FromResult(NativeResult.Failure<INativeWindow>(error));

    private static NativeError? Validate(WindowOptions options)
    {
        if (options.Id is { } id && (string.IsNullOrWhiteSpace(id) || id.Length > 128))
        {
            return Invalid("Window ids must be non-empty and at most 128 characters.");
        }

        if (options.OwnerId is not null && options.OwnerId == options.Id)
        {
            return Invalid("A window cannot own itself.");
        }

        if (!IsPositive(options.Width) || !IsPositive(options.Height))
        {
            return Invalid("Window width and height must be finite and greater than zero.");
        }

        if (options.MinWidth is { } minWidth && (!double.IsFinite(minWidth) || minWidth < 0) ||
            options.MinHeight is { } minHeight && (!double.IsFinite(minHeight) || minHeight < 0))
        {
            return Invalid("Minimum window sizes must be finite and not negative.");
        }

        if (options.X is { } x && !double.IsFinite(x) || options.Y is { } y && !double.IsFinite(y))
        {
            return Invalid("Window coordinates must be finite.");
        }

        return Enum.IsDefined(options.InitialState) ? null : Invalid($"Unknown window state '{options.InitialState}'.");

        static bool IsPositive(double? value) => value is not { } v || (double.IsFinite(v) && v > 0);

        static NativeError Invalid(string message) => NativeError.Failed(NativeCapabilities.Windows, NativeErrorCodes.InvalidArgument, message);
    }
}

/// <summary>
/// A window opened by <see cref="FakeWindowManager"/>. Operations change its state and raise the matching events; closing
/// raises <see cref="Closing"/> (handlers may cancel) and then <see cref="Closed"/>.
/// </summary>
/// <remarks>
/// Operations on a closed window return <see cref="NativeResultStatus.Unavailable"/> with <see cref="NativeErrorCodes.NotFound"/>;
/// operations missing from <see cref="SupportedOperations"/> return <see cref="NativeResultStatus.Unsupported"/>.
/// </remarks>
public sealed class FakeNativeWindow : INativeWindow
{
    private readonly Lock _gate = new();
    private readonly FakeWindowManager _manager;
    private string _title;
    private NativeWindowState _state;
    private WindowBounds _bounds;
    private bool _focused;
    private bool _visible = true;
    private bool _alwaysOnTop;
    private bool _closed;

    internal FakeNativeWindow(FakeWindowManager manager, string id, WindowOptions options, bool isMainWindow, WindowBounds bounds)
    {
        _manager = manager;
        Id = id;
        Options = options;
        IsMainWindow = isMainWindow;
        _title = options.Title ?? string.Empty;
        _state = options.InitialState;
        _bounds = bounds;
        _alwaysOnTop = options.AlwaysOnTop;
    }

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <inheritdoc />
    public event EventHandler? BoundsChanged;

    /// <inheritdoc />
    public event EventHandler? FocusChanged;

    /// <inheritdoc />
    public event EventHandler? VisibilityChanged;

    /// <inheritdoc />
    public event EventHandler<WindowClosingEventArgs>? Closing;

    /// <inheritdoc />
    public event EventHandler? Closed;

    /// <inheritdoc />
    public string Id { get; }

    /// <summary>The options the window was opened with (root component, parameters, route, owner, ...).</summary>
    public WindowOptions Options { get; }

    /// <inheritdoc />
    public bool IsMainWindow { get; }

    /// <inheritdoc />
    /// <remarks>Settable, so tests can simulate hosts with restricted windows.</remarks>
    public WindowOperations SupportedOperations { get; set; } = WindowOperations.All;

    /// <inheritdoc />
    public string Title
    {
        get
        {
            lock (_gate)
            {
                return _title;
            }
        }
    }

    /// <inheritdoc />
    public NativeWindowState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <inheritdoc />
    public WindowBounds Bounds
    {
        get
        {
            lock (_gate)
            {
                return _bounds;
            }
        }
    }

    /// <inheritdoc />
    public bool IsFocused
    {
        get
        {
            lock (_gate)
            {
                return _focused;
            }
        }
    }

    /// <inheritdoc />
    public bool IsVisible
    {
        get
        {
            lock (_gate)
            {
                return _visible;
            }
        }
    }

    /// <summary>Whether the window is kept above other windows.</summary>
    public bool IsAlwaysOnTop
    {
        get
        {
            lock (_gate)
            {
                return _alwaysOnTop;
            }
        }
    }

    /// <summary>Whether the window has been closed.</summary>
    public bool IsClosed
    {
        get
        {
            lock (_gate)
            {
                return _closed;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<NativeResult> SetTitleAsync(string title, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(title);
        return Run(WindowOperations.SetTitle, cancellationToken: cancellationToken, operation: () =>
        {
            lock (_gate)
            {
                _title = title;
            }

            return NativeResult.Ok;
        });
    }

    /// <inheritdoc />
    public ValueTask<NativeResult> SetBoundsAsync(WindowBounds bounds, CancellationToken cancellationToken = default) =>
        Run(WindowOperations.Move | WindowOperations.Resize, cancellationToken: cancellationToken, operation: () =>
        {
            if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) ||
                bounds.Width <= 0 || bounds.Height <= 0)
            {
                return NativeError.Failed(NativeCapabilities.Windows, NativeErrorCodes.InvalidArgument, "Window bounds must be finite with a positive width and height.");
            }

            Change(ref _bounds, bounds, BoundsChanged);
            return NativeResult.Ok;
        });

    /// <inheritdoc />
    public ValueTask<NativeResult> SetStateAsync(NativeWindowState state, CancellationToken cancellationToken = default) =>
        Run(RequiredFor(state), cancellationToken: cancellationToken, operation: () =>
        {
            if (!Enum.IsDefined(state))
            {
                return NativeError.Failed(NativeCapabilities.Windows, NativeErrorCodes.InvalidArgument, $"Unknown window state '{state}'.");
            }

            Change(ref _state, state, StateChanged);
            return NativeResult.Ok;
        });

    /// <inheritdoc />
    public ValueTask<NativeResult> SetAlwaysOnTopAsync(bool alwaysOnTop, CancellationToken cancellationToken = default) =>
        Run(WindowOperations.AlwaysOnTop, cancellationToken: cancellationToken, operation: () =>
        {
            lock (_gate)
            {
                _alwaysOnTop = alwaysOnTop;
            }

            return NativeResult.Ok;
        });

    /// <inheritdoc />
    /// <remarks>Restores a minimized window and removes focus from every other window of the manager.</remarks>
    public ValueTask<NativeResult> FocusAsync(CancellationToken cancellationToken = default) =>
        Run(WindowOperations.Focus, cancellationToken: cancellationToken, operation: () =>
        {
            SimulateUserFocus();
            return NativeResult.Ok;
        });

    /// <inheritdoc />
    public ValueTask<NativeResult> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) =>
        Run(WindowOperations.Visibility, cancellationToken: cancellationToken, operation: () =>
        {
            if (!visible)
            {
                SetFocused(false);
            }

            Change(ref _visible, visible, VisibilityChanged);
            return NativeResult.Ok;
        });

    /// <inheritdoc />
    /// <remarks>Succeeds even when a <see cref="Closing"/> handler cancels: like the real hosts, it only requests closing.</remarks>
    public ValueTask<NativeResult> CloseAsync(CancellationToken cancellationToken = default) =>
        Run(WindowOperations.Close, cancellationToken: cancellationToken, operation: () =>
        {
            _ = SimulateUserClose();
            return NativeResult.Ok;
        });

    /// <inheritdoc />
    public bool TryGetPlatformHandle(out nint handle)
    {
        handle = 0;
        return false;
    }

    /// <summary>
    /// Simulates the user activating the window (clicking it), regardless of <see cref="SupportedOperations"/>: a minimized
    /// window is restored and every other window of the manager loses focus. Does nothing for a closed window.
    /// </summary>
    public void SimulateUserFocus()
    {
        if (IsClosed)
        {
            return;
        }

        if (State == NativeWindowState.Minimized)
        {
            Change(ref _state, NativeWindowState.Normal, StateChanged);
        }

        _manager.Activate(this);
    }

    /// <summary>Simulates the user closing the window (title-bar close button), regardless of <see cref="SupportedOperations"/>.</summary>
    /// <returns><see langword="true"/> when the window closed; <see langword="false"/> when a <see cref="Closing"/> handler cancelled.</returns>
    public bool SimulateUserClose()
    {
        if (IsClosed)
        {
            return true;
        }

        var closing = new WindowClosingEventArgs();
        Closing?.Invoke(this, closing);
        if (closing.Cancel)
        {
            return false;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return true;
            }

            _closed = true;
            _visible = false;
        }

        SetFocused(false);
        Closed?.Invoke(this, EventArgs.Empty);
        _manager.OnClosed(this);
        return true;
    }

    internal void SetFocused(bool focused) => Change(ref _focused, focused, FocusChanged);

    private static WindowOperations RequiredFor(NativeWindowState state) => state switch
    {
        NativeWindowState.Minimized => WindowOperations.Minimize,
        NativeWindowState.Maximized => WindowOperations.Maximize,
        NativeWindowState.FullScreen => WindowOperations.FullScreen,
        _ => WindowOperations.None,
    };

    private ValueTask<NativeResult> Run(WindowOperations required, Func<NativeResult> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsClosed)
        {
            return ValueTask.FromResult<NativeResult>(NativeError.Unavailable(NativeCapabilities.Windows, NativeErrorCodes.NotFound, $"Window '{Id}' has been closed."));
        }

        if ((SupportedOperations & required) != required)
        {
            return ValueTask.FromResult<NativeResult>(NativeError.Unsupported(NativeCapabilities.Windows, $"Window '{Id}' does not support {required} in this host."));
        }

        return ValueTask.FromResult(operation());
    }

    private void Change<T>(ref T field, T value, EventHandler? handler)
    {
        lock (_gate)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
        }

        handler?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>An <see cref="IScreenService"/> that reports <see cref="Displays"/> (one 1920x1080 primary display by default).</summary>
public sealed class FakeScreenService() : FakeCapability(NativeCapabilities.Screens), IScreenService
{
    /// <summary>The display reported by default: 1920x1080 at scale 1, with a 40 DIP task bar at the bottom.</summary>
    public static DisplayInfo DefaultDisplay { get; } = new("display-1", "Fake display", new WindowBounds(0, 0, 1920, 1080), new WindowBounds(0, 0, 1920, 1040), 1.0, true);

    /// <summary>The connected displays. An empty list makes <see cref="GetDisplaysAsync"/> report no display, like a headless session.</summary>
    public IReadOnlyList<DisplayInfo> Displays { get; set; } = [DefaultDisplay];

    /// <inheritdoc />
    public ValueTask<NativeResult<IReadOnlyList<DisplayInfo>>> GetDisplaysAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Record(nameof(GetDisplaysAsync));
        if (StatusError() is { } error)
        {
            return ValueTask.FromResult<NativeResult<IReadOnlyList<DisplayInfo>>>(error);
        }

        var displays = Displays;
        return ValueTask.FromResult(displays.Count == 0
            ? NativeResult.Failure<IReadOnlyList<DisplayInfo>>(NativeError.Unavailable(Capability, NativeErrorCodes.HardwareUnavailable, "No display is connected."))
            : NativeResult.Success(displays));
    }
}

/// <summary>
/// An <see cref="IGlobalShortcutService"/> that tracks registrations and lets tests press shortcuts with
/// <see cref="SimulatePressAsync(KeyGesture)"/>. Registering a gesture that is already registered (by this app or, after
/// <see cref="SimulateRegisteredByAnotherApplication(KeyGesture)"/>, by another one) fails with <see cref="NativeErrorCodes.Conflict"/>.
/// </summary>
public sealed class FakeGlobalShortcutService() : FakeCapability(NativeCapabilities.GlobalShortcuts), IGlobalShortcutService
{
    private readonly Lock _gate = new();
    private readonly List<Registration> _registrations = [];
    private readonly HashSet<KeyGesture> _foreign = [];

    /// <summary>The platform used to resolve <see cref="KeyModifiers.CommandOrControl"/>. Defaults to Windows.</summary>
    public NativePlatform Platform { get; set; } = NativePlatform.Windows;

    /// <inheritdoc />
    public IReadOnlyList<KeyGesture> RegisteredGestures
    {
        get
        {
            lock (_gate)
            {
                return [.. _registrations.Select(static r => r.Gesture)];
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<IGlobalShortcutRegistration>> RegisterAsync(KeyGesture gesture, Func<ValueTask> callback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        cancellationToken.ThrowIfCancellationRequested();
        Record(nameof(RegisterAsync));
        if (StatusError() is { } error)
        {
            return ValueTask.FromResult<NativeResult<IGlobalShortcutRegistration>>(error);
        }

        var resolved = gesture.Resolve(Platform);
        if (resolved.Key == NativeKey.None)
        {
            return ValueTask.FromResult<NativeResult<IGlobalShortcutRegistration>>(
                NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "A global shortcut needs a key."));
        }

        lock (_gate)
        {
            if (_foreign.Contains(resolved) || _registrations.Exists(r => r.Gesture == resolved))
            {
                return ValueTask.FromResult<NativeResult<IGlobalShortcutRegistration>>(
                    NativeError.Failed(Capability, NativeErrorCodes.Conflict, $"'{resolved}' is already registered as a global shortcut (by this or another application)."));
            }

            var registration = new Registration(this, resolved, callback);
            _registrations.Add(registration);
            return ValueTask.FromResult(NativeResult.Success<IGlobalShortcutRegistration>(registration));
        }
    }

    /// <summary>Marks a gesture as owned by another application, so registering it fails with <see cref="NativeErrorCodes.Conflict"/>.</summary>
    public FakeGlobalShortcutService SimulateRegisteredByAnotherApplication(KeyGesture gesture)
    {
        lock (_gate)
        {
            _foreign.Add(gesture.Resolve(Platform));
        }

        return this;
    }

    /// <summary>Marks a gesture such as <c>Ctrl+Shift+K</c> as owned by another application.</summary>
    public FakeGlobalShortcutService SimulateRegisteredByAnotherApplication(string gesture) => SimulateRegisteredByAnotherApplication(KeyGesture.Parse(gesture));

    /// <summary>Simulates the user pressing a shortcut. Exceptions thrown by the callback propagate.</summary>
    /// <returns><see langword="true"/> when a registration handled the gesture.</returns>
    public async ValueTask<bool> SimulatePressAsync(KeyGesture gesture)
    {
        var resolved = gesture.Resolve(Platform);
        Registration? registration;
        lock (_gate)
        {
            registration = _registrations.Find(r => r.Gesture == resolved);
        }

        if (registration is null)
        {
            return false;
        }

        await registration.Callback().ConfigureAwait(false);
        return true;
    }

    /// <summary>Simulates the user pressing a shortcut such as <c>Ctrl+Shift+K</c>.</summary>
    /// <returns><see langword="true"/> when a registration handled the gesture.</returns>
    public ValueTask<bool> SimulatePressAsync(string gesture) => SimulatePressAsync(KeyGesture.Parse(gesture));

    private void Remove(Registration registration)
    {
        lock (_gate)
        {
            _registrations.Remove(registration);
        }
    }

    private sealed class Registration(FakeGlobalShortcutService owner, KeyGesture gesture, Func<ValueTask> callback) : IGlobalShortcutRegistration
    {
        public KeyGesture Gesture { get; } = gesture;

        public Func<ValueTask> Callback { get; } = callback;

        public ValueTask DisposeAsync()
        {
            owner.Remove(this);
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A context menu shown through <see cref="FakeNativeMenuService.ShowContextMenuAsync"/>.</summary>
/// <param name="Menu">The menu.</param>
/// <param name="ScreenX">The requested horizontal position, if any.</param>
/// <param name="ScreenY">The requested vertical position, if any.</param>
/// <param name="WindowId">The requested owner window, if any.</param>
public sealed record FakeContextMenu(NativeMenu Menu, double? ScreenX, double? ScreenY, string? WindowId);

/// <summary>
/// An <see cref="INativeMenuService"/> that stores application menus per window and answers context menus from
/// <see cref="ContextMenuChoices"/> (dismissed by default). Choosing an item, through a scripted choice or
/// <see cref="SimulateInvokeAsync"/>, runs its <see cref="NativeMenuItem.OnInvoked"/> callback and raises
/// <see cref="ItemInvoked"/> before the call returns.
/// </summary>
/// <remarks>Exceptions thrown by item callbacks propagate, so failures are visible in tests.</remarks>
public sealed class FakeNativeMenuService() : FakeCapability(NativeCapabilities.Menus), INativeMenuService
{
    private readonly ConcurrentDictionary<string, NativeMenu> _menus = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<FakeContextMenu> _contextMenus = new();

    /// <inheritdoc />
    public event EventHandler<MenuItemInvokedEventArgs>? ItemInvoked;

    /// <summary>The application menu (set without a window id).</summary>
    public NativeMenu? ApplicationMenu => GetApplicationMenu(null);

    /// <summary>The ids chosen in context menus; defaults to the user dismissing the menu (<see cref="NativeResultStatus.Cancelled"/>).</summary>
    public ResultQueue<string> ContextMenuChoices { get; } = new(NativeError.Cancelled(NativeCapabilities.Menus, "The context menu was dismissed."));

    /// <summary>The context menus shown, in order.</summary>
    public IReadOnlyList<FakeContextMenu> ShownContextMenus => [.. _contextMenus];

    /// <summary>Gets the menu set for a window, or the application menu when <paramref name="windowId"/> is <see langword="null"/>.</summary>
    public NativeMenu? GetApplicationMenu(string? windowId) => _menus.GetValueOrDefault(windowId ?? string.Empty);

    /// <summary>Makes the next context menu choose the item with <paramref name="itemId"/>.</summary>
    public FakeNativeMenuService ChoosesItem(string itemId)
    {
        ContextMenuChoices.EnqueueSuccess(itemId);
        return this;
    }

    /// <inheritdoc />
    public ValueTask<NativeResult> SetApplicationMenuAsync(NativeMenu? menu, string? windowId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Record(nameof(SetApplicationMenuAsync));
        if (StatusError() is { } error)
        {
            return ValueTask.FromResult<NativeResult>(error);
        }

        if (menu is null)
        {
            _menus.TryRemove(windowId ?? string.Empty, out _);
        }
        else
        {
            _menus[windowId ?? string.Empty] = menu;
        }

        return ValueTask.FromResult(NativeResult.Ok);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">A scripted choice is not an enabled, invokable item of <paramref name="menu"/>.</exception>
    public async ValueTask<NativeResult<string>> ShowContextMenuAsync(NativeMenu menu, double? screenX = null, double? screenY = null, string? windowId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(menu);
        cancellationToken.ThrowIfCancellationRequested();
        Record(nameof(ShowContextMenuAsync));
        if (StatusError() is { } error)
        {
            return error;
        }

        if (menu.Items.Count == 0)
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "The menu has no items.");
        }

        _contextMenus.Enqueue(new FakeContextMenu(menu, screenX, screenY, windowId));
        var choice = ContextMenuChoices.Next();
        if (!choice.IsSuccess)
        {
            return choice;
        }

        if (menu.Find(choice.Value!) is not { } item || !IsInvokable(item))
        {
            throw new InvalidOperationException($"The scripted choice '{choice.Value}' is not an enabled item of the menu.");
        }

        await InvokeAsync(item, windowId).ConfigureAwait(false);
        return choice;
    }

    /// <summary>Simulates the user choosing an item of the application menu set for <paramref name="windowId"/>.</summary>
    /// <returns><see langword="true"/> when an enabled item with the id exists and was invoked.</returns>
    public async ValueTask<bool> SimulateInvokeAsync(string itemId, string? windowId = null)
    {
        ArgumentNullException.ThrowIfNull(itemId);
        if (GetApplicationMenu(windowId)?.Find(itemId) is not { } item || !IsInvokable(item))
        {
            return false;
        }

        await InvokeAsync(item, windowId).ConfigureAwait(false);
        return true;
    }

    internal static bool IsInvokable(NativeMenuItem item) =>
        item.IsEnabled && item.Kind is not (NativeMenuItemKind.Separator or NativeMenuItemKind.Submenu);

    private async ValueTask InvokeAsync(NativeMenuItem item, string? windowId)
    {
        if (item.OnInvoked is { } callback)
        {
            await callback().ConfigureAwait(false);
        }

        ItemInvoked?.Invoke(this, new MenuItemInvokedEventArgs(item, windowId));
    }
}

/// <summary>An <see cref="ITrayService"/> that keeps <see cref="FakeTrayIcon"/>s in memory.</summary>
public sealed class FakeTrayService() : FakeCapability(NativeCapabilities.Tray), ITrayService
{
    private readonly Lock _gate = new();
    private readonly List<FakeTrayIcon> _icons = [];

    /// <summary>Results for <see cref="CreateAsync"/>; a queued failure is returned instead of creating an icon. Defaults to success.</summary>
    public ResultQueue CreateResults { get; } = new();

    /// <inheritdoc />
    public IReadOnlyList<ITrayIcon> Icons => FakeIcons;

    /// <summary>The icons as their fake type.</summary>
    public IReadOnlyList<FakeTrayIcon> FakeIcons
    {
        get
        {
            lock (_gate)
            {
                return [.. _icons];
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<ITrayIcon>> CreateAsync(TrayIconOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        Record(nameof(CreateAsync));
        if (StatusError() is { } error)
        {
            return ValueTask.FromResult<NativeResult<ITrayIcon>>(error);
        }

        if (CreateResults.Next() is { IsSuccess: false } scripted)
        {
            return ValueTask.FromResult(NativeResult.Failure<ITrayIcon>(scripted.Error!));
        }

        var icon = new FakeTrayIcon(this, options);
        lock (_gate)
        {
            _icons.Add(icon);
        }

        return ValueTask.FromResult(NativeResult.Success<ITrayIcon>(icon));
    }

    internal void Remove(FakeTrayIcon icon)
    {
        lock (_gate)
        {
            _icons.Remove(icon);
        }
    }
}

/// <summary>A tray icon created by <see cref="FakeTrayService"/>. Tests raise clicks and choose menu items with the <c>Simulate*</c> methods.</summary>
public sealed class FakeTrayIcon : ITrayIcon
{
    private readonly Lock _gate = new();
    private readonly FakeTrayService _owner;
    private string _tooltip;
    private string? _iconPath;
    private ReadOnlyMemory<byte>? _iconData;
    private NativeMenu? _menu;
    private bool _disposed;

    internal FakeTrayIcon(FakeTrayService owner, TrayIconOptions options)
    {
        _owner = owner;
        Id = options.Id ?? Guid.NewGuid().ToString("N");
        _tooltip = options.Tooltip;
        _iconPath = options.IconPath;
        _iconData = options.IconData;
        _menu = options.Menu;
    }

    /// <inheritdoc />
    public event EventHandler? Clicked;

    /// <inheritdoc />
    public event EventHandler? DoubleClicked;

    /// <inheritdoc />
    public string Id { get; }

    /// <summary>The tooltip.</summary>
    public string Tooltip
    {
        get
        {
            lock (_gate)
            {
                return _tooltip;
            }
        }
    }

    /// <summary>The icon file, if one was set.</summary>
    public string? IconPath
    {
        get
        {
            lock (_gate)
            {
                return _iconPath;
            }
        }
    }

    /// <summary>The icon bytes, if they were set.</summary>
    public ReadOnlyMemory<byte>? IconData
    {
        get
        {
            lock (_gate)
            {
                return _iconData;
            }
        }
    }

    /// <summary>The context menu.</summary>
    public NativeMenu? Menu
    {
        get
        {
            lock (_gate)
            {
                return _menu;
            }
        }
    }

    /// <summary>Whether the icon has been removed.</summary>
    public bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<NativeResult> SetTooltipAsync(string tooltip, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tooltip);
        return Update(cancellationToken: cancellationToken, update: () => _tooltip = tooltip);
    }

    /// <inheritdoc />
    public ValueTask<NativeResult> SetIconAsync(string? iconPath, ReadOnlyMemory<byte>? iconData = null, CancellationToken cancellationToken = default) =>
        Update(cancellationToken: cancellationToken, update: () =>
        {
            _iconPath = iconPath;
            _iconData = iconData;
        });

    /// <inheritdoc />
    public ValueTask<NativeResult> SetMenuAsync(NativeMenu? menu, CancellationToken cancellationToken = default) =>
        Update(cancellationToken: cancellationToken, update: () => _menu = menu);

    /// <summary>Simulates a primary-button click.</summary>
    public void SimulateClick() => Clicked?.Invoke(this, EventArgs.Empty);

    /// <summary>Simulates a double click.</summary>
    public void SimulateDoubleClick() => DoubleClicked?.Invoke(this, EventArgs.Empty);

    /// <summary>Simulates choosing an item of the icon's menu. Exceptions thrown by the callback propagate.</summary>
    /// <returns><see langword="true"/> when an enabled item with the id exists and was invoked.</returns>
    public async ValueTask<bool> SimulateMenuItemAsync(string itemId)
    {
        ArgumentNullException.ThrowIfNull(itemId);
        if (Menu?.Find(itemId) is not { } item || !FakeNativeMenuService.IsInvokable(item))
        {
            return false;
        }

        if (item.OnInvoked is { } callback)
        {
            await callback().ConfigureAwait(false);
        }

        return true;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        _owner.Remove(this);
        return ValueTask.CompletedTask;
    }

    private ValueTask<NativeResult> Update(Action update, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.FromResult<NativeResult>(NativeError.Unavailable(NativeCapabilities.Tray, NativeErrorCodes.NotFound, $"Tray icon '{Id}' has been removed."));
            }

            update();
        }

        return ValueTask.FromResult(NativeResult.Ok);
    }
}

/// <summary>
/// An <see cref="IDesktopFileOperations"/> that records requests instead of touching the shell. It enforces the same URI
/// policy as the desktop adapter (only <c>http</c>, <c>https</c>, <c>mailto</c> and <see cref="AllowedUriSchemes"/>; never
/// <c>file</c>, <c>javascript</c>, <c>vbscript</c> or <c>data</c>) and rejects empty paths. Paths are not checked for existence.
/// </summary>
public sealed class FakeDesktopFileOperations() : FakeCapability(NativeCapabilities.DesktopFiles, "reveal", "open-path", "open-uri", "trash"), IDesktopFileOperations
{
    private static readonly string[] s_defaultSchemes = ["http", "https", "mailto"];
    private static readonly string[] s_forbiddenSchemes = ["file", "javascript", "vbscript", "data"];
    private readonly ConcurrentQueue<string> _revealed = new();
    private readonly ConcurrentQueue<string> _opened = new();
    private readonly ConcurrentQueue<Uri> _openedUris = new();
    private readonly ConcurrentQueue<string> _trashed = new();

    /// <summary>Additional URI schemes <see cref="OpenUriAsync"/> may open (like <c>DesktopNativeOptions.AllowedUriSchemes</c>).</summary>
    public ISet<string> AllowedUriSchemes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Results for every operation that passes validation; a queued failure is returned instead of recording. Defaults to success.</summary>
    public ResultQueue Results { get; } = new();

    /// <summary>Paths revealed in the file manager.</summary>
    public IReadOnlyList<string> RevealedPaths => [.. _revealed];

    /// <summary>Paths opened with their default application.</summary>
    public IReadOnlyList<string> OpenedPaths => [.. _opened];

    /// <summary>URIs opened.</summary>
    public IReadOnlyList<Uri> OpenedUris => [.. _openedUris];

    /// <summary>Paths moved to the trash.</summary>
    public IReadOnlyList<string> TrashedPaths => [.. _trashed];

    /// <inheritdoc />
    public ValueTask<NativeResult> RevealInFileManagerAsync(string path, CancellationToken cancellationToken = default) =>
        RunPath(nameof(RevealInFileManagerAsync), path, _revealed, cancellationToken);

    /// <inheritdoc />
    public ValueTask<NativeResult> OpenPathAsync(string path, CancellationToken cancellationToken = default) =>
        RunPath(nameof(OpenPathAsync), path, _opened, cancellationToken);

    /// <inheritdoc />
    public ValueTask<NativeResult> MoveToTrashAsync(string path, CancellationToken cancellationToken = default) =>
        RunPath(nameof(MoveToTrashAsync), path, _trashed, cancellationToken);

    /// <inheritdoc />
    public ValueTask<NativeResult> OpenUriAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();
        Record(nameof(OpenUriAsync));
        if (StatusError() is { } error)
        {
            return ValueTask.FromResult<NativeResult>(error);
        }

        if (ValidateUri(uri) is { } invalid)
        {
            return ValueTask.FromResult<NativeResult>(invalid);
        }

        var result = Results.Next();
        if (result.IsSuccess)
        {
            _openedUris.Enqueue(uri);
        }

        return ValueTask.FromResult(result);
    }

    private ValueTask<NativeResult> RunPath(string operation, string path, ConcurrentQueue<string> log, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Record(operation);
        if (StatusError() is { } error)
        {
            return ValueTask.FromResult<NativeResult>(error);
        }

        if (string.IsNullOrWhiteSpace(path) || path.AsSpan().IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return ValueTask.FromResult<NativeResult>(NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "A non-empty, valid path is required."));
        }

        var result = Results.Next();
        if (result.IsSuccess)
        {
            log.Enqueue(path);
        }

        return ValueTask.FromResult(result);
    }

    private NativeError? ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "Only absolute URIs can be opened.");
        }

        var scheme = uri.Scheme;
        if (s_forbiddenSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase))
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, $"The '{scheme}' scheme is never opened. Use OpenPathAsync for local files.");
        }

        if (!s_defaultSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase) && !AllowedUriSchemes.Contains(scheme))
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, $"The '{scheme}' scheme is not allowed; add it to AllowedUriSchemes to permit it.");
        }

        return scheme is "http" or "https" && string.IsNullOrEmpty(uri.Host)
            ? NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "HTTP(S) URIs must have a host.")
            : null;
    }
}

/// <summary>
/// An <see cref="IProtocolRegistrationService"/> that keeps registrations in memory. Scheme validation matches the desktop
/// adapter (RFC 3986 syntax, schemes owned by browsers or the OS are rejected, names are case-insensitive).
/// </summary>
public sealed class FakeProtocolRegistrationService() : FakeCapability(NativeCapabilities.ProtocolRegistration, "registry"), IProtocolRegistrationService
{
    private static readonly HashSet<string> s_reserved = new(
    [
        "about", "blob", "chrome", "chrome-extension", "cid", "data", "edge", "file", "ftp", "ftps", "help", "hcp", "http", "https",
        "its", "jar", "javascript", "ldap", "ldaps", "mailto", "mid", "mk", "moz-extension", "news", "nntp", "res", "resource",
        "search", "search-ms", "sftp", "shell", "smb", "telnet", "urn", "view-source", "vbscript", "ws", "wss",
    ], StringComparer.OrdinalIgnoreCase);

    private static readonly string[] s_reservedPrefixes = ["ms-", "microsoft", "windows", "x-scheme-handler"];
    private readonly ConcurrentDictionary<string, ProtocolRegistrationOptions> _registrations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _foreign = new(StringComparer.Ordinal);

    /// <summary>The schemes registered to this application (lowercase), with the options they were registered with.</summary>
    public IReadOnlyDictionary<string, ProtocolRegistrationOptions> Registrations => _registrations;

    /// <summary>Marks a scheme as registered to another application: registering it fails with <see cref="NativeErrorCodes.Conflict"/>.</summary>
    public FakeProtocolRegistrationService SimulateRegisteredByAnotherApplication(string scheme)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        _foreign[scheme.ToLowerInvariant()] = 0;
        return this;
    }

    /// <inheritdoc />
    public ValueTask<NativeResult> RegisterAsync(string scheme, ProtocolRegistrationOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Record(nameof(RegisterAsync));
        if (Gate(scheme, out var normalized) is { } error)
        {
            return ValueTask.FromResult<NativeResult>(error);
        }

        if (_foreign.ContainsKey(normalized))
        {
            return ValueTask.FromResult<NativeResult>(NativeError.Failed(Capability, NativeErrorCodes.Conflict, $"The '{normalized}' scheme is registered to another application."));
        }

        _registrations[normalized] = options ?? new ProtocolRegistrationOptions();
        return ValueTask.FromResult(NativeResult.Ok);
    }

    /// <inheritdoc />
    public ValueTask<NativeResult> UnregisterAsync(string scheme, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Record(nameof(UnregisterAsync));
        if (Gate(scheme, out var normalized) is { } error)
        {
            return ValueTask.FromResult<NativeResult>(error);
        }

        _registrations.TryRemove(normalized, out _);
        return ValueTask.FromResult(NativeResult.Ok);
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<bool>> IsRegisteredAsync(string scheme, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Record(nameof(IsRegisteredAsync));
        if (Gate(scheme, out var normalized) is { } error)
        {
            return ValueTask.FromResult<NativeResult<bool>>(error);
        }

        return ValueTask.FromResult(NativeResult.Success(_registrations.ContainsKey(normalized)));
    }

    private NativeError? Gate(string scheme, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(scheme) || scheme.Length > 64 || !char.IsAsciiLetter(scheme[0]) ||
            !scheme.All(static c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.'))
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument,
                $"'{scheme}' is not a valid URI scheme: use a letter followed by letters, digits, '+', '-' or '.' (at most 64 characters).");
        }

        var lower = scheme.ToLowerInvariant();
        if (s_reserved.Contains(lower) || s_reservedPrefixes.Any(p => lower.StartsWith(p, StringComparison.Ordinal)))
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, $"The '{lower}' scheme is reserved for the browser or operating system and cannot be registered.");
        }

        normalized = lower;
        return StatusError();
    }
}
