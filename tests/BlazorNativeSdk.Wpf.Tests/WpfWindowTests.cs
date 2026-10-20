using System.Windows;
using System.Windows.Interop;
using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.Lifecycle;
using BlazorNativeSdk.WindowsPlatform;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorNativeSdk.Wpf.Tests;

/// <summary>
/// Drives real WPF windows (without a BlazorWebView) on a dedicated STA dispatcher thread. Skipped without an interactive
/// desktop (services, some CI agents).
/// </summary>
public sealed class WpfWindowTests : IDisposable
{
    private readonly TestHost _host = new();

    [Fact]
    public async Task Attached_main_window_restores_placement_and_round_trips_bounds_state_and_close()
    {
        Assert.SkipUnless(Environment.UserInteractive, "Requires an interactive desktop.");
        var ct = TestContext.Current.CancellationToken;

        await using var provider = _host.Build(o => o.PlacementSaveDelay = TimeSpan.FromMilliseconds(50));
        var manager = provider.GetRequiredService<WpfWindowManager>();
        var lifecycle = Assert.IsType<WpfAppLifecycle>(provider.GetRequiredService<IAppLifecycle>());
        var placements = provider.GetRequiredService<IWindowPlacementStore>();
        var displays = (await provider.GetRequiredService<IScreenService>().GetDisplaysAsync(ct)).GetValueOrThrow();
        var work = displays.Single(d => d.IsPrimary).WorkArea;
        var saved = new WindowBounds(Math.Round(work.X) + 40, Math.Round(work.Y) + 50, 420, 300);
        await placements.SaveAsync("main", new WindowPlacement { Bounds = saved, State = NativeWindowState.Normal }, ct);

        var opened = new List<string>();
        var closedIds = new List<string>();
        manager.WindowOpened += (_, w) => opened.Add(w.Id);
        manager.WindowClosed += (_, w) => closedIds.Add(w.Id);

        await using var sta = await StaDispatcherThread.StartAsync();
        Window? window = null;
        WpfNativeWindow native;
        try
        {
            native = await sta.InvokeAsync(() =>
            {
                // CenterScreen from XAML must not override the saved placement.
                window = new Window
                {
                    Title = "bns-wpf-test",
                    Width = 300,
                    Height = 200,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                };
                return manager.AttachMainWindow(window).GetValueOrThrow();
            });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Assert.Skip("WPF windows cannot be created in this session: " + ex.Message);
            return;
        }

        Assert.Equal(["main"], opened);
        Assert.Same(native, manager.MainWindow);
        Assert.Same(native, manager.FindWindow("main"));
        Assert.True(native.IsMainWindow);
        Assert.Equal("bns-wpf-test", native.Title);
        Assert.False(native.TryGetPlatformHandle(out _));
        Assert.Equal(WindowOperations.All, native.SupportedOperations);

        // Attaching is idempotent; a second main window or a duplicate id conflicts.
        Assert.Same(native, (await sta.InvokeAsync(() => manager.AttachMainWindow(window!))).Value);
        var conflict = await sta.InvokeAsync(() => manager.AttachMainWindow(new Window(), "other"));
        Assert.Equal(NativeErrorCodes.Conflict, conflict.Error!.Code);
        Assert.Equal(NativeErrorCodes.Conflict, (await sta.InvokeAsync(() => manager.AttachWindow(new Window(), "main"))).Error!.Code);

        // Creating the native window (still hidden) applies the saved placement.
        var hwnd = await sta.InvokeAsync(() => new WindowInteropHelper(window!).EnsureHandle());
        Assert.True(native.TryGetPlatformHandle(out var handle));
        Assert.Equal(hwnd, handle);
        Assert.Equal(hwnd, provider.GetRequiredService<IWindowsHostBridge>().MainWindowHandle);
        Assert.False(native.IsVisible);
        BoundsAssert.Close(saved, native.Bounds);
        Assert.Equal(AppLifecycleState.Launching, lifecycle.State);

        // Bounds round-trip from a background thread (marshalled to the window's dispatcher).
        var requested = saved with { X = saved.X + 30, Width = 480 };
        Assert.True((await native.SetBoundsAsync(requested, ct)).IsSuccess);
        BoundsAssert.Close(requested, native.Bounds);

        // State round-trip. A hidden window is shown through WPF.
        var states = new List<NativeWindowState>();
        native.StateChanged += (_, _) => states.Add(native.State);
        // WpfNativeWindow's visibility notifications are the INativeWindow event (no separate WPF-specific event).
        var visibilityChanges = 0;
        INativeWindow contract = native;
        contract.VisibilityChanged += (_, _) => visibilityChanges++;

        Assert.True((await native.SetStateAsync(NativeWindowState.Normal, ct)).IsSuccess);
        Assert.True(native.IsVisible);
        Assert.True(visibilityChanges > 0);
        Assert.True(await sta.InvokeAsync(() => window!.IsVisible));
        Assert.Equal(AppLifecycleState.Inactive, lifecycle.State);

        // Opening the id of an open window focuses it instead of creating another one.
        var reopened = await manager.OpenAsync(new WindowOptions { Id = "main", Route = "/elsewhere" }, ct);
        Assert.Same(native, reopened.Value);
        Assert.Single(manager.Windows);

        Assert.True((await native.SetStateAsync(NativeWindowState.Maximized, ct)).IsSuccess);
        Assert.Equal(NativeWindowState.Maximized, native.State);
        Assert.Equal(WindowState.Maximized, await sta.InvokeAsync(() => window!.WindowState));
        BoundsAssert.Close(requested, native.GetPlacement()!.Bounds);

        Assert.True((await native.SetStateAsync(NativeWindowState.Minimized, ct)).IsSuccess);
        Assert.Equal(NativeWindowState.Minimized, native.State);
        Assert.Equal(AppLifecycleState.Background, lifecycle.State);

        Assert.True((await native.SetStateAsync(NativeWindowState.Normal, ct)).IsSuccess);
        Assert.Equal(NativeWindowState.Normal, native.State);
        Assert.Equal(WindowState.Normal, await sta.InvokeAsync(() => window!.WindowState));
        Assert.Equal(AppLifecycleState.Inactive, lifecycle.State);
        BoundsAssert.Close(requested, native.Bounds);

        Assert.True((await native.SetStateAsync(NativeWindowState.FullScreen, ct)).IsSuccess);
        Assert.Equal(NativeWindowState.FullScreen, native.State);
        Assert.True((await native.SetStateAsync(NativeWindowState.Normal, ct)).IsSuccess);
        Assert.Equal(NativeWindowState.Normal, native.State);
        BoundsAssert.Close(requested, native.Bounds);

        // StateChanged is raised once per transition, including full screen (which WPF's WindowState does not report).
        Assert.Equal(
            [NativeWindowState.Maximized, NativeWindowState.Minimized, NativeWindowState.Normal, NativeWindowState.FullScreen, NativeWindowState.Normal],
            states);

        // Title and topmost go through WPF so its own state stays consistent.
        Assert.True((await native.SetTitleAsync("renamed", ct)).IsSuccess);
        Assert.Equal("renamed", native.Title);
        Assert.Equal("renamed", await sta.InvokeAsync(() => window!.Title));
        Assert.True((await native.SetAlwaysOnTopAsync(true, ct)).IsSuccess);
        Assert.True(await sta.InvokeAsync(() => window!.Topmost));
        Assert.True((await native.SetAlwaysOnTopAsync(false, ct)).IsSuccess);

        // Hiding the only window moves the app to the background.
        Assert.True((await native.SetVisibleAsync(false, ct)).IsSuccess);
        Assert.False(native.IsVisible);
        Assert.Equal(AppLifecycleState.Background, lifecycle.State);
        Assert.True((await native.SetVisibleAsync(true, ct)).IsSuccess);
        Assert.True(native.IsVisible);

        // Moves are saved after the debounce delay.
        var moved = requested with { Y = requested.Y + 20 };
        Assert.True((await native.SetBoundsAsync(moved, ct)).IsSuccess);
        await Wait.UntilAsync(async () => await placements.LoadAsync("main", ct) is { } p && Math.Abs(p.Bounds.Y - moved.Y) <= 1, ct);

        // Closing can be cancelled by a handler.
        var cancelNext = true;
        var closingCount = 0;
        native.Closing += (_, e) =>
        {
            closingCount++;
            if (cancelNext)
            {
                e.Cancel = true;
                cancelNext = false;
            }
        };
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        native.Closed += (_, _) => closed.TrySetResult();

        Assert.True((await native.CloseAsync(ct)).IsSuccess);
        await Wait.UntilAsync(() => closingCount == 1, ct);
        await sta.IdleAsync();
        Assert.False(native.IsClosed);
        Assert.True(native.IsVisible);

        var final = requested with { Width = 500 };
        Assert.True((await native.SetBoundsAsync(final, ct)).IsSuccess);
        Assert.True((await native.CloseAsync(ct)).IsSuccess);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        Assert.True(native.IsClosed);
        Assert.False(native.TryGetPlatformHandle(out _));
        Assert.Empty(manager.Windows);
        Assert.Null(manager.MainWindow);
        Assert.Equal(["main"], closedIds);
        Assert.Equal(NativeErrorCodes.NotFound, (await native.SetTitleAsync("x", ct)).Error!.Code);
        Assert.Equal(AppLifecycleState.Background, lifecycle.State);

        // The placement was saved when the window closed.
        await manager.FlushPlacementsAsync(ct);
        var persisted = await placements.LoadAsync("main", ct);
        Assert.NotNull(persisted);
        BoundsAssert.Close(final, persisted.Bounds);
        Assert.Equal(NativeWindowState.Normal, persisted.State);
    }

    [Fact]
    public async Task Secondary_windows_attach_without_becoming_main_and_restore_maximized()
    {
        Assert.SkipUnless(Environment.UserInteractive, "Requires an interactive desktop.");
        var ct = TestContext.Current.CancellationToken;

        await using var provider = _host.Build();
        var manager = provider.GetRequiredService<WpfWindowManager>();
        var placements = provider.GetRequiredService<IWindowPlacementStore>();
        var work = (await provider.GetRequiredService<IScreenService>().GetDisplaysAsync(ct)).GetValueOrThrow().Single(d => d.IsPrimary).WorkArea;
        var saved = new WindowBounds(Math.Round(work.X) + 60, Math.Round(work.Y) + 70, 360, 260);
        await placements.SaveAsync("tool", new WindowPlacement { Bounds = saved, State = NativeWindowState.Maximized }, ct);

        await using var sta = await StaDispatcherThread.StartAsync();
        var (window, native) = await sta.InvokeAsync(() =>
        {
            // Maximized windows are always activated when shown (WPF and Win32 cannot maximize without activation).
            var w = new Window { Title = "bns-wpf-tool", ShowInTaskbar = false };
            return (w, manager.AttachWindow(w, "tool").GetValueOrThrow());
        });

        Assert.False(native.IsMainWindow);
        Assert.Null(manager.MainWindow);
        Assert.Same(native, manager.FindWindow("tool"));

        await sta.InvokeAsync(window.Show);
        Assert.Equal(NativeWindowState.Maximized, native.State);
        BoundsAssert.Close(saved, native.GetPlacement()!.Bounds);

        // Closing through WPF directly is observed as well.
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        native.Closed += (_, _) => closed.TrySetResult();
        await sta.InvokeAsync(window.Close);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Empty(manager.Windows);

        await manager.FlushPlacementsAsync(ct);
        var persisted = await placements.LoadAsync("tool", ct);
        Assert.Equal(NativeWindowState.Maximized, persisted!.State);
        BoundsAssert.Close(saved, persisted.Bounds);
    }

    [Fact]
    public async Task Full_screen_placement_is_restored_once_an_attached_window_is_shown()
    {
        Assert.SkipUnless(Environment.UserInteractive, "Requires an interactive desktop.");
        var ct = TestContext.Current.CancellationToken;

        await using var provider = _host.Build();
        var manager = provider.GetRequiredService<WpfWindowManager>();
        var placements = provider.GetRequiredService<IWindowPlacementStore>();
        var work = (await provider.GetRequiredService<IScreenService>().GetDisplaysAsync(ct)).GetValueOrThrow().Single(d => d.IsPrimary).WorkArea;
        var saved = new WindowBounds(Math.Round(work.X) + 80, Math.Round(work.Y) + 90, 400, 280);
        await placements.SaveAsync("presenter", new WindowPlacement { Bounds = saved, State = NativeWindowState.FullScreen }, ct);

        await using var sta = await StaDispatcherThread.StartAsync();
        var (window, native) = await sta.InvokeAsync(() =>
        {
            var w = new Window { Title = "bns-wpf-presenter", ShowActivated = false, ShowInTaskbar = false };
            var attached = manager.AttachWindow(w, "presenter").GetValueOrThrow();

            // The app creates the native window before showing it.
            _ = new WindowInteropHelper(w).EnsureHandle();
            return (w, attached);
        });

        await sta.IdleAsync();
        Assert.False(native.IsVisible);
        Assert.NotEqual(NativeWindowState.FullScreen, native.State);

        await sta.InvokeAsync(window.Show);
        await Wait.UntilAsync(() => native.State == NativeWindowState.FullScreen, ct);
        BoundsAssert.Close(saved, native.GetPlacement()!.Bounds);

        Assert.True((await native.SetStateAsync(NativeWindowState.Normal, ct)).IsSuccess);
        BoundsAssert.Close(saved, native.Bounds);
        Assert.True((await native.CloseAsync(ct)).IsSuccess);
        await Wait.UntilAsync(() => native.IsClosed, ct);
    }

    [Fact]
    public async Task Open_reports_configuration_errors_without_creating_windows()
    {
        Assert.SkipUnless(Environment.UserInteractive, "Requires an interactive desktop.");
        var ct = TestContext.Current.CancellationToken;

        await using var provider = _host.Build();
        var manager = provider.GetRequiredService<WpfWindowManager>();
        await using var sta = await StaDispatcherThread.StartAsync();
        var main = await sta.InvokeAsync(() => manager.AttachMainWindow(new Window { ShowActivated = false, ShowInTaskbar = false }, persistPlacement: false).GetValueOrThrow());

        // An attached window provides the dispatcher.
        var status = await manager.GetStatusAsync(ct);
        Assert.Equal(CapabilityAvailability.Available, status.Availability);
        Assert.True(status.Supports("multi-window"));

        // The main window hosts no BlazorWebView and no default root component is configured.
        var noRoot = await manager.OpenAsync(new WindowOptions { Id = "settings", Route = "/settings" }, ct);
        Assert.Equal(NativeResultStatus.Failed, noRoot.Status);
        Assert.Equal(NativeErrorCodes.InvalidArgument, noRoot.Error!.Code);

        var noOwner = await manager.OpenAsync(new WindowOptions { Id = "child", OwnerId = "missing", RootComponent = typeof(WpfWindowTests) }, ct);
        Assert.Equal(NativeResultStatus.Unavailable, noOwner.Status);
        Assert.Equal(NativeErrorCodes.NotFound, noOwner.Error!.Code);

        Assert.Equal([main], manager.Windows);
        Assert.Equal(1, await sta.InvokeAsync(() => main.Window.OwnedWindows.Count + 1));
        Assert.True((await main.CloseAsync(ct)).IsSuccess);
        await Wait.UntilAsync(() => main.IsClosed, ct);
    }

    public void Dispose() => _host.Dispose();
}
