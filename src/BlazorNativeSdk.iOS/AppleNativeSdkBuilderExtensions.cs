using BlazorNativeSdk.ApplePlatform;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using FileSystem = Microsoft.Maui.Storage.FileSystem;

namespace BlazorNativeSdk;

/// <summary>Registers the iOS / Mac Catalyst adapter.</summary>
public static class AppleNativeSdkBuilderExtensions
{
    /// <summary>
    /// Adds the iOS / Mac Catalyst adapter: permissions, camera, barcode, file pickers, app storage, Keychain secure storage,
    /// share, clipboard, biometrics, notifications, lifecycle, connectivity, background work (BGTaskScheduler) and HTTP transfers,
    /// plus a file-based state store in <c>Library/bns-state</c>.
    /// </summary>
    /// <remarks>
    /// Also call <see cref="AppleNativeSdk.Initialize()"/> from <c>AppDelegate.FinishedLaunching</c> so notification taps that
    /// launch the app and background tasks work, and forward URLs, user activities and quick actions with
    /// <see cref="AppleActivation"/>. Capabilities registered after this call override the adapter's.
    /// </remarks>
    public static INativeSdkBuilder AddApple(this INativeSdkBuilder builder, Action<AppleNativeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new AppleNativeOptions();
        configure?.Invoke(options);
        builder.Services.AddOptions<AppleNativeOptions>();
        if (configure is not null)
        {
            builder.Services.Configure(configure);
        }

        builder.SetHost(AppleHost.Create(options.HostKind));

        // Registered first so the bridge is attached before other startup tasks (persisted work, transfers) run.
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<INativeSdkStartupTask, AppleStartupTask>(static sp => new AppleStartupTask(sp)));

        builder.AddFileStateStore(Path.Combine(FileSystem.AppDataDirectory, "bns-state"));
        builder.AddCapability<IAppStorage, AppleAppStorage>(NativeCapabilities.AppStorage);
        builder.AddCapability<IPermissionService, ApplePermissionService>(NativeCapabilities.Permissions);
        builder.AddCapability<ICameraService, AppleCameraService>(NativeCapabilities.Camera);
        builder.AddCapability<IBarcodeScanner, AppleBarcodeScanner>(NativeCapabilities.Barcode);
        builder.AddCapability<IFilePickerService, AppleFilePickerService>(NativeCapabilities.Files);
        builder.AddCapability<ISecureStorage, AppleSecureStorage>(NativeCapabilities.SecureStorage);
        builder.AddCapability<IShareService, AppleShareService>(NativeCapabilities.Share);
        builder.AddCapability<IClipboardService, AppleClipboardService>(NativeCapabilities.Clipboard);
        builder.AddCapability<IBiometricService, AppleBiometricService>(NativeCapabilities.Biometrics);
        builder.AddCapability<INotificationService, AppleNotificationService>(NativeCapabilities.Notifications);
        builder.AddCapability<IAppLifecycle, AppleAppLifecycle>(NativeCapabilities.Lifecycle);
        builder.AddCapability<IConnectivityService, AppleConnectivityService>(NativeCapabilities.Connectivity);

        // The Core scheduler persists and runs the work; the Apple scheduler wraps it and asks iOS to launch the app for it.
        builder.AddInProcessBackgroundWork(o =>
        {
            o.RunWhileInBackground = OperatingSystem.IsMacCatalyst();
            o.AdapterName = OperatingSystem.IsMacCatalyst() ? "maccatalyst-in-process" : "ios-bgtasks";
        });
        builder.AddCapability<IBackgroundWorkScheduler, AppleBackgroundWorkScheduler>(NativeCapabilities.BackgroundWork);

        // Transfers run in process. iOS suspends the app shortly after it moves to the background (a background URLSession
        // adapter is future work); Mac Catalyst apps keep running.
        builder.AddHttpTransfers(o => o.ContinuesInBackground = OperatingSystem.IsMacCatalyst());
        return builder;
    }

    /// <summary>Alias of <see cref="AddApple"/> for iOS-only apps.</summary>
    public static INativeSdkBuilder AddIos(this INativeSdkBuilder builder, Action<AppleNativeOptions>? configure = null) =>
        builder.AddApple(configure);
}
