using System.Text.Json;
using BlazorNativeSdk.Browser.Hosting;
using BlazorNativeSdk.Browser.Interop;
using BlazorNativeSdk.Browser.Services;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using TestContext = Xunit.TestContext;

namespace BlazorNativeSdk.Browser.Tests;

/// <summary>A bUnit context with the browser adapter registered (Blazor Server mode) and the module/session mocked.</summary>
public sealed class BrowserFixture : IAsyncDisposable
{
    private readonly BunitContext _context = new();
    private readonly AsyncServiceScope _scope;

    public BrowserFixture(Action<BrowserNativeOptions>? configure = null, Action<DeepLinkOptions>? deepLinks = null)
    {
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
        var module = _context.JSInterop.SetupModule(BrowserNativeOptions.DefaultModulePath);
        Session = module.SetupModule("createSession", _ => true);
        _context.Services.AddBlazorNativeSdk(sdk =>
        {
            sdk.AddBrowser(configure);
            if (deepLinks is not null)
            {
                sdk.ConfigureDeepLinks(deepLinks);
            }
        });
        _scope = _context.Services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
    }

    /// <summary>Handlers for calls on the JavaScript session object.</summary>
    public BunitJSInterop Session { get; }

    public T Get<T>()
        where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public void Returns(string identifier, string json) => Session.Setup<JsonElement>(identifier, _ => true).SetResult(Json(json));

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _context.DisposeAsync();
    }
}

internal sealed class FakeStreamReference(byte[] content) : IJSStreamReference
{
    public long Length => content.Length;

    public ValueTask<Stream> OpenReadStreamAsync(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<Stream>(new MemoryStream(content));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class ClipboardTests
{
    [Fact]
    public async Task Writing_text_succeeds()
    {
        await using var fixture = new BrowserFixture();
        fixture.Returns("clipboardWrite", """{"ok":true,"value":null}""");

        var result = await fixture.Get<IClipboardService>().SetTextAsync("hello", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var invocation = Assert.Single(fixture.Session.Invocations["clipboardWrite"]);
        Assert.Equal("hello", invocation.Arguments[0]);
    }

    [Fact]
    public async Task Reading_text_returns_the_text_and_null_when_empty()
    {
        await using var fixture = new BrowserFixture();
        fixture.Returns("clipboardRead", """{"ok":true,"value":"copied"}""");
        var clipboard = fixture.Get<IClipboardService>();

        var text = await clipboard.GetTextAsync(TestContext.Current.CancellationToken);
        Assert.Equal("copied", text.Value);

        fixture.Returns("clipboardRead", """{"ok":true,"value":""}""");
        var empty = await clipboard.GetTextAsync(TestContext.Current.CancellationToken);
        Assert.True(empty.IsSuccess);
        Assert.Null(empty.Value);
    }

    [Fact]
    public async Task Denied_read_maps_to_permission_denied()
    {
        await using var fixture = new BrowserFixture();
        fixture.Returns("clipboardRead", """{"ok":false,"name":"NotAllowedError","message":"Read permission denied.","permissionState":"denied"}""");

        var result = await fixture.Get<IClipboardService>().GetTextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(NativeResultStatus.PermissionDenied, result.Status);
        Assert.Equal(NativePermissions.ClipboardRead, result.Error!.Permission);
        Assert.Equal(PermissionStatus.Denied, result.Error.PermissionStatus);
    }

    [Fact]
    public async Task Write_without_gesture_maps_to_user_gesture_required()
    {
        await using var fixture = new BrowserFixture();
        fixture.Returns("clipboardWrite", """{"ok":false,"name":"NotAllowedError","message":"Document is not focused.","userActivation":false}""");

        var result = await fixture.Get<IClipboardService>().SetTextAsync("x", TestContext.Current.CancellationToken);

        Assert.Equal(NativeResultStatus.Unavailable, result.Status);
        Assert.Equal(NativeErrorCodes.UserGestureRequired, result.Error!.Code);
    }

    [Fact]
    public async Task Status_lists_features()
    {
        await using var fixture = new BrowserFixture();
        fixture.Returns("probe", """{"ok":true,"value":{"secureContext":true,"clipboardRead":false,"clipboardWrite":true,"execCommandCopy":true}}""");

        var status = await fixture.Get<IClipboardService>().GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.True(status.IsAvailable);
        Assert.True(status.Supports(ClipboardFeatures.WriteText));
        Assert.False(status.Supports(ClipboardFeatures.ReadText));
        Assert.False(status.Supports(ClipboardFeatures.ChangeNotification));
        Assert.Equal("browser", status.Adapter);
    }
}

public sealed class ShareTests
{
    [Fact]
    public async Task AbortError_maps_to_cancelled()
    {
        await using var fixture = new BrowserFixture();
        fixture.Returns("share", """{"ok":false,"name":"AbortError","message":"Share canceled"}""");

        var result = await fixture.Get<IShareService>().ShareAsync(ShareRequest.ForText("hi"), TestContext.Current.CancellationToken);

        Assert.Equal(NativeResultStatus.Cancelled, result.Status);
        Assert.Equal(NativeErrorCodes.Cancelled, result.Error!.Code);
    }

    [Fact]
    public async Task NotAllowedError_without_activation_requires_a_gesture()
    {
        await using var fixture = new BrowserFixture();
        fixture.Returns("share", """{"ok":false,"name":"NotAllowedError","message":"requires transient activation","userActivation":false}""");

        var result = await fixture.Get<IShareService>().ShareAsync(ShareRequest.ForUri(new Uri("https://example.com")), TestContext.Current.CancellationToken);

        Assert.Equal(NativeResultStatus.Unavailable, result.Status);
        Assert.Equal(NativeErrorCodes.UserGestureRequired, result.Error!.Code);
    }

    [Fact]
    public async Task Sends_request_and_streams_files()
    {
        await using var fixture = new BrowserFixture();
        fixture.Returns("share", """{"ok":true,"value":null}""");
        await using var file = NativeFile.FromBytes("a.txt", "abc"u8.ToArray());

        var result = await fixture.Get<IShareService>().ShareAsync(ShareRequest.ForFiles("t", file), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var invocation = Assert.Single(fixture.Session.Invocations["share"]);
        Assert.Contains("\"kind\":\"stream\"", (string)invocation.Arguments[1]!);
        Assert.IsType<DotNetStreamReference>(invocation.Arguments[2]);
    }

    [Fact]
    public async Task Empty_request_is_rejected_without_calling_the_browser()
    {
        await using var fixture = new BrowserFixture();

        var result = await fixture.Get<IShareService>().ShareAsync(new ShareRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(NativeErrorCodes.InvalidArgument, result.Error!.Code);
        Assert.Empty(fixture.Session.Invocations["share"]);
    }

    [Fact]
    public async Task Status_reports_files_only_when_the_browser_can_share_them()
    {
        await using var fixture = new BrowserFixture();
        fixture.Returns("probe", """{"ok":true,"value":{"secureContext":true,"share":true,"canShareFiles":false}}""");

        var status = await fixture.Get<IShareService>().GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.True(status.Supports(ShareFeatures.Text));
        Assert.False(status.Supports(ShareFeatures.Files));
    }
}

public sealed class SecureStorageTests
{
    [Fact]
    public async Task Disabled_by_default_with_an_explicit_reason()
    {
        await using var fixture = new BrowserFixture();
        var storage = fixture.Get<ISecureStorage>();

        var status = await storage.GetStatusAsync(TestContext.Current.CancellationToken);
        var get = await storage.GetAsync("token", TestContext.Current.CancellationToken);

        Assert.Equal(CapabilityAvailability.Unsupported, status.Availability);
        Assert.Equal(BrowserSecureStorage.DisabledReason, status.Reason);
        Assert.Contains("EnableBestEffortSecureStorage", status.Reason, StringComparison.Ordinal);
        Assert.Equal(NativeResultStatus.Unsupported, get.Status);
        Assert.Equal(SecureStorageProtection.None, storage.Protection);
        Assert.Empty(fixture.Session.Invocations);
    }

    [Fact]
    public async Task Enabled_storage_is_best_effort_and_round_trips()
    {
        await using var fixture = new BrowserFixture(o =>
        {
            o.EnableBestEffortSecureStorage = true;
            o.SecureStorageDatabaseName = "test-db";
        });
        fixture.Returns("secureSet", """{"ok":true,"value":null}""");
        fixture.Returns("secureGet", """{"ok":true,"value":"secret"}""");
        fixture.Returns("secureRemove", """{"ok":true,"value":true}""");
        var storage = fixture.Get<ISecureStorage>();

        Assert.Equal(SecureStorageProtection.BestEffort, storage.Protection);
        Assert.True((await storage.SetAsync("token", "secret", TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal("secret", (await storage.GetAsync("token", TestContext.Current.CancellationToken)).Value);
        Assert.True((await storage.RemoveAsync("token", TestContext.Current.CancellationToken)).Value);
        Assert.Equal("test-db", fixture.Session.Invocations["secureSet"][0].Arguments[0]);
    }

    [Fact]
    public async Task Insecure_context_is_unavailable()
    {
        await using var fixture = new BrowserFixture(o => o.EnableBestEffortSecureStorage = true);
        fixture.Returns("probe", """{"ok":true,"value":{"secureContext":false,"indexedDb":true,"subtleCrypto":false}}""");
        fixture.Returns("secureGet", """{"ok":false,"name":"InsecureContextError","message":"no"}""");
        var storage = fixture.Get<ISecureStorage>();

        var status = await storage.GetStatusAsync(TestContext.Current.CancellationToken);
        var get = await storage.GetAsync("k", TestContext.Current.CancellationToken);

        Assert.Equal(CapabilityAvailability.Unavailable, status.Availability);
        Assert.Equal(NativeErrorCodes.InsecureContext, status.ReasonCode);
        Assert.Equal(NativeErrorCodes.InsecureContext, get.Error!.Code);
    }
}

public sealed class UnsupportedCapabilityTests
{
    [Fact]
    public async Task Biometrics_report_an_explicit_reason()
    {
        await using var fixture = new BrowserFixture();
        var biometrics = fixture.Get<IBiometricService>();

        var status = await biometrics.GetStatusAsync(TestContext.Current.CancellationToken);
        var auth = await biometrics.AuthenticateAsync(new BiometricRequest { Reason = "unlock" }, TestContext.Current.CancellationToken);

        Assert.Equal(CapabilityAvailability.Unsupported, status.Availability);
        Assert.Equal(NativeErrorCodes.NotSupported, status.ReasonCode);
        Assert.Contains("WebAuthn", status.Reason, StringComparison.Ordinal);
        Assert.Equal(NativeResultStatus.Unsupported, auth.Status);
        Assert.NotEqual(NativeErrorCodes.NoAdapter, auth.Error!.Code);
    }

    [Fact]
    public async Task Folder_picking_scheduling_and_settings_are_unsupported()
    {
        await using var fixture = new BrowserFixture();

        var folder = await fixture.Get<IFilePickerService>().PickFolderAsync(cancellationToken: TestContext.Current.CancellationToken);
        var schedule = await fixture.Get<INotificationService>().ScheduleAsync(
            new LocalNotification { Id = "1", Title = "t" }, new NotificationSchedule { DeliverAt = DateTimeOffset.UtcNow }, TestContext.Current.CancellationToken);
        var channel = await fixture.Get<INotificationService>().CreateChannelAsync(new NotificationChannel { Id = "c", Name = "n" }, TestContext.Current.CancellationToken);
        var settings = await fixture.Get<IPermissionService>().OpenAppSettingsAsync(TestContext.Current.CancellationToken);
        var appStorage = fixture.Get<IAppStorage>().GetDirectory(AppDirectory.AppData);

        Assert.Equal(NativeResultStatus.Unsupported, folder.Status);
        Assert.Equal(NativeResultStatus.Unsupported, schedule.Status);
        Assert.True(channel.IsSuccess);
        Assert.Equal(NativeResultStatus.Unsupported, settings.Status);
        Assert.Equal(NativeResultStatus.Unsupported, appStorage.Status);
    }

    [Fact]
    public async Task Barcode_is_unsupported_without_BarcodeDetector()
    {
        await using var fixture = new BrowserFixture();
        fixture.Returns("probe", """{"ok":true,"value":{"secureContext":true,"userMedia":true,"barcodeDetector":false}}""");

        var status = await fixture.Get<IBarcodeScanner>().GetStatusAsync(TestContext.Current.CancellationToken);
        var scan = await fixture.Get<IBarcodeScanner>().ScanAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(CapabilityAvailability.Unsupported, status.Availability);
        Assert.Equal(BrowserBarcodeScanner.UnsupportedReason, status.Reason);
        Assert.Equal(NativeResultStatus.Unsupported, scan.Status);
        Assert.Empty(fixture.Session.Invocations["scanBarcode"]);
    }
}

public sealed class DeviceServiceTests
{
    [Fact]
    public async Task Camera_status_requires_permission_and_lists_features()
    {
        await using var fixture = new BrowserFixture();
        fixture.Returns("probe", """{"ok":true,"value":{"secureContext":true,"userMedia":true,"mediaRecorder":true,"cameraPermission":"prompt","videoInputs":1}}""");

        var status = await fixture.Get<ICameraService>().GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CapabilityAvailability.RequiresPermission, status.Availability);
        Assert.True(status.Supports(CameraFeatures.Photo));
        Assert.True(status.Supports(CameraFeatures.Video));
        Assert.True(status.Supports(CameraFeatures.CameraSelection));
    }

    [Fact]
    public async Task Camera_without_media_recorder_has_no_video_feature()
    {
        await using var fixture = new BrowserFixture();
        fixture.Returns("probe", """{"ok":true,"value":{"secureContext":true,"userMedia":true,"mediaRecorder":false,"cameraPermission":"granted","videoInputs":2}}""");

        var status = await fixture.Get<ICameraService>().GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.True(status.IsAvailable);
        Assert.False(status.Supports(CameraFeatures.Video));
    }

    [Fact]
    public async Task Camera_denied_maps_to_permission_denied()
    {
        await using var fixture = new BrowserFixture();
        fixture.Returns("capture", """{"ok":false,"name":"NotAllowedError","message":"Permission denied","permissionState":"denied","userActivation":null}""");

        var result = await fixture.Get<ICameraService>().CapturePhotoAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(NativeResultStatus.PermissionDenied, result.Status);
        Assert.Equal(NativePermissions.Camera, result.Error!.Permission);
    }

    [Fact]
    public async Task Picked_files_are_browser_files_that_stream_their_content()
    {
        await using var fixture = new BrowserFixture();
        fixture.Returns("pickFiles", """{"ok":true,"value":[{"handle":"op:1","name":"a.txt","type":"text/plain","size":3,"lastModified":1700000000000}]}""");
        fixture.Session.SetupModule("takeFile", _ => true);
        fixture.Session.Setup<IJSStreamReference>("blobStream", _ => true).SetResult(new FakeStreamReference("abc"u8.ToArray()));

        var result = await fixture.Get<IFilePickerService>().PickFilesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var file = Assert.IsType<BrowserNativeFile>(Assert.Single(result.Value!));
        Assert.Equal("a.txt", file.Name);
        Assert.Equal("text/plain", file.ContentType);
        Assert.Equal(3, file.Size);
        Assert.Equal("abc"u8.ToArray(), await file.ReadAllBytesAsync(cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<IOException>(async () => await file.OpenReadAsync(2, TestContext.Current.CancellationToken));
        await file.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => file.JSReference);
    }

    [Fact]
    public async Task Dismissed_picker_is_cancelled()
    {
        await using var fixture = new BrowserFixture();
        fixture.Returns("pickFiles", """{"ok":true,"value":[]}""");

        var result = await fixture.Get<IFilePickerService>().PickFilesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(NativeResultStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task Caller_cancellation_throws_and_aborts_the_browser_operation()
    {
        await using var fixture = new BrowserFixture();
        fixture.Session.Setup<JsonElement>("capture", _ => true); // Never completes: the overlay stays open.
        var abort = fixture.Session.SetupVoid("abort", _ => true);
        using var cts = new CancellationTokenSource();

        var capture = fixture.Get<ICameraService>().CapturePhotoAsync(cancellationToken: cts.Token).AsTask();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        var operationId = (string)fixture.Session.Invocations["capture"][0].Arguments[0]!;
        Assert.Contains(abort.Invocations, i => (string)i.Arguments[0]! == operationId);
    }

    [Fact]
    public async Task Barcode_scan_returns_the_detected_code()
    {
        await using var fixture = new BrowserFixture();
        fixture.Returns("probe", """{"ok":true,"value":{"secureContext":true,"userMedia":true,"barcodeDetector":true,"barcodeFormats":["qr_code","ean_13"]}}""");
        fixture.Returns("scanBarcode", """{"ok":true,"value":{"rawValue":"https://example.com","format":"qr_code","x":10,"y":20,"width":30,"height":40}}""");

        var result = await fixture.Get<IBarcodeScanner>().ScanAsync(new BarcodeScanOptions { Formats = BarcodeFormats.QrCode | BarcodeFormats.Aztec }, TestContext.Current.CancellationToken);

        Assert.Equal(BarcodeFormats.QrCode, result.Value!.Format);
        Assert.Equal(new BarcodeBounds(10, 20, 30, 40), result.Value.Bounds);
        var request = (string)fixture.Session.Invocations["scanBarcode"][0].Arguments[1]!;
        Assert.Contains("\"formats\":[\"qr_code\"]", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Barcode_timeout_maps_to_timeout()
    {
        await using var fixture = new BrowserFixture();
        fixture.Returns("probe", """{"ok":true,"value":{"secureContext":true,"userMedia":true,"barcodeDetector":true,"barcodeFormats":["qr_code"]}}""");
        fixture.Returns("scanBarcode", """{"ok":false,"name":"TimeoutError","message":"No barcode"}""");

        var result = await fixture.Get<IBarcodeScanner>().ScanAsync(new BarcodeScanOptions { Timeout = TimeSpan.FromSeconds(5) }, TestContext.Current.CancellationToken);

        Assert.Equal(NativeResultStatus.Failed, result.Status);
        Assert.Equal(NativeErrorCodes.Timeout, result.Error!.Code);
    }

    [Fact]
    public async Task Permissions_are_checked_and_unknown_names_are_unsupported()
    {
        await using var fixture = new BrowserFixture();
        fixture.Returns("permissionQuery", """{"ok":true,"value":"prompt"}""");
        var permissions = fixture.Get<IPermissionService>();

        Assert.Equal(PermissionStatus.NotDetermined, (await permissions.CheckAsync(NativePermissions.Camera, TestContext.Current.CancellationToken)).Value);
        Assert.Equal(NativeResultStatus.Unsupported, (await permissions.CheckAsync(NativePermissions.ExactAlarms, TestContext.Current.CancellationToken)).Status);
        Assert.False((await permissions.ShouldShowRationaleAsync(NativePermissions.Camera, TestContext.Current.CancellationToken)).Value);
    }

    [Fact]
    public async Task Notifications_without_permission_are_permission_denied()
    {
        await using var fixture = new BrowserFixture();
        fixture.Returns("showNotification", """{"ok":false,"name":"NotAllowedError","message":"no","permissionState":"prompt"}""");

        var result = await fixture.Get<INotificationService>().ShowAsync(new LocalNotification { Id = "1", Title = "Hi" }, TestContext.Current.CancellationToken);

        Assert.Equal(NativeResultStatus.PermissionDenied, result.Status);
        Assert.Equal(PermissionStatus.NotDetermined, result.Error!.PermissionStatus);
        Assert.Equal(NativePermissions.Notifications, result.Error.Permission);
    }
}

public sealed class LifecycleAndActivationTests
{
    [Fact]
    public async Task Lifecycle_reports_initial_and_subsequent_states()
    {
        await using var fixture = new BrowserFixture();
        fixture.Returns("startLifecycle", """{"ok":true,"value":"active"}""");
        var lifecycle = fixture.Get<IAppLifecycle>();
        var transitions = new List<AppLifecycleState>();
        lifecycle.StateChanged += (_, e) => transitions.Add(e.Current);

        var status = await lifecycle.GetStatusAsync(TestContext.Current.CancellationToken);
        fixture.Get<BrowserInterop>().Callbacks.OnLifecycle("background", "visibilitychange");

        Assert.True(status.IsAvailable);
        Assert.Equal(AppLifecycleState.Background, lifecycle.State);
        Assert.Equal([AppLifecycleState.Active, AppLifecycleState.Background], transitions);
    }

    [Fact]
    public async Task Connectivity_reports_changes()
    {
        await using var fixture = new BrowserFixture();
        fixture.Returns("startConnectivity", """{"ok":true,"value":{"onLine":true,"type":"wifi","saveData":false}}""");
        var connectivity = fixture.Get<IConnectivityService>();
        ConnectivityChangedEventArgs? change = null;

        await connectivity.GetStatusAsync(TestContext.Current.CancellationToken);
        connectivity.Changed += (_, e) => change = e;
        fixture.Get<BrowserInterop>().Callbacks.OnConnectivity("""{"onLine":false}""");

        Assert.NotNull(change);
        Assert.True(change.Previous.IsOnline);
        Assert.Equal([ConnectionType.WiFi], change.Previous.ConnectionTypes);
        Assert.Equal(NetworkAccess.None, connectivity.Current.Access);
    }

    [Fact]
    public async Task Protocol_handler_link_is_published_as_the_launch_activation()
    {
        await using var fixture = new BrowserFixture(deepLinks: o => o.Schemes.Add("web+myapp"));
        fixture.Returns("launchInfo", """{"ok":true,"value":{"link":"web+myapp://orders/42","notificationClick":null}}""");
        fixture.Returns("startLifecycle", """{"ok":true,"value":"active"}""");
        fixture.Returns("startConnectivity", """{"ok":true,"value":{"onLine":true}}""");

        Assert.True(await fixture.Get<BrowserStartup>().StartAsync(TestContext.Current.CancellationToken));

        var activation = fixture.Get<IAppActivationService>();
        Assert.NotNull(activation.LaunchActivation);
        Assert.Equal(ActivationKind.DeepLink, activation.LaunchActivation.Kind);
        Assert.True(activation.LaunchActivation.IsColdStart);
        Assert.True(activation.TryResolveRoute(activation.LaunchActivation, out var route));
        Assert.Equal("/orders/42", route);
    }

    [Fact]
    public async Task Notification_taps_are_published_with_their_route()
    {
        await using var fixture = new BrowserFixture();
        fixture.Get<INotificationService>(); // Creates the activation bridge.
        var activations = (ActivationService)fixture.Get<IAppActivationService>();
        var received = new TaskCompletionSource<AppActivation>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = activations.Subscribe((a, _) =>
        {
            received.TrySetResult(a);
            return ValueTask.FromResult(true);
        });
        activations.MarkReady();

        fixture.Get<BrowserInterop>().Callbacks.OnNotificationClick("""{"id":"n1","action":"reply","route":"/inbox/7","data":{"k":"v"}}""");
        var activation = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(ActivationKind.Notification, activation.Kind);
        Assert.Equal("n1", activation.NotificationId);
        Assert.Equal("reply", activation.ActionId);
        Assert.Equal("/inbox/7", activation.Route);
        Assert.Equal("v", activation.Data["k"]);
        Assert.False(activation.IsColdStart);
    }
}
