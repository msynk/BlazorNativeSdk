using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;
using BlazorNativeSdk.Storage;
using BlazorNativeSdk.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace BlazorNativeSdk.Tests;

public sealed class FakeRegistrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AddFakes_registers_a_shared_fake_for_every_capability()
    {
        await using var provider = new ServiceCollection()
            .AddBlazorNativeSdk(sdk => sdk.AddFakes())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<ICapabilityRegistry>();

        foreach (var capability in NativeCapabilities.All)
        {
            var implementation = registry.GetProvider(capability);
            Assert.NotNull(implementation);
            var status = await registry.GetStatusAsync(capability, Ct);
            Assert.True(status.IsAvailable, status.ToString());

            // Deep links are served by Core's deterministic activation pipeline; everything else by a fake.
            var expected = capability == NativeCapabilities.DeepLinks ? typeof(ActivationService).Assembly : typeof(FakeCapability).Assembly;
            Assert.True(expected == implementation.GetType().Assembly, $"{capability}: {implementation.GetType()}");
        }

        Assert.Same(provider.GetRequiredService<IWindowManager>(), provider.GetRequiredService<FakeWindowManager>());
        Assert.Same(provider.GetRequiredService<IIpcService>(), provider.GetRequiredService<FakeIpcService>());
        Assert.Same(provider.GetRequiredService<ISingleInstanceService>(), provider.GetRequiredService<FakeSingleInstanceService>());
        Assert.Same(provider.GetRequiredService<IBackgroundWorkScheduler>(), provider.GetRequiredService<FakeBackgroundWorkScheduler>());
        Assert.Same(provider.GetRequiredService<IAppStorage>(), provider.GetRequiredService<FakeAppStorage>());
        Assert.Same(provider.GetRequiredService<INativeStateStore>(), provider.GetRequiredService<InMemoryStateStore>());
        Assert.IsType<InMemoryWorkflowCheckpointStore>(provider.GetRequiredService<IWorkflowCheckpointStore>());
    }

    [Fact]
    public async Task Fakes_can_simulate_hosts_without_a_capability()
    {
        await using var provider = new ServiceCollection().AddBlazorNativeSdk(sdk => sdk.AddFakes()).BuildServiceProvider();
        var tray = provider.GetRequiredService<FakeTrayService>();
        tray.SimulateUnsupported("No notification area on a phone.");

        var result = await provider.GetRequiredService<ITrayService>().CreateAsync(new TrayIconOptions { Tooltip = "x" }, Ct);

        Assert.Equal(NativeResultStatus.Unsupported, result.Status);
        Assert.Equal("No notification area on a phone.", result.Error!.Message);
        Assert.Empty(tray.Icons);
    }
}

public sealed class StorageFakeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void App_storage_maps_directories_under_its_root_and_cleans_up()
    {
        string root;
        using (var storage = new FakeAppStorage())
        {
            root = storage.RootDirectory;
            var appData = storage.GetDirectory(AppDirectory.AppData);
            Assert.Equal(Path.Combine(root, "AppData"), appData.Value);
            Assert.True(Directory.Exists(appData.Value));

            storage.SimulateUnsupportedDirectory(AppDirectory.Documents, "No documents here.");
            var documents = storage.GetDirectory(AppDirectory.Documents);
            Assert.Equal(NativeResultStatus.Unsupported, documents.Status);
            Assert.Equal("No documents here.", documents.Error!.Message);
            Assert.Equal(NativeErrorCodes.InvalidArgument, storage.GetDirectory((AppDirectory)42).Error!.Code);
            Assert.Equal(3, storage.Calls.Count);
        }

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Checkpoints_expire_with_the_clock()
    {
        // SaveStateAsync stamps checkpoints with the system clock, so start the fake clock there.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryWorkflowCheckpointStore(time);
        await store.SaveStateAsync("checkout", new CheckoutState("SKU-1", 2), TestJsonContext.Default.CheckoutState, "/checkout", TimeSpan.FromMinutes(5), cancellationToken: Ct);
        await store.SaveAsync(new WorkflowCheckpoint { WorkflowId = "forever", StateJson = "{}", SavedAt = time.GetUtcNow().AddMinutes(-1) }, Ct);

        Assert.Equal(new CheckoutState("SKU-1", 2), await store.LoadStateAsync("checkout", TestJsonContext.Default.CheckoutState, Ct));
        Assert.Equal(["checkout", "forever"], (await store.GetPendingAsync(Ct)).Select(c => c.WorkflowId));

        time.Advance(TimeSpan.FromMinutes(6));

        Assert.Null(await store.GetAsync("checkout", Ct));
        Assert.Equal(["forever"], (await store.GetPendingAsync(Ct)).Select(c => c.WorkflowId));
        Assert.Equal(["forever"], store.Checkpoints.Keys);
        Assert.True((await store.GetStatusAsync(Ct)).IsAvailable);
    }
}

public sealed class BackgroundFakeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Scheduler_follows_the_existing_work_policies_and_cancellation()
    {
        var scheduler = new FakeBackgroundWorkScheduler();
        var changes = new List<WorkInfo>();
        scheduler.WorkChanged += (_, info) => changes.Add(info);

        var first = await scheduler.ScheduleAsync(new WorkRequest { Id = "sync", WorkerName = "sync", Input = new Dictionary<string, string> { ["a"] = "1" } }, Ct);
        var kept = await scheduler.ScheduleAsync(new WorkRequest { Id = "sync", WorkerName = "sync", ExistingPolicy = ExistingWorkPolicy.Keep, Input = new Dictionary<string, string> { ["a"] = "2" } }, Ct);
        Assert.Equal(WorkStatus.Enqueued, first.Value!.Status);
        Assert.Same(first.Value, kept.Value);
        Assert.Equal("1", scheduler.Requests["sync"].Input["a"]);

        await scheduler.ScheduleAsync(new WorkRequest { Id = "sync", WorkerName = "sync", Input = new Dictionary<string, string> { ["a"] = "3" } }, Ct);
        Assert.Equal("3", scheduler.Requests["sync"].Input["a"]);

        Assert.True((await scheduler.CancelAsync("sync", Ct)).Value);
        Assert.False((await scheduler.CancelAsync("sync", Ct)).Value);
        Assert.Empty((await scheduler.GetWorkAsync(Ct)).Value!);
        Assert.Equal([WorkStatus.Enqueued, WorkStatus.Enqueued, WorkStatus.Cancelled], changes.Select(c => c.Status));

        Assert.Equal(NativeErrorCodes.InvalidArgument, (await scheduler.ScheduleAsync(new WorkRequest { Id = "x", WorkerName = "x", RepeatInterval = TimeSpan.Zero }, Ct)).Error!.Code);
        scheduler.ScheduleResults.EnqueueFailure(NativeError.Unavailable(NativeCapabilities.BackgroundWork, NativeErrorCodes.BackgroundExecutionDenied, "Denied."));
        Assert.Equal(NativeErrorCodes.BackgroundExecutionDenied, (await scheduler.ScheduleAsync(new WorkRequest { Id = "x", WorkerName = "x" }, Ct)).Error!.Code);
    }

    [Fact]
    public async Task Scheduler_applies_retries_backoff_and_periodic_runs()
    {
        var time = new FakeTimeProvider();
        var scheduler = new FakeBackgroundWorkScheduler(timeProvider: time);
        await scheduler.ScheduleAsync(new WorkRequest { Id = "upload", WorkerName = "upload", MaxAttempts = 2, BackoffDelay = TimeSpan.FromSeconds(10) }, Ct);
        await scheduler.ScheduleAsync(new WorkRequest { Id = "refresh", WorkerName = "refresh", RepeatInterval = TimeSpan.FromHours(1) }, Ct);

        var retry = await scheduler.CompleteAsync("upload", WorkResult.Retry("offline"));
        Assert.Equal((WorkStatus.Enqueued, 1, "offline"), (retry.Status, retry.Attempts, retry.LastMessage));
        Assert.Equal(time.GetUtcNow().AddSeconds(10), retry.EarliestNextRun);
        Assert.Equal(WorkStatus.Failed, (await scheduler.CompleteAsync("upload", WorkResult.Retry())).Status);

        var periodic = await scheduler.CompleteAsync("refresh", WorkResult.Success(new Dictionary<string, string> { ["count"] = "3" }));
        Assert.Equal((WorkStatus.Enqueued, 0, "3"), (periodic.Status, periodic.Attempts, periodic.Output["count"]));
        Assert.Equal(time.GetUtcNow().AddHours(1), periodic.EarliestNextRun);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await scheduler.CompleteAsync("missing", WorkResult.Success()));
    }

    [Fact]
    public async Task Scheduler_runs_registered_workers_and_rejects_unknown_ones()
    {
        var services = new ServiceCollection();
        services.AddBackgroundWorker<EchoWorker>("echo");
        await using var provider = services.BuildServiceProvider();
        var scheduler = new FakeBackgroundWorkScheduler(provider);

        Assert.Equal(NativeErrorCodes.InvalidArgument, (await scheduler.ScheduleAsync(new WorkRequest { Id = "w", WorkerName = "missing" }, Ct)).Error!.Code);

        await scheduler.ScheduleAsync(new WorkRequest { Id = "w", WorkerName = "echo", Input = new Dictionary<string, string> { ["value"] = "hi" } }, Ct);
        var info = await scheduler.RunAsync("w", isOsInitiated: true, Ct);

        Assert.Equal(WorkStatus.Succeeded, info.Status);
        Assert.Equal("hi:1:True", info.Output["echo"]);
    }

    [Fact]
    public async Task Transfers_are_driven_by_the_test_and_complete_waiters()
    {
        var transfers = new FakeTransferService();
        var download = await transfers.EnqueueDownloadAsync(new DownloadRequest { Id = "d", Uri = new Uri("https://example.com/a.zip"), DestinationPath = "a.zip" }, Ct);
        Assert.Equal(TransferStatus.Queued, download.Value!.Status);
        Assert.Same(download.Value, (await transfers.EnqueueDownloadAsync(new DownloadRequest { Id = "d", Uri = new Uri("https://example.com/b.zip"), DestinationPath = "b.zip" }, Ct)).Value);

        var waiting = transfers.WaitForCompletionAsync("d", Ct).AsTask();
        Assert.Equal(0.5, transfers.SimulateProgress("d", 50, 100).Progress);
        Assert.False(waiting.IsCompleted);
        transfers.SimulateCompleted("d");
        var completed = await waiting;
        Assert.Equal((TransferStatus.Completed, 100L), (completed.Value!.Status, completed.Value.BytesTransferred));

        await transfers.EnqueueUploadAsync(new UploadRequest { Id = "u", Uri = new Uri("https://example.com/upload"), SourcePath = "photo.jpg" }, Ct);
        transfers.SimulateFailed("u", "Server error", 500);
        var failed = await transfers.WaitForCompletionAsync("u", Ct);
        Assert.Equal((NativeResultStatus.Failed, "Server error"), (failed.Status, failed.Error!.Message));
        Assert.Equal(TransferStatus.Queued, (await transfers.RetryAsync("u", Ct)).Value!.Status);
        Assert.True((await transfers.CancelAsync("u", Ct)).Value);
        Assert.Equal(NativeResultStatus.Cancelled, (await transfers.WaitForCompletionAsync("u", Ct)).Status);

        Assert.Equal(NativeErrorCodes.InvalidArgument, (await transfers.EnqueueDownloadAsync(new DownloadRequest { Uri = new Uri("ftp://example.com/a"), DestinationPath = "a" }, Ct)).Error!.Code);
        Assert.Equal(NativeErrorCodes.NotFound, (await transfers.WaitForCompletionAsync("missing", Ct)).Error!.Code);
        Assert.Equal(2, transfers.Downloads.Count(r => r.Id == "d"));
        Assert.Single(transfers.Uploads);
    }

    private sealed class EchoWorker : IBackgroundWorker
    {
        public ValueTask<WorkResult> ExecuteAsync(WorkContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(WorkResult.Success(new Dictionary<string, string> { ["echo"] = $"{context.Input["value"]}:{context.Attempt}:{context.IsOsInitiated}" }));
    }
}

public sealed class DesktopShellFakeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Window_manager_opens_focuses_and_closes_windows()
    {
        var manager = new FakeWindowManager();
        var main = Assert.IsType<FakeNativeWindow>(manager.MainWindow);
        Assert.True(main.IsFocused);
        var opened = new List<string>();
        var closed = new List<string>();
        manager.WindowOpened += (_, w) => opened.Add(w.Id);
        manager.WindowClosed += (_, w) => closed.Add(w.Id);

        var settings = (await manager.OpenAsync(new WindowOptions { Id = "settings", Title = "Settings", Route = "/settings", Width = 400, Height = 300 }, Ct)).Value!;
        Assert.Equal(("Settings", new WindowBounds(760, 390, 400, 300), true, false), (settings.Title, settings.Bounds, settings.IsFocused, main.IsFocused));
        Assert.Equal("/settings", manager.FindFakeWindow("settings")!.Options.Route);

        await main.FocusAsync(Ct);
        var again = await manager.OpenAsync(new WindowOptions { Id = "settings" }, Ct);
        Assert.Same(settings, again.Value);
        Assert.True(settings.IsFocused);
        Assert.Equal(["settings"], opened);

        var fake = (FakeNativeWindow)settings;
        fake.Closing += (_, e) => e.Cancel = true;
        Assert.True((await settings.CloseAsync(Ct)).IsSuccess);
        Assert.False(fake.IsClosed);

        var other = (await manager.OpenAsync(new WindowOptions { Title = "Untitled" }, Ct)).Value!;
        var stateChanges = 0;
        other.StateChanged += (_, _) => stateChanges++;
        Assert.True((await other.SetStateAsync(NativeWindowState.Maximized, Ct)).IsSuccess);
        Assert.True((await other.CloseAsync(Ct)).IsSuccess);
        Assert.Equal(1, stateChanges);
        Assert.Equal([other.Id], closed);
        Assert.Null(manager.FindWindow(other.Id));
        Assert.Equal(NativeErrorCodes.NotFound, (await other.SetTitleAsync("x", Ct)).Error!.Code);
        Assert.Equal(2, manager.Windows.Count);
    }

    [Fact]
    public async Task Window_manager_validates_options_and_reports_unsupported_operations()
    {
        var manager = new FakeWindowManager();

        Assert.Equal(NativeErrorCodes.InvalidArgument, (await manager.OpenAsync(new WindowOptions { Width = -1 }, Ct)).Error!.Code);
        Assert.Equal(NativeErrorCodes.NotFound, (await manager.OpenAsync(new WindowOptions { OwnerId = "nope" }, Ct)).Error!.Code);
        manager.OpenResults.EnqueueFailure(NativeError.Unavailable(NativeCapabilities.Windows, NativeErrorCodes.HostNotReady, "Not yet."));
        Assert.Equal(NativeErrorCodes.HostNotReady, (await manager.OpenAsync(new WindowOptions(), Ct)).Error!.Code);
        Assert.Equal(3, manager.OpenRequests.Count);

        var main = manager.FakeWindows.Single();
        main.SupportedOperations = WindowOperations.All & ~WindowOperations.FullScreen;
        Assert.Equal(NativeResultStatus.Unsupported, (await main.SetStateAsync(NativeWindowState.FullScreen, Ct)).Status);
        Assert.Equal(NativeErrorCodes.InvalidArgument, (await main.SetBoundsAsync(new WindowBounds(0, 0, 0, 10), Ct)).Error!.Code);
        Assert.False(main.TryGetPlatformHandle(out _));
    }

    [Fact]
    public async Task Screens_global_shortcuts_and_protocols_behave_like_the_adapters()
    {
        var screens = new FakeScreenService();
        Assert.Equal(FakeScreenService.DefaultDisplay, Assert.Single((await screens.GetDisplaysAsync(Ct)).Value!));
        screens.Displays = [];
        Assert.Equal(NativeErrorCodes.HardwareUnavailable, (await screens.GetDisplaysAsync(Ct)).Error!.Code);

        var shortcuts = new FakeGlobalShortcutService().SimulateRegisteredByAnotherApplication("Ctrl+Alt+Delete");
        var pressed = 0;
        var registration = (await shortcuts.RegisterAsync(KeyGesture.Parse("CmdOrCtrl+Shift+K"), () =>
        {
            pressed++;
            return ValueTask.CompletedTask;
        }, Ct)).Value!;
        Assert.Equal(KeyGesture.Parse("Ctrl+Shift+K"), registration.Gesture);
        Assert.Equal(NativeErrorCodes.Conflict, (await shortcuts.RegisterAsync(KeyGesture.Parse("Ctrl+Shift+K"), () => ValueTask.CompletedTask, Ct)).Error!.Code);
        Assert.Equal(NativeErrorCodes.Conflict, (await shortcuts.RegisterAsync(KeyGesture.Parse("Ctrl+Alt+Delete"), () => ValueTask.CompletedTask, Ct)).Error!.Code);
        Assert.True(await shortcuts.SimulatePressAsync("Ctrl+Shift+K"));
        await registration.DisposeAsync();
        Assert.False(await shortcuts.SimulatePressAsync("Ctrl+Shift+K"));
        Assert.Equal(1, pressed);
        Assert.Empty(shortcuts.RegisteredGestures);

        var protocols = new FakeProtocolRegistrationService().SimulateRegisteredByAnotherApplication("taken");
        Assert.True((await protocols.RegisterAsync("MyApp", new ProtocolRegistrationOptions { DisplayName = "My app" }, Ct)).IsSuccess);
        Assert.True((await protocols.IsRegisteredAsync("myapp", Ct)).Value);
        Assert.Equal("My app", protocols.Registrations["myapp"].DisplayName);
        Assert.Equal(NativeErrorCodes.Conflict, (await protocols.RegisterAsync("taken", cancellationToken: Ct)).Error!.Code);
        Assert.Equal(NativeErrorCodes.InvalidArgument, (await protocols.RegisterAsync("https", cancellationToken: Ct)).Error!.Code);
        Assert.Equal(NativeErrorCodes.InvalidArgument, (await protocols.RegisterAsync("1bad", cancellationToken: Ct)).Error!.Code);
        Assert.True((await protocols.UnregisterAsync("myapp", Ct)).IsSuccess);
        Assert.False((await protocols.IsRegisteredAsync("myapp", Ct)).Value);
    }

    [Fact]
    public async Task Menus_and_tray_icons_invoke_items_like_the_user_would()
    {
        var menus = new FakeNativeMenuService();
        var invoked = new List<string>();
        var events = new List<(string Id, string? Window)>();
        menus.ItemInvoked += (_, e) => events.Add((e.Item.Id, e.WindowId));
        var menu = NativeMenu.Create(
            NativeMenuItem.Create("copy", "Copy", () =>
            {
                invoked.Add("copy");
                return ValueTask.CompletedTask;
            }),
            NativeMenuItem.Separator(),
            new NativeMenuItem { Id = "off", Label = "Disabled", IsEnabled = false });

        Assert.Equal(NativeResultStatus.Cancelled, (await menus.ShowContextMenuAsync(menu, 10, 20, cancellationToken: Ct)).Status);
        Assert.Equal("copy", (await menus.ChoosesItem("copy").ShowContextMenuAsync(menu, windowId: "main", cancellationToken: Ct)).Value);
        Assert.Equal(NativeErrorCodes.InvalidArgument, (await menus.ShowContextMenuAsync(NativeMenu.Create(), cancellationToken: Ct)).Error!.Code);
        Assert.Equal((10d, 20d), (menus.ShownContextMenus[0].ScreenX!.Value, menus.ShownContextMenus[0].ScreenY!.Value));

        await menus.SetApplicationMenuAsync(menu, "main", Ct);
        Assert.Same(menu, menus.GetApplicationMenu("main"));
        Assert.True(await menus.SimulateInvokeAsync("copy", "main"));
        Assert.False(await menus.SimulateInvokeAsync("off", "main"));
        Assert.False(await menus.SimulateInvokeAsync("copy"));
        await menus.SetApplicationMenuAsync(null, "main", Ct);
        Assert.Null(menus.GetApplicationMenu("main"));
        Assert.Equal(["copy", "copy"], invoked);
        Assert.Equal([("copy", "main"), ("copy", "main")], events);

        var tray = new FakeTrayService();
        var icon = Assert.IsType<FakeTrayIcon>((await tray.CreateAsync(new TrayIconOptions { Id = "status", Tooltip = "Idle", Menu = menu }, Ct)).Value);
        var clicks = 0;
        icon.Clicked += (_, _) => clicks++;
        icon.SimulateClick();
        Assert.True(await icon.SimulateMenuItemAsync("copy"));
        Assert.True((await icon.SetTooltipAsync("Busy", Ct)).IsSuccess);
        Assert.Equal(("Busy", 1, 3), (icon.Tooltip, clicks, invoked.Count));

        await icon.DisposeAsync();
        Assert.Empty(tray.Icons);
        Assert.Equal(NativeErrorCodes.NotFound, (await icon.SetTooltipAsync("x", Ct)).Error!.Code);
    }

    [Fact]
    public async Task File_operations_record_requests_and_enforce_the_uri_policy()
    {
        var files = new FakeDesktopFileOperations();
        files.AllowedUriSchemes.Add("vscode");

        Assert.True((await files.OpenUriAsync(new Uri("https://example.com"), Ct)).IsSuccess);
        Assert.True((await files.OpenUriAsync(new Uri("vscode://file/x"), Ct)).IsSuccess);
        Assert.Equal(NativeErrorCodes.InvalidArgument, (await files.OpenUriAsync(new Uri("file:///C:/Windows/notepad.exe"), Ct)).Error!.Code);
        Assert.Equal(NativeErrorCodes.InvalidArgument, (await files.OpenUriAsync(new Uri("ms-settings:privacy"), Ct)).Error!.Code);
        Assert.True((await files.RevealInFileManagerAsync("/tmp/a.txt", Ct)).IsSuccess);
        Assert.Equal(NativeErrorCodes.InvalidArgument, (await files.OpenPathAsync(" ", Ct)).Error!.Code);
        files.Results.EnqueueFailure(NativeError.Failed(NativeCapabilities.DesktopFiles, "desktop.trash-unavailable", "No trash."));
        Assert.Equal("desktop.trash-unavailable", (await files.MoveToTrashAsync("/tmp/a.txt", Ct)).Error!.Code);

        Assert.Equal(2, files.OpenedUris.Count);
        Assert.Equal(["/tmp/a.txt"], files.RevealedPaths);
        Assert.Empty(files.TrashedPaths);
    }
}

public sealed class DesktopIpcFakeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Services_on_a_shared_network_talk_to_each_other()
    {
        var network = new FakeIpcNetwork();
        await using var primary = new FakeIpcService(network);
        await using var secondary = new FakeIpcService(network);
        var secret = new byte[32];
        await using var server = (await primary.StartServerAsync("docs", (m, _, _) => ValueTask.FromResult<IpcMessage?>(IpcMessage.Create("ok", m.Payload)), new IpcSecurityOptions { SharedSecret = secret }, Ct)).Value!;

        Assert.Equal("ipc.authentication-failed", (await secondary.ConnectAsync("docs", cancellationToken: Ct)).Error!.Code);
        await using var client = (await secondary.ConnectAsync("docs", new IpcSecurityOptions { SharedSecret = secret }, cancellationToken: Ct)).Value!;
        Assert.Equal("x", (await client.RequestAsync(IpcMessage.Create("open", "x"), cancellationToken: Ct)).Value!.Payload);
        Assert.Equal(["docs"], network.Channels);
        Assert.Single(secondary.Connections);
        Assert.Single(primary.Servers);
        Assert.Equal(NativeErrorCodes.InvalidArgument, (await secondary.ConnectAsync("docs", new IpcSecurityOptions { SharedSecret = new byte[4] }, cancellationToken: Ct)).Error!.Code);
    }

    [Fact]
    public async Task Requests_time_out_and_pending_requests_fail_when_the_server_stops()
    {
        await using var ipc = new FakeIpcService();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = (await ipc.StartServerAsync("slow", async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }, cancellationToken: Ct)).Value!;
        var client = (await ipc.ConnectAsync("slow", cancellationToken: Ct)).Value!;

        var timedOut = await client.RequestAsync(IpcMessage.Create("wait"), TimeSpan.FromMilliseconds(50), Ct);
        Assert.Equal(NativeErrorCodes.Timeout, timedOut.Error!.Code);

        var pending = client.RequestAsync(IpcMessage.Create("wait"), TimeSpan.FromSeconds(30), Ct).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await server.DisposeAsync();

        Assert.Equal("ipc.disconnected", (await pending.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Error!.Code);
        Assert.False(client.IsConnected);
        Assert.Empty(ipc.Connections);
    }

    [Fact]
    public async Task Single_instance_primary_publishes_its_launch_and_receives_forwarded_deep_links()
    {
        await using var provider = new ServiceCollection()
            .AddBlazorNativeSdk(sdk => sdk.AddFakes().ConfigureDeepLinks(o => o.Schemes.Add("bnstest")))
            .BuildServiceProvider();
        var activation = provider.GetRequiredService<ActivationService>();
        var routes = new List<string>();
        using var map = activation.MapRoute("/orders/{id}", (m, _) =>
        {
            routes.Add(m.Values["id"] + ":" + m.Activation.Kind + ":" + m.Activation.IsColdStart);
            return ValueTask.CompletedTask;
        });
        activation.MarkReady();
        var singleInstance = provider.GetRequiredService<FakeSingleInstanceService>();

        await using var lease = (await provider.GetRequiredService<ISingleInstanceService>().AcquireAsync(["bnstest://orders/1"], Ct)).Value!;
        var second = (await singleInstance.AcquireAsync(["bnstest://orders/2", "--ignored"], Ct)).Value!;
        Assert.True(await singleInstance.SimulateSecondaryInstanceAsync(["--verbose"], Ct));
        await activation.WhenIdleAsync(Ct);

        Assert.True(lease.IsPrimary);
        Assert.Same(lease, singleInstance.PrimaryLease);
        Assert.Equal((false, true), (second.IsPrimary, second.ArgumentsForwarded));
        Assert.Equal(["1:DeepLink:True", "2:DeepLink:False"], routes);
        Assert.Equal([["bnstest://orders/2", "--ignored"], ["--verbose"]], singleInstance.ReceivedArguments);

        await lease.DisposeAsync();
        Assert.Null(singleInstance.PrimaryLease);
        Assert.False(await singleInstance.SimulateSecondaryInstanceAsync(["x"], Ct));
    }

    [Fact]
    public async Task Single_instance_can_be_a_secondary_instance()
    {
        var singleInstance = new FakeSingleInstanceService { AnotherInstanceIsRunning = true, ForwardingSucceeds = false };

        var lease = (await singleInstance.AcquireAsync(["--open", "a.txt"], Ct)).Value!;

        Assert.Equal((false, false), (lease.IsPrimary, lease.ArgumentsForwarded));
        Assert.Equal([["--open", "a.txt"]], singleInstance.ForwardedArguments);
        Assert.Null(singleInstance.PrimaryLease);
        Assert.Empty(singleInstance.ReceivedArguments);
    }
}
