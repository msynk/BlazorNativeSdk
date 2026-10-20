using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;
using BlazorNativeSdk.WindowsPlatform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk;

/// <summary>Registers the Windows adapter.</summary>
public static class WindowsNativeSdkBuilderExtensions
{
    /// <summary>
    /// Adds the Windows adapter: displays, global shortcuts, notification-area icons, native menus, DPAPI secure storage,
    /// Windows Hello, clipboard, file pickers, share UI, toast notifications, connectivity, permissions, media picking and
    /// app storage. Works in WPF, Windows Forms and .NET MAUI (WinUI) hosts.
    /// </summary>
    /// <remarks>
    /// Host adapters register an <see cref="IWindowsHostBridge"/> that knows the main window and UI dispatcher; without one
    /// the adapter uses <see cref="DefaultWindowsHostBridge"/>. Window management (<see cref="IWindowManager"/>) belongs to
    /// host adapters, which can build on <see cref="Win32NativeWindowBase"/>. IPC, single instance, shell file operations
    /// and protocol registration come from <c>BlazorNativeSdk.Desktop</c>. Barcode scanning reports
    /// <see cref="NativeResultStatus.Unsupported"/> until you register a decoder.
    /// </remarks>
    public static INativeSdkBuilder AddWindows(this INativeSdkBuilder builder, Action<WindowsNativeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        var options = services.AddOptions<WindowsNativeOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddSingleton<IWindowsHostBridge, DefaultWindowsHostBridge>();
        services.TryAddSingleton(static sp => new MessageWindowThread(sp.GetService<ILogger<MessageWindowThread>>()));
        services.TryAddSingleton(static sp => new ToastActivationKeyStore(
            Path.Combine(WindowsAppStorage.GetAppDataRoot(WindowsAdapter.ToFolderName(AppId(sp))), "notifications"),
            AppId(sp)));

        services.TryAddSingleton(static sp => new WindowsScreenService());
        services.TryAddSingleton(static sp => new WindowsGlobalShortcutService(Thread(sp), Logger<WindowsGlobalShortcutService>(sp)));
        services.TryAddSingleton(static sp => new WindowsNativeMenuService(Thread(sp), Bridge(sp), Logger<WindowsNativeMenuService>(sp), sp));
        services.TryAddSingleton(static sp => new WindowsTrayService(Thread(sp), sp.GetRequiredService<WindowsNativeMenuService>(), Logger<WindowsTrayService>(sp)));
        services.TryAddSingleton(static sp => new DpapiSecureStorage(
            sp.GetRequiredService<IOptions<NativeSdkOptions>>(), sp.GetRequiredService<IOptions<WindowsNativeOptions>>()));
        services.TryAddSingleton(static sp => new WindowsAppStorage(sp.GetRequiredService<IOptions<NativeSdkOptions>>()));
        services.TryAddSingleton(static sp => new WindowsHelloBiometricService(Bridge(sp), Logger<WindowsHelloBiometricService>(sp)));
        services.TryAddSingleton(static sp => new WindowsClipboardService(Thread(sp), Logger<WindowsClipboardService>(sp)));
        services.TryAddSingleton(static sp => new WindowsFilePickerService(Bridge(sp), Logger<WindowsFilePickerService>(sp)));
        services.TryAddSingleton(static sp => new WindowsCameraService(sp.GetRequiredService<WindowsFilePickerService>()));
        services.TryAddSingleton(static sp => new WindowsBarcodeScanner());
        services.TryAddSingleton(static sp => new WindowsShareService(
            Bridge(sp), sp.GetRequiredService<IOptions<NativeSdkOptions>>(), Logger<WindowsShareService>(sp)));
        services.TryAddSingleton(static sp => new WindowsNotificationService(
            sp.GetRequiredService<IOptions<NativeSdkOptions>>(),
            sp.GetRequiredService<IOptions<WindowsNativeOptions>>(),
            sp.GetRequiredService<IActivationPublisher>(),
            sp.GetRequiredService<ToastActivationKeyStore>(),
            Logger<WindowsNotificationService>(sp),
            sp.GetService<TimeProvider>()));
        services.TryAddSingleton(static sp => new WindowsConnectivityService(Logger<WindowsConnectivityService>(sp), sp.GetService<TimeProvider>()));
        services.TryAddSingleton(static sp => new WindowsPermissionService(sp.GetRequiredService<WindowsNotificationService>(), Logger<WindowsPermissionService>(sp)));

        builder.AddCapability<IScreenService>(NativeCapabilities.Screens, static sp => sp.GetRequiredService<WindowsScreenService>());
        builder.AddCapability<IGlobalShortcutService>(NativeCapabilities.GlobalShortcuts, static sp => sp.GetRequiredService<WindowsGlobalShortcutService>());
        builder.AddCapability<INativeMenuService>(NativeCapabilities.Menus, static sp => sp.GetRequiredService<WindowsNativeMenuService>());
        builder.AddCapability<ITrayService>(NativeCapabilities.Tray, static sp => sp.GetRequiredService<WindowsTrayService>());
        builder.AddCapability<ISecureStorage>(NativeCapabilities.SecureStorage, static sp => sp.GetRequiredService<DpapiSecureStorage>());
        builder.AddCapability<IAppStorage>(NativeCapabilities.AppStorage, static sp => sp.GetRequiredService<WindowsAppStorage>());
        builder.AddCapability<IBiometricService>(NativeCapabilities.Biometrics, static sp => sp.GetRequiredService<WindowsHelloBiometricService>());
        builder.AddCapability<IClipboardService>(NativeCapabilities.Clipboard, static sp => sp.GetRequiredService<WindowsClipboardService>());
        builder.AddCapability<IFilePickerService>(NativeCapabilities.Files, static sp => sp.GetRequiredService<WindowsFilePickerService>());
        builder.AddCapability<ICameraService>(NativeCapabilities.Camera, static sp => sp.GetRequiredService<WindowsCameraService>());
        builder.AddCapability<IBarcodeScanner>(NativeCapabilities.Barcode, static sp => sp.GetRequiredService<WindowsBarcodeScanner>());
        builder.AddCapability<IShareService>(NativeCapabilities.Share, static sp => sp.GetRequiredService<WindowsShareService>());
        builder.AddCapability<INotificationService>(NativeCapabilities.Notifications, static sp => sp.GetRequiredService<WindowsNotificationService>());
        builder.AddCapability<IConnectivityService>(NativeCapabilities.Connectivity, static sp => sp.GetRequiredService<WindowsConnectivityService>());
        builder.AddCapability<IPermissionService>(NativeCapabilities.Permissions, static sp => sp.GetRequiredService<WindowsPermissionService>());

        // Turns signed toast launch URIs (protocol activation) back into notification activations and drops forged ones.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IActivationTranslator, WindowsToastActivationTranslator>(static sp => new WindowsToastActivationTranslator(
            sp.GetRequiredService<IOptions<WindowsNativeOptions>>(),
            sp.GetRequiredService<ToastActivationKeyStore>(),
            Logger<WindowsToastActivationTranslator>(sp))));

        return builder;

        static MessageWindowThread Thread(IServiceProvider sp) => sp.GetRequiredService<MessageWindowThread>();

        static IWindowsHostBridge Bridge(IServiceProvider sp) => sp.GetRequiredService<IWindowsHostBridge>();

        static string AppId(IServiceProvider sp) => sp.GetRequiredService<IOptions<NativeSdkOptions>>().Value.AppId;

        static ILogger<T> Logger<T>(IServiceProvider sp) => sp.GetService<ILogger<T>>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;
    }
}
