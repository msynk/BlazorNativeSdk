using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;
using BlazorNativeSdk.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorNativeSdk;

/// <summary>Registers fakes for component and service tests (for example with bUnit).</summary>
public static class FakeNativeSdkBuilderExtensions
{
    /// <summary>
    /// Registers a fake for every device capability, connectivity and lifecycle. Resolve the concrete fake types
    /// (for example <see cref="FakeCameraService"/>) to script results and assert calls.
    /// </summary>
    /// <param name="builder">The SDK builder.</param>
    /// <param name="host">The host to simulate; defaults to a MAUI app on Android.</param>
    public static INativeSdkBuilder AddFakes(this INativeSdkBuilder builder, NativeHostInfo? host = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.SetHost(host ?? new NativeHostInfo { Platform = NativePlatform.Android, Host = NativeHostKind.Maui, FormFactor = DeviceFormFactor.Phone, Description = "Fake host" });
        builder.AddCapability<ICameraService, FakeCameraService>(NativeCapabilities.Camera);
        builder.AddCapability<IBarcodeScanner, FakeBarcodeScanner>(NativeCapabilities.Barcode);
        builder.AddCapability<IFilePickerService, FakeFilePickerService>(NativeCapabilities.Files);
        builder.AddCapability<ISecureStorage, InMemorySecureStorage>(NativeCapabilities.SecureStorage);
        builder.AddCapability<IShareService, FakeShareService>(NativeCapabilities.Share);
        builder.AddCapability<IBiometricService, FakeBiometricService>(NativeCapabilities.Biometrics);
        builder.AddCapability<IClipboardService, FakeClipboardService>(NativeCapabilities.Clipboard);
        builder.AddCapability<IPermissionService, FakePermissionService>(NativeCapabilities.Permissions);
        builder.AddCapability<IConnectivityService, FakeConnectivityService>(NativeCapabilities.Connectivity);
        builder.AddCapability<IAppLifecycle, FakeAppLifecycle>(NativeCapabilities.Lifecycle);
        builder.AddCapability<INotificationService>(NativeCapabilities.Notifications, static sp => sp.GetRequiredService<FakeNotificationService>());
        builder.Services.AddSingleton(static sp => new FakeNotificationService(sp.GetRequiredService<IActivationPublisher>()));
        return builder;
    }
}
