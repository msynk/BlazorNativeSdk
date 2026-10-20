using BlazorNativeSdk.AndroidPlatform;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using FileSystem = Microsoft.Maui.Storage.FileSystem;

namespace BlazorNativeSdk;

/// <summary>Registers the Android adapter.</summary>
public static class AndroidNativeSdkBuilderExtensions
{
    /// <summary>
    /// Adds the Android implementations of the device, lifecycle and background-work capabilities, a file-based state store
    /// in <c>{AppData}/bns-state</c> and the HTTP transfer queue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Works in .NET MAUI apps (Microsoft.Maui.Essentials is initialized by MAUI) and in .NET for Android apps that call
    /// <c>Microsoft.Maui.ApplicationModel.Platform.Init(activity, savedInstanceState)</c> in their activity, forward
    /// <c>OnRequestPermissionsResult</c> to <c>Platform.OnRequestPermissionsResult</c> and start the SDK with
    /// <c>services.StartBlazorNativeSdkAsync()</c> (ideally in <c>Application.OnCreate</c>, so background work and
    /// notification actions find the service provider in a process the OS started).
    /// </para>
    /// <para>
    /// Required manifest entries depend on the capabilities used (camera, notifications, exact alarms, boot receiver,
    /// network state, intent filters for deep links); see the Android adapter documentation.
    /// </para>
    /// </remarks>
    /// <param name="builder">The SDK builder.</param>
    /// <param name="configure">Configures <see cref="AndroidNativeOptions"/>.</param>
    public static INativeSdkBuilder AddAndroid(this INativeSdkBuilder builder, Action<AndroidNativeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new AndroidNativeOptions();
        configure?.Invoke(options);
        options.Validate();
        NativeSdkWorker.StartupTimeout = options.WorkerStartupTimeout;

        var services = builder.Services;
        services.Replace(ServiceDescriptor.Singleton(options));
        builder.SetHost(AndroidHostInfoFactory.Create(options.HostKind));

        // Must run first: receivers, workers and activation helpers reach the container through it.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INativeSdkStartupTask, AndroidStartupTask>());

        builder.AddCapability<IAppStorage, AndroidAppStorage>(NativeCapabilities.AppStorage);
        builder.AddFileStateStore(Path.Combine(FileSystem.AppDataDirectory, "bns-state"));

        builder.AddCapability<IPermissionService, AndroidPermissionService>(NativeCapabilities.Permissions);
        builder.AddCapability<ICameraService, AndroidCameraService>(NativeCapabilities.Camera);
        builder.AddCapability<IBarcodeScanner, AndroidBarcodeScanner>(NativeCapabilities.Barcode);
        builder.AddCapability<IFilePickerService, AndroidFilePickerService>(NativeCapabilities.Files);
        builder.AddCapability<ISecureStorage, AndroidSecureStorage>(NativeCapabilities.SecureStorage);
        builder.AddCapability<IShareService, AndroidShareService>(NativeCapabilities.Share);
        builder.AddCapability<IClipboardService, AndroidClipboardService>(NativeCapabilities.Clipboard);
        builder.AddCapability<IBiometricService, AndroidBiometricService>(NativeCapabilities.Biometrics);
        builder.AddCapability<INotificationService, AndroidNotificationService>(NativeCapabilities.Notifications);
        builder.AddCapability<IAppLifecycle, AndroidAppLifecycle>(NativeCapabilities.Lifecycle);
        builder.AddCapability<IConnectivityService, AndroidConnectivityService>(NativeCapabilities.Connectivity);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<INativeSdkStartupTask, AndroidAppLifecycle>(
            static sp => sp.GetRequiredService<AndroidAppLifecycle>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INativeSdkStartupTask, AndroidNotificationService>(
            static sp => sp.GetRequiredService<AndroidNotificationService>()));

        if (options.UseWorkManager)
        {
            builder.AddCapability<IBackgroundWorkScheduler, AndroidWorkManagerScheduler>(NativeCapabilities.BackgroundWork);
        }
        else
        {
            builder.AddInProcessBackgroundWork(o =>
            {
                o.AdapterName = "android-in-process";
                o.RunWhileInBackground = false;
            });
        }

        // Android may suspend or kill the process in the background; transfers resume on the next launch.
        builder.AddHttpTransfers(o => o.ContinuesInBackground = false);
        return builder;
    }
}
