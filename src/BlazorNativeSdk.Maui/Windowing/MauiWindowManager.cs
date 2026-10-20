#if WINDOWS || MACCATALYST
using BlazorNativeSdk.Desktop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MauiApplication = Microsoft.Maui.Controls.Application;
using DisplayInfo = BlazorNativeSdk.Desktop.DisplayInfo;
using MauiWindow = Microsoft.Maui.Controls.Window;

namespace BlazorNativeSdk.Maui;

/// <summary>
/// <see cref="IWindowManager"/> for MAUI apps on Windows and Mac Catalyst. Tracks every MAUI window (the main window has the id
/// <see cref="MauiNativeOptions.MainWindowId"/>) and opens additional windows with
/// <c>Application.Current.OpenWindow(new Window(new BlazorWindowPage(...)))</c>.
/// </summary>
/// <remarks>
/// <para>
/// Ids are unique: opening an id that is open focuses the existing window, and concurrent requests for the same id share one
/// window. With <see cref="WindowOptions.PersistPlacement"/> and an explicit <see cref="WindowOptions.Id"/>, the placement is
/// restored when the window opens and saved (debounced) when it moves, resizes or closes, through
/// <see cref="IWindowPlacementStore"/>, which validates restored placements against the connected displays.
/// </para>
/// <para>
/// Windows: geometry, owner, resizability and always-on-top are applied before the window is first shown (no flicker);
/// windows without coordinates are centered on the display of their owner or the main window. Mac Catalyst: scenes are created
/// asynchronously and need <c>UIApplicationSupportsMultipleScenes</c>; geometry needs macOS 13; owners, initial states and
/// always-on-top are not available.
/// </para>
/// </remarks>
internal sealed partial class MauiWindowManager : IWindowManager, IDisposable, IAsyncDisposable
{
    private const string AdapterName = "maui";
    private static readonly TimeSpan s_lastWindowSaveTimeout = TimeSpan.FromSeconds(2);

    private readonly MauiWindowTracker _tracker;
    private readonly MauiUIDispatcher _dispatcher;
    private readonly IWindowPlacementStore _placements;
    private readonly IScreenService _screens;
    private readonly IOptions<MauiNativeOptions> _options;
    private readonly IOptions<NativeSdkOptions> _sdkOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MauiWindowManager> _logger;
    private readonly Lock _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<string, Task<NativeResult<INativeWindow>>> _pending = new(StringComparer.Ordinal);
#if WINDOWS
    private readonly Task<WindowPlacement?>? _mainPlacement;
#endif
    private int _disposed;

    /// <summary>Creates the window manager.</summary>
    public MauiWindowManager(
        MauiWindowTracker tracker,
        MauiUIDispatcher dispatcher,
        IWindowPlacementStore placements,
        IScreenService screens,
        IOptions<MauiNativeOptions> options,
        IOptions<NativeSdkOptions> sdkOptions,
        ILogger<MauiWindowManager> logger,
        TimeProvider? timeProvider = null)
    {
        _tracker = tracker;
        _dispatcher = dispatcher;
        _placements = placements;
        _screens = screens;
        _options = options;
        _sdkOptions = sdkOptions;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

#if WINDOWS
        // Mac Catalyst restores scene frames itself; on Windows start reading the main window's placement now, so it is
        // usually available when the window is created and can be applied before it is first shown.
        if (options.Value.PersistMainWindowPlacement)
        {
            var mainId = options.Value.MainWindowId;
            _mainPlacement = Task.Run(() => LoadPlacementAsync(mainId, CancellationToken.None));
        }
#endif

        tracker.Enable();
        tracker.WindowAdded += OnWindowAdded;
        tracker.WindowRemoved += OnWindowRemoved;
        tracker.WindowChanged += OnWindowChanged;
        AdoptTrackedWindows();
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
                return [.. _entries.Select(static e => e.Native)];
            }
        }
    }

    /// <inheritdoc />
    public INativeWindow? MainWindow
    {
        get
        {
            lock (_gate)
            {
                return _entries.FirstOrDefault(static e => e.Native.IsMainWindow)?.Native;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<INativeWindow>? WindowOpened;

    /// <inheritdoc />
    public event EventHandler<INativeWindow>? WindowClosed;

    /// <inheritdoc />
    public INativeWindow? FindWindow(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            return FindEntryLocked(id)?.Native;
        }
    }

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return ValueTask.FromResult(CapabilityStatus.Unavailable(Capability, NativeErrorCodes.HostNotReady, "The window manager has been disposed.", AdapterName));
        }

#if WINDOWS
        return ValueTask.FromResult(CapabilityStatus.Available(
            Capability,
            AdapterName,
            MauiWindowFeatures.MultipleWindows,
            MauiWindowFeatures.PlacementPersistence,
            MauiWindowFeatures.MoveAndResize,
            MauiWindowFeatures.WindowStates,
            MauiWindowFeatures.AlwaysOnTop,
            MauiWindowFeatures.OwnedWindows,
            MauiWindowFeatures.CancelableClose));
#else
        return GetSceneStatusAsync(cancellationToken);
#endif
    }

#if MACCATALYST
    private async ValueTask<CapabilityStatus> GetSceneStatusAsync(CancellationToken cancellationToken)
    {
        bool supportsScenes;
        try
        {
            supportsScenes = await _dispatcher.InvokeAsync(static () => UIKit.UIApplication.SharedApplication.SupportsMultipleScenes)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return CapabilityStatus.Unknown(Capability, "The UI thread is not available to query scene support.", AdapterName);
        }

        if (!supportsScenes)
        {
            return CapabilityStatus.Unavailable(Capability, NativeErrorCodes.MissingDeclaration, SceneManifestMessage, AdapterName);
        }

        return MacCatalystMauiWindow.SupportsGeometry
            ? CapabilityStatus.Available(Capability, AdapterName, MauiWindowFeatures.MultipleWindows, MauiWindowFeatures.PlacementPersistence, MauiWindowFeatures.MoveAndResize)
            : CapabilityStatus.Available(Capability, AdapterName, MauiWindowFeatures.MultipleWindows);
    }
#endif

    /// <inheritdoc />
    public async ValueTask<NativeResult<INativeWindow>> OpenAsync(WindowOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0)
        {
            return NativeError.Unavailable(Capability, NativeErrorCodes.HostNotReady, "The window manager has been disposed.");
        }

        if (Validate(options) is { } invalid)
        {
            return invalid;
        }

        if (!_dispatcher.IsAvailable)
        {
            return NativeError.Unavailable(Capability, NativeErrorCodes.HostNotReady, "No MAUI application is running yet.");
        }

        var id = options.Id ?? MauiWindowIds.CreateRandom();
        INativeWindow? existing;
        Task<NativeResult<INativeWindow>>? inFlight = null;
        TaskCompletionSource<NativeResult<INativeWindow>>? opening = null;
        lock (_gate)
        {
            existing = FindEntryLocked(id)?.Native;
            if (existing is null && !_pending.TryGetValue(id, out inFlight))
            {
                opening = new TaskCompletionSource<NativeResult<INativeWindow>>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending[id] = opening.Task;
            }
        }

        if (existing is not null)
        {
            var focused = await existing.FocusAsync(cancellationToken).ConfigureAwait(false);
            if (!focused.IsSuccess)
            {
                LogFocusExistingFailed(_logger, id, focused.Error.Message);
            }

            return NativeResult.Success(existing);
        }

        if (opening is null)
        {
            return await inFlight!.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        NativeResult<INativeWindow> result;
        try
        {
            result = await OpenCoreAsync(id, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Complete(id, opening, NativeError.Failed(Capability, NativeErrorCodes.Cancelled, $"The request that was opening window '{id}' was cancelled."));
            throw;
        }
        catch (Exception ex)
        {
            result = NativeError.FromException(Capability, ex);
        }

        Complete(id, opening, result);
        return result;
    }

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
        Entry[] entries;
        lock (_gate)
        {
            entries = [.. _entries];
            _entries.Clear();
        }

        foreach (var entry in entries)
        {
            entry.Saver?.Dispose();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void Complete(string id, TaskCompletionSource<NativeResult<INativeWindow>> opening, NativeResult<INativeWindow> result)
    {
        lock (_gate)
        {
            _pending.Remove(id);
        }

        opening.TrySetResult(result);
    }

    private NativeError? Validate(WindowOptions options)
    {
        if (options.Id is not null && !MauiWindowIds.IsValid(options.Id))
        {
            return Invalid($"Window ids must be 1-{MauiWindowIds.MaxLength} characters without control characters.");
        }

        if (!IsPositive(options.Width) || !IsPositive(options.Height) || !IsPositive(options.MinWidth) || !IsPositive(options.MinHeight))
        {
            return Invalid("Window sizes must be finite and positive.");
        }

        if ((options.X is { } x && !double.IsFinite(x)) || (options.Y is { } y && !double.IsFinite(y)))
        {
            return Invalid("Window coordinates must be finite.");
        }

        if (!Enum.IsDefined(options.InitialState))
        {
            return Invalid($"Unknown window state '{options.InitialState}'.");
        }

        if (options.RootComponent is null)
        {
            if (_options.Value.DefaultRootComponent is null)
            {
                return Invalid($"The window has no root component: set {nameof(WindowOptions)}.{nameof(WindowOptions.RootComponent)} or {nameof(MauiNativeOptions)}.{nameof(MauiNativeOptions.DefaultRootComponent)}.");
            }

            if (options.Route is { Length: > 0 } route && !BlazorWindowPage.IsAppRoute(route))
            {
                return Invalid($"The window route '{route}' must be an app-relative path such as '/settings'.");
            }
        }

        return null;

        static bool IsPositive(double? value) => value is not { } v || (double.IsFinite(v) && v > 0);
    }

    private NativeError Invalid(string message) => NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, message);

    private async Task<NativeResult<INativeWindow>> OpenCoreAsync(string id, WindowOptions options, CancellationToken cancellationToken)
    {
        WindowPlacement? placement = null;
        if (options.PersistPlacement && options.Id is not null)
        {
            placement = await LoadPlacementAsync(id, cancellationToken).ConfigureAwait(false);
        }

        var request = new WindowRequest(id, options, placement);
        var opened = await _dispatcher.InvokeAsync(() => OpenOnUIThread(request)).ConfigureAwait(false);
        if (!opened.TryGetValue(out var window))
        {
            return opened.Error!;
        }

        var timeout = _options.Value.WindowOpenTimeout;
        try
        {
            var native = await request.Ready.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            LogWindowOpened(_logger, id);
            return NativeResult.Success(native);
        }
        catch (TimeoutException)
        {
            _tracker.Forget(window);
            return NativeError.Failed(Capability, NativeErrorCodes.Timeout, $"The platform did not create window '{id}' within {timeout.TotalSeconds:0.#} s.");
        }
    }

    private NativeResult<MauiWindow> OpenOnUIThread(WindowRequest request)
    {
        if (MauiApplication.Current is not { } application)
        {
            return NativeError.Unavailable(Capability, NativeErrorCodes.HostNotReady, "No MAUI application is running.");
        }

        _tracker.EnsureAttached();
        var options = request.Options;
        if (options.OwnerId is { } ownerId && FindWindow(ownerId) is null)
        {
            return NativeError.Unavailable(Capability, NativeErrorCodes.NotFound, $"No open window has the id '{ownerId}'.");
        }

#if MACCATALYST
        if (!UIKit.UIApplication.SharedApplication.SupportsMultipleScenes)
        {
            return NativeError.Unavailable(Capability, NativeErrorCodes.MissingDeclaration, SceneManifestMessage);
        }
#endif

        var mauiOptions = _options.Value;
        BlazorWindowPage page;
        try
        {
            page = new BlazorWindowPage(request.Id, options, mauiOptions);
        }
        catch (ArgumentException ex)
        {
            return Invalid(ex.Message);
        }

        var window = new MauiWindow(page) { Title = options.Title ?? _sdkOptions.Value.DisplayName };
        if (options.MinWidth is { } minWidth)
        {
            window.MinimumWidth = minWidth;
        }

        if (options.MinHeight is { } minHeight)
        {
            window.MinimumHeight = minHeight;
        }

#if WINDOWS
        if (!options.Resizable)
        {
            window.IsMaximizable = false;
        }
#else
        PrepareMacGeometry(window, request);
#endif

        mauiOptions.ConfigureWindow?.Invoke(window, page.BlazorWebView, options);
        _tracker.Expect(window, request);
        try
        {
            application.OpenWindow(window);
        }
        catch
        {
            _tracker.Forget(window);
            throw;
        }

        return window;
    }

    private void AdoptTrackedWindows()
    {
        if (_tracker.GetWindows().Count == 0)
        {
            return;
        }

        void Adopt()
        {
            foreach (var tracked in _tracker.GetWindows())
            {
                OnWindowAdded(_tracker, tracked);
            }
        }

        if (_dispatcher.CheckAccess() || !_dispatcher.Post(Adopt))
        {
            Adopt();
        }
    }

    private void OnWindowAdded(object? sender, TrackedWindow tracked)
    {
        // Always on the UI thread (tracker events and adoption), so the check below cannot race with another add.
        if (Volatile.Read(ref _disposed) != 0 || FindEntry(tracked) is not null)
        {
            return;
        }

        var options = _options.Value;
        var request = tracked.Request;
#if WINDOWS
        IMauiNativeWindow native = new MauiWin32Window(tracked, _dispatcher, _logger);
        var persistMain = options.PersistMainWindowPlacement;
#else
        IMauiNativeWindow native = new MacCatalystMauiWindow(tracked, _dispatcher, _logger);
        var persistMain = false;
#endif
        var persistedId = request is null
            ? (tracked.IsMain && persistMain ? tracked.Id : null)
            : (request.Options.PersistPlacement && request.Options.Id is not null ? request.Id : null);

        var entry = new Entry(native, request) { OwnerId = request?.Options.OwnerId };
        lock (_gate)
        {
            _entries.Add(entry);
        }

        if (persistedId is not null)
        {
            entry.Saver = new WindowPlacementSaver(persistedId, native, _placements, _dispatcher, _timeProvider, options.PlacementSaveDelay, _logger);
            entry.Saver.SetBaseline(request?.Placement);
        }

#if WINDOWS
        if (native is MauiWin32Window win32)
        {
            try
            {
                ApplyInitialGeometry(entry, win32);
            }
            catch (Exception ex)
            {
                LogInitialGeometryFailed(_logger, tracked.Id, ex);
            }
        }
#endif

        request?.Ready.TrySetResult(native);
        Raise(WindowOpened, native);
    }

    private void OnWindowChanged(object? sender, TrackedWindowChange change)
    {
        var entry = FindEntry(change.Window);
        if (entry is null)
        {
            return;
        }

        switch (change.Change)
        {
            case WindowChange.Activation:
                entry.Native.NotifyFocusChanged();

                // Activation also resumes a stopped (Mac Catalyst) window.
                entry.Native.NotifyVisibilityChanged();
                if (change.Window.IsActive)
                {
                    ApplyPendingState(entry);
                }

                break;
            case WindowChange.State:
            case WindowChange.Visibility:
                entry.Native.NotifyStateChanged();
                entry.Native.NotifyVisibilityChanged();
                break;
            case WindowChange.Bounds:
                entry.Native.NotifyBoundsChanged();
                break;
        }
    }

    private void OnWindowRemoved(object? sender, TrackedWindow tracked)
    {
        Entry? entry;
        Entry[] owned;
        bool isLast;
        lock (_gate)
        {
            entry = _entries.FirstOrDefault(e => ReferenceEquals(e.Native.Tracked, tracked));
            if (entry is null)
            {
                return;
            }

            _entries.Remove(entry);
            owned = [.. _entries.Where(e => string.Equals(e.OwnerId, entry.Native.Id, StringComparison.Ordinal))];
            isLast = _entries.Count == 0;
        }

#if WINDOWS
        // Owned windows close with their owner. Detach them first so Windows does not destroy them behind WinUI's back.
        foreach (var child in owned)
        {
            child.OwnerId = null;
            if (child.Native is MauiWin32Window ownedWindow)
            {
                ownedWindow.ClearOwner();
            }

            // Closed after the owner's close notification returns (closing windows from inside it re-enters WinUI).
            var mauiWindow = child.Native.Tracked.Window;
            if (!_dispatcher.Post(() => MauiApplication.Current?.CloseWindow(mauiWindow)))
            {
                MauiApplication.Current?.CloseWindow(mauiWindow);
            }
        }
#else
        _ = owned;
#endif

        if (entry.Saver is { } saver)
        {
            var flush = saver.FlushAsync();
            if (isLast)
            {
                // The process usually ends right after its last window closes: give the final write a moment.
                WaitBriefly(flush);
            }

            saver.Dispose();
        }

        entry.Native.NotifyClosed();
        LogWindowClosed(_logger, tracked.Id);
        Raise(WindowClosed, entry.Native);
    }

    private void ApplyPendingState(Entry entry)
    {
        if (entry.PendingState is not { } state)
        {
            return;
        }

        entry.PendingState = null;

        // Not from inside the platform's activation callback: changing the state re-enters the window procedure.
        var native = entry.Native;
        if (!_dispatcher.Post(() => _ = ApplyStateAsync(native, state)))
        {
            _ = ApplyStateAsync(native, state);
        }
    }

    private async Task ApplyStateAsync(INativeWindow window, NativeWindowState state)
    {
        var result = await window.SetStateAsync(state).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            LogInitialStateFailed(_logger, window.Id, state, result.Error.Message);
        }
    }

#if WINDOWS
    private void ApplyInitialGeometry(Entry entry, MauiWin32Window window)
    {
        var request = entry.Request;
        if (request is null)
        {
            if (entry.Saver is not null && _mainPlacement is { } load)
            {
                if (load.IsCompletedSuccessfully)
                {
                    ApplyRestored(entry, window, load.Result, shown: false);
                }
                else
                {
                    _ = ApplyMainPlacementWhenLoadedAsync(entry, window, load);
                }
            }

            return;
        }

        var options = request.Options;
        nint owner = 0;
        if (options.OwnerId is { } ownerId && FindWindow(ownerId) is { } ownerWindow && ownerWindow.TryGetPlatformHandle(out var handle))
        {
            owner = handle;
        }

        window.ConfigureFrame(options.Resizable, options.AlwaysOnTop, owner);
        if (request.Placement is { } placement)
        {
            ApplyRestored(entry, window, placement, shown: false);
            return;
        }

        if (ComputeInitialBounds(window, options, owner) is { } bounds)
        {
            var result = window.ApplyBounds(bounds);
            if (!result.IsSuccess)
            {
                LogInitialGeometryFailed(_logger, window.Id, result.Error.ToException());
            }
        }

        if (options.InitialState != NativeWindowState.Normal)
        {
            entry.PendingState = options.InitialState;
        }
    }

    private void ApplyRestored(Entry entry, MauiWin32Window window, WindowPlacement? placement, bool shown)
    {
        if (placement is null)
        {
            return;
        }

        entry.Saver?.SetBaseline(placement);
        var result = window.ApplyRestoredBounds(placement);
        if (!result.IsSuccess)
        {
            LogInitialGeometryFailed(_logger, window.Id, result.Error.ToException());
            return;
        }

        if (placement.State is NativeWindowState.Maximized or NativeWindowState.FullScreen)
        {
            if (shown)
            {
                _ = ApplyStateAsync(window, placement.State);
            }
            else
            {
                // Changing the state shows the window; WinUI must show (activate) it first.
                entry.PendingState = placement.State;
            }
        }
    }

    private async Task ApplyMainPlacementWhenLoadedAsync(Entry entry, MauiWin32Window window, Task<WindowPlacement?> load)
    {
        var placement = await load.ConfigureAwait(false);
        if (placement is null)
        {
            return;
        }

        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (!window.Tracked.IsClosed)
                {
                    ApplyRestored(entry, window, placement, shown: window.Tracked.HasBeenShown);
                }

                return true;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogInitialGeometryFailed(_logger, window.Id, ex);
        }
    }

    private WindowBounds? ComputeInitialBounds(MauiWin32Window window, WindowOptions options, nint owner)
    {
        var current = window.Bounds;
        var width = options.Width ?? current.Width;
        var height = options.Height ?? current.Height;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        if (options.X is not null || options.Y is not null)
        {
            return new WindowBounds(options.X ?? current.X, options.Y ?? current.Y, width, height);
        }

        // Center on the display of the owner (or the main window), else on the primary display.
        var displays = TryGetDisplays();
        if (displays is not { Count: > 0 })
        {
            return new WindowBounds(current.X, current.Y, width, height);
        }

        WindowBounds? reference = null;
        var referenceWindow = owner != 0 ? FindByHandle(owner) : MainWindow;
        if (referenceWindow is not null && !ReferenceEquals(referenceWindow, window))
        {
            reference = referenceWindow.Bounds;
        }

        var target = reference is { } r
            ? displays.OrderByDescending(d => d.WorkArea.IntersectionArea(r)).ThenByDescending(static d => d.IsPrimary).First()
            : displays.FirstOrDefault(static d => d.IsPrimary) ?? displays[0];
        var work = target.WorkArea;
        width = Math.Min(width, work.Width);
        height = Math.Min(height, work.Height);
        return new WindowBounds(work.X + ((work.Width - width) / 2), work.Y + ((work.Height - height) / 2), width, height);
    }

    private IReadOnlyList<DisplayInfo>? TryGetDisplays()
    {
        try
        {
            // The Windows screen service enumerates monitors synchronously; never block the UI thread if it does not.
            var pending = _screens.GetDisplaysAsync();
            return pending.IsCompletedSuccessfully && pending.Result.TryGetValue(out var displays) ? displays : null;
        }
        catch (Exception ex)
        {
            LogInitialGeometryFailed(_logger, "*", ex);
            return null;
        }
    }

    private INativeWindow? FindByHandle(nint handle)
    {
        lock (_gate)
        {
            return _entries.FirstOrDefault(e => e.Native.TryGetPlatformHandle(out var h) && h == handle)?.Native;
        }
    }
#else
    private void PrepareMacGeometry(MauiWindow window, WindowRequest request)
    {
        var options = request.Options;
        WindowBounds? bounds = request.Placement?.Bounds;
        if (bounds is null && (options.Width is not null || options.Height is not null || options.X is not null || options.Y is not null))
        {
            // MAUI applies scene geometry only when X, Y, width and height are all known. Without coordinates, center the
            // window over the main window (both are in macOS screen points).
            var width = options.Width ?? 800;
            var height = options.Height ?? 600;
            var main = MainWindow?.Bounds;
            var x = options.X ?? (main is { Width: > 0 } m ? m.X + ((m.Width - width) / 2) : double.NaN);
            var y = options.Y ?? (main is { Height: > 0 } n ? n.Y + ((n.Height - height) / 2) : double.NaN);
            if (double.IsFinite(x) && double.IsFinite(y))
            {
                bounds = new WindowBounds(Math.Max(0, x), Math.Max(0, y), width, height);
            }
        }

        if (bounds is { } b && MacCatalystMauiWindow.IsValid(b))
        {
            if (MacCatalystMauiWindow.SupportsGeometry)
            {
                MacCatalystMauiWindow.ApplyGeometry(window, b);
            }

            if (!options.Resizable)
            {
                window.MinimumWidth = window.MaximumWidth = b.Width;
                window.MinimumHeight = window.MaximumHeight = b.Height;
            }
        }
        else if (!options.Resizable && options.Width is { } fixedWidth && options.Height is { } fixedHeight)
        {
            window.MinimumWidth = window.MaximumWidth = fixedWidth;
            window.MinimumHeight = window.MaximumHeight = fixedHeight;
        }

        if (options.AlwaysOnTop || options.OwnerId is not null || options.InitialState != NativeWindowState.Normal)
        {
            LogIgnoredMacOptions(_logger, request.Id);
        }
    }

    private const string SceneManifestMessage =
        "Opening windows on Mac Catalyst requires multiple scenes: add UIApplicationSceneManifest with " +
        "UIApplicationSupportsMultipleScenes = true and a scene delegate deriving from MauiUISceneDelegate to Info.plist.";
#endif

    private async Task<WindowPlacement?> LoadPlacementAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            return await _placements.LoadAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogLoadPlacementFailed(_logger, id, ex);
            return null;
        }
    }

    private static void WaitBriefly(Task task)
    {
        try
        {
            task.Wait(s_lastWindowSaveTimeout);
        }
        catch (AggregateException)
        {
            // Logged by the saver.
        }
    }

    private Entry? FindEntry(TrackedWindow tracked)
    {
        lock (_gate)
        {
            return _entries.FirstOrDefault(e => ReferenceEquals(e.Native.Tracked, tracked));
        }
    }

    private Entry? FindEntryLocked(string id) =>
        _entries.FirstOrDefault(e => string.Equals(e.Native.Id, id, StringComparison.Ordinal));

    private void Raise(EventHandler<INativeWindow>? handler, INativeWindow window)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList().Cast<EventHandler<INativeWindow>>())
        {
            try
            {
                subscriber(this, window);
            }
            catch (Exception ex)
            {
                LogSubscriberFailed(_logger, window.Id, ex);
            }
        }
    }

    [LoggerMessage(EventId = 8501, Level = LogLevel.Debug, Message = "Opened window '{WindowId}'.")]
    private static partial void LogWindowOpened(ILogger logger, string windowId);

    [LoggerMessage(EventId = 8502, Level = LogLevel.Debug, Message = "Window '{WindowId}' closed.")]
    private static partial void LogWindowClosed(ILogger logger, string windowId);

    [LoggerMessage(EventId = 8503, Level = LogLevel.Debug, Message = "Window '{WindowId}' is already open but could not be focused: {Reason}")]
    private static partial void LogFocusExistingFailed(ILogger logger, string windowId, string reason);

    [LoggerMessage(EventId = 8504, Level = LogLevel.Warning, Message = "Loading the placement of window '{WindowId}' failed; it opens with its default placement.")]
    private static partial void LogLoadPlacementFailed(ILogger logger, string windowId, Exception exception);

    [LoggerMessage(EventId = 8505, Level = LogLevel.Warning, Message = "Applying the initial geometry of window '{WindowId}' failed.")]
    private static partial void LogInitialGeometryFailed(ILogger logger, string windowId, Exception exception);

    [LoggerMessage(EventId = 8506, Level = LogLevel.Warning, Message = "Applying the initial state {State} of window '{WindowId}' failed: {Reason}")]
    private static partial void LogInitialStateFailed(ILogger logger, string windowId, NativeWindowState state, string reason);

    [LoggerMessage(EventId = 8507, Level = LogLevel.Error, Message = "A window manager event handler failed for window '{WindowId}'.")]
    private static partial void LogSubscriberFailed(ILogger logger, string windowId, Exception exception);

#if MACCATALYST
    [LoggerMessage(EventId = 8508, Level = LogLevel.Debug, Message = "Window '{WindowId}': AlwaysOnTop, OwnerId and InitialState are not available on Mac Catalyst and were ignored.")]
    private static partial void LogIgnoredMacOptions(ILogger logger, string windowId);
#endif

    private sealed class Entry(IMauiNativeWindow native, WindowRequest? request)
    {
        public IMauiNativeWindow Native { get; } = native;

        public WindowRequest? Request { get; } = request;

        public WindowPlacementSaver? Saver { get; set; }

        public NativeWindowState? PendingState { get; set; }

        public string? OwnerId { get; set; }
    }
}
#endif
