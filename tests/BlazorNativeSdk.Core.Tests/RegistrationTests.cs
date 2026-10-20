using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;
using BlazorNativeSdk.Testing;
using BlazorNativeSdk.Unsupported;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorNativeSdk.Tests;

public sealed class RegistrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider Build(Action<INativeSdkBuilder>? configure = null) =>
        new ServiceCollection().AddBlazorNativeSdk(configure).BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

    [Fact]
    public async Task Every_capability_resolves_and_reports_unsupported_without_adapters()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<ICapabilityRegistry>();

        Assert.Equal(NativeCapabilities.All.Count, registry.Capabilities.Count);

        var statuses = await registry.GetAllStatusesAsync(Ct);
        var core = new[] { NativeCapabilities.DeepLinks, NativeCapabilities.WorkflowCheckpoints, NativeCapabilities.Lifecycle };
        foreach (var status in statuses.Where(s => !core.Contains(s.Capability)))
        {
            Assert.Equal(CapabilityAvailability.Unsupported, status.Availability);
            Assert.Equal(NativeErrorCodes.NoAdapter, status.ReasonCode);
            Assert.Contains("Register an adapter", status.Reason, StringComparison.Ordinal);
        }

        var camera = provider.GetRequiredService<ICameraService>();
        var result = await camera.CapturePhotoAsync(cancellationToken: Ct);
        Assert.Equal(NativeResultStatus.Unsupported, result.Status);
        Assert.Equal(NativeErrorCodes.NoAdapter, result.Error!.Code);
    }

    [Fact]
    public void Desktop_and_lifecycle_interfaces_always_resolve()
    {
        using var provider = Build();

        Assert.IsType<UnsupportedWindowManager>(provider.GetRequiredService<IWindowManager>());
        Assert.IsType<UnsupportedTrayService>(provider.GetRequiredService<ITrayService>());
        Assert.IsType<ManualAppLifecycle>(provider.GetRequiredService<IAppLifecycle>());
        Assert.IsType<ActivationService>(provider.GetRequiredService<IAppActivationService>());
        Assert.Same(provider.GetRequiredService<IAppActivationService>(), provider.GetRequiredService<IActivationPublisher>());
        Assert.NotNull(provider.GetRequiredService<IWorkflowCheckpointStore>());
        Assert.NotNull(provider.GetRequiredService<IWindowPlacementStore>());
    }

    [Fact]
    public async Task Adapters_replace_fallbacks_and_registry_reports_them()
    {
        await using var provider = Build(sdk => sdk.AddFakes());
        await using var scope = provider.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<ICapabilityRegistry>();

        Assert.IsType<FakeCameraService>(provider.GetRequiredService<ICameraService>());
        Assert.Same(provider.GetRequiredService<ICameraService>(), provider.GetRequiredService<FakeCameraService>());

        var status = await registry.GetStatusAsync(NativeCapabilities.Camera, Ct);
        Assert.True(status.IsAvailable);
        Assert.Equal("fake", status.Adapter);
        Assert.Equal(NativePlatform.Android, registry.Host.Platform);
    }

    [Fact]
    public void Later_registrations_override_adapters()
    {
        using var provider = Build(sdk =>
        {
            sdk.AddFakes();
            sdk.AddCapability<IBarcodeScanner, CustomScanner>(NativeCapabilities.Barcode);
        });

        Assert.IsType<CustomScanner>(provider.GetRequiredService<IBarcodeScanner>());
    }

    [Fact]
    public void One_implementation_can_serve_several_capabilities_as_a_single_instance()
    {
        using var provider = Build(sdk =>
        {
            sdk.AddCapability<IClipboardService, MultiService>(NativeCapabilities.Clipboard);
            sdk.AddCapability<IShareService, MultiService>(NativeCapabilities.Share);
        });

        Assert.Same(provider.GetRequiredService<IClipboardService>(), provider.GetRequiredService<IShareService>());
    }

    [Fact]
    public async Task Unknown_capabilities_report_unsupported()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<ICapabilityRegistry>();

        var status = await registry.GetStatusAsync(new CapabilityId("vendor.laser"), Ct);

        Assert.True(status.IsUnsupported);
        Assert.Null(registry.GetProvider(new CapabilityId("vendor.laser")));
    }

    [Fact]
    public async Task Ensure_permission_requests_only_when_undetermined()
    {
        var permissions = new FakePermissionService();
        permissions.Set(NativePermissions.Microphone, PermissionStatus.Restricted);

        var granted = await permissions.EnsureAsync(NativePermissions.Camera, Ct);
        Assert.True(granted.IsSuccess);
        Assert.Equal(PermissionStatus.Granted, granted.Value);

        var restricted = await permissions.EnsureAsync(NativePermissions.Microphone, Ct);
        Assert.Equal(NativeResultStatus.PermissionDenied, restricted.Status);
        Assert.Equal(PermissionStatus.Restricted, restricted.Error!.PermissionStatus);
        Assert.Equal(2, permissions.Calls.Count(c => c == nameof(IPermissionService.CheckAsync)));
        Assert.Equal(1, permissions.Calls.Count(c => c == nameof(IPermissionService.RequestAsync)));

        permissions.RequestOutcome = PermissionStatus.Denied;
        var denied = await permissions.EnsureAsync(NativePermissions.Notifications, Ct);
        Assert.Equal(NativeResultStatus.PermissionDenied, denied.Status);
    }

    [Fact]
    public async Task Fake_notification_taps_flow_through_the_activation_pipeline()
    {
        await using var provider = Build(sdk => sdk.AddFakes());
        var notifications = provider.GetRequiredService<FakeNotificationService>();
        var activation = provider.GetRequiredService<ActivationService>();
        var routes = new List<string>();
        using var map = activation.MapRoute("/chat/{id}", (m, _) =>
        {
            routes.Add(m.Values["id"] + ":" + m.Activation.ActionId + ":" + m.Activation.UserText);
            return ValueTask.CompletedTask;
        });
        activation.MarkReady();

        await notifications.ShowAsync(new LocalNotification { Id = "n1", Title = "Hi", Route = "/chat/9" }, Ct);
        await notifications.SimulateTapAsync("n1", actionId: "reply", userText: "hello");
        await activation.WhenIdleAsync(Ct);

        Assert.Equal(["9:reply:hello"], routes);
    }

    [Fact]
    public async Task In_memory_secure_storage_satisfies_the_contract()
    {
        await CapabilityContracts.VerifySecureStorageAsync(new InMemorySecureStorage(), Ct);
        await CapabilityContracts.VerifyClipboardAsync(new FakeClipboardService(), Ct);
        await CapabilityContracts.VerifyStatusAsync(new FakeCameraService(), NativeCapabilities.Camera, Ct);
    }

    private sealed class CustomScanner : FakeCapability, IBarcodeScanner
    {
        public CustomScanner()
            : base(NativeCapabilities.Barcode)
        {
        }

        public ValueTask<NativeResult<Barcode>> ScanAsync(BarcodeScanOptions? options = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<NativeResult<Barcode>>(new Barcode { Format = BarcodeFormats.QrCode, RawValue = "custom" });

        public ValueTask<NativeResult<IReadOnlyList<Barcode>>> DetectAsync(NativeFile image, BarcodeFormats formats = BarcodeFormats.All, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(NativeResult.Success<IReadOnlyList<Barcode>>([]));
    }

    private sealed class MultiService : FakeCapability, IClipboardService, IShareService
    {
        public MultiService()
            : base(NativeCapabilities.Clipboard)
        {
        }

        public event EventHandler? ContentChanged;

        public ValueTask<NativeResult<string?>> GetTextAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(NativeResult.Success<string?>(null));

        public ValueTask<NativeResult> SetTextAsync(string? text, CancellationToken cancellationToken = default)
        {
            ContentChanged?.Invoke(this, EventArgs.Empty);
            return ValueTask.FromResult(NativeResult.Ok);
        }

        public ValueTask<NativeResult> ShareAsync(ShareRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(NativeResult.Ok);
    }
}
