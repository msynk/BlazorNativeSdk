using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;
using BlazorNativeSdk.Storage;
using BlazorNativeSdk.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk;

/// <summary>Registers fakes for component and service tests (for example with bUnit).</summary>
public static class FakeNativeSdkBuilderExtensions
{
    /// <summary>
    /// Registers a fake for every capability: device capabilities, app storage, lifecycle, connectivity, background work,
    /// transfers, workflow checkpoints and the desktop shell (windows, screens, tray, global shortcuts, menus, shell file
    /// operations, IPC, single instance and protocol registration), plus an <see cref="InMemoryStateStore"/> as
    /// <see cref="INativeStateStore"/>. Resolve the concrete fake types (for example <see cref="FakeCameraService"/> or
    /// <see cref="FakeWindowManager"/>) to script results, raise events and assert calls.
    /// </summary>
    /// <remarks>
    /// Every fake reports <see cref="CapabilityAvailability.Available"/>, whatever <paramref name="host"/> says; call
    /// <see cref="FakeCapability.SimulateUnsupported"/> to simulate a host without a capability (for example the desktop
    /// capabilities on a phone). Deep links (<see cref="IAppActivationService"/>) are served by Core's in-process activation
    /// pipeline, which is deterministic: publish activations through <see cref="IActivationPublisher"/>, or use
    /// <see cref="FakeNotificationService.SimulateTapAsync"/> and <see cref="FakeSingleInstanceService.SimulateSecondaryInstanceAsync"/>.
    /// </remarks>
    /// <param name="builder">The SDK builder.</param>
    /// <param name="host">The host to simulate; defaults to a MAUI app on Android.</param>
    public static INativeSdkBuilder AddFakes(this INativeSdkBuilder builder, NativeHostInfo? host = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        builder.SetHost(host ?? new NativeHostInfo { Platform = NativePlatform.Android, Host = NativeHostKind.Maui, FormFactor = DeviceFormFactor.Phone, Description = "Fake host" });

        // Devices.
        builder.AddCapability<ICameraService, FakeCameraService>(NativeCapabilities.Camera);
        builder.AddCapability<IBarcodeScanner, FakeBarcodeScanner>(NativeCapabilities.Barcode);
        builder.AddCapability<IFilePickerService, FakeFilePickerService>(NativeCapabilities.Files);
        builder.AddCapability<IAppStorage, FakeAppStorage>(NativeCapabilities.AppStorage);
        builder.AddCapability<ISecureStorage, InMemorySecureStorage>(NativeCapabilities.SecureStorage);
        builder.AddCapability<IShareService, FakeShareService>(NativeCapabilities.Share);
        builder.AddCapability<IBiometricService, FakeBiometricService>(NativeCapabilities.Biometrics);
        builder.AddCapability<IClipboardService, FakeClipboardService>(NativeCapabilities.Clipboard);
        builder.AddCapability<IPermissionService, FakePermissionService>(NativeCapabilities.Permissions);
        builder.AddCapability<INotificationService>(NativeCapabilities.Notifications, static sp => sp.GetRequiredService<FakeNotificationService>());
        services.AddSingleton(static sp => new FakeNotificationService(sp.GetRequiredService<IActivationPublisher>()));

        // Lifecycle, background work and state.
        builder.AddCapability<IConnectivityService, FakeConnectivityService>(NativeCapabilities.Connectivity);
        builder.AddCapability<IAppLifecycle, FakeAppLifecycle>(NativeCapabilities.Lifecycle);
        builder.AddCapability<IBackgroundWorkScheduler>(NativeCapabilities.BackgroundWork, static sp => sp.GetRequiredService<FakeBackgroundWorkScheduler>());
        services.AddSingleton(static sp => new FakeBackgroundWorkScheduler(sp, sp.GetService<TimeProvider>()));
        builder.AddCapability<ITransferService, FakeTransferService>(NativeCapabilities.Transfers);
        builder.AddCapability<IWorkflowCheckpointStore, InMemoryWorkflowCheckpointStore>(NativeCapabilities.WorkflowCheckpoints);
        services.TryAddSingleton<InMemoryStateStore>();
        services.Replace(ServiceDescriptor.Singleton<INativeStateStore>(static sp => sp.GetRequiredService<InMemoryStateStore>()));

        // Desktop shell.
        builder.AddCapability<IWindowManager, FakeWindowManager>(NativeCapabilities.Windows);
        builder.AddCapability<IScreenService, FakeScreenService>(NativeCapabilities.Screens);
        builder.AddCapability<IGlobalShortcutService, FakeGlobalShortcutService>(NativeCapabilities.GlobalShortcuts);
        builder.AddCapability<ITrayService, FakeTrayService>(NativeCapabilities.Tray);
        builder.AddCapability<INativeMenuService, FakeNativeMenuService>(NativeCapabilities.Menus);
        builder.AddCapability<IDesktopFileOperations, FakeDesktopFileOperations>(NativeCapabilities.DesktopFiles);
        builder.AddCapability<IIpcService>(NativeCapabilities.Ipc, static sp => sp.GetRequiredService<FakeIpcService>());
        services.AddSingleton(static _ => new FakeIpcService());
        builder.AddCapability<ISingleInstanceService>(NativeCapabilities.SingleInstance, static sp => sp.GetRequiredService<FakeSingleInstanceService>());
        services.AddSingleton(static sp => new FakeSingleInstanceService(
            sp.GetRequiredService<IActivationPublisher>(),
            sp.GetService<IOptions<NativeSdkOptions>>()?.Value.DeepLinks));
        builder.AddCapability<IProtocolRegistrationService, FakeProtocolRegistrationService>(NativeCapabilities.ProtocolRegistration);
        return builder;
    }
}
