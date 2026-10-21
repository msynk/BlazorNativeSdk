using BlazorNativeSdk.Browser;
using BlazorNativeSdk.Browser.Hosting;
using BlazorNativeSdk.Browser.Interop;
using BlazorNativeSdk.Browser.Services;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk;

/// <summary>Registers the browser adapter.</summary>
public static class BrowserNativeSdkBuilderExtensions
{
    /// <summary>
    /// Provides device capabilities through web platform APIs: camera, barcode scanning, files, share, clipboard,
    /// permissions, notifications, page lifecycle, connectivity, protocol-handler and PWA launch activations, and (opt-in)
    /// best-effort secure storage. Biometrics and app directories report explicit <see cref="NativeResultStatus.Unsupported"/> reasons.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Blazor WebAssembly</b>: services are singletons, <see cref="INativeStateStore"/> is backed by <c>localStorage</c>
    /// and the listeners start with the SDK.
    /// </para>
    /// <para>
    /// <b>Blazor Server / interactive server rendering</b>: .NET runs on the server but every capability runs in the
    /// <em>user's</em> browser through the circuit, so services are scoped per circuit. Activation routing is also made
    /// per circuit, so one user's notification tap or deep link can never navigate another user's session. Place
    /// <see cref="BrowserNativeSdkInitializer"/> in the layout to start the listeners of each circuit. The SDK's own state
    /// store stays on the server. During prerendering, capabilities report <see cref="CapabilityAvailability.Unknown"/> and
    /// operations return <see cref="NativeResultStatus.Unavailable"/> with <see cref="NativeErrorCodes.HostNotReady"/>.
    /// </para>
    /// </remarks>
    public static INativeSdkBuilder AddBrowser(this INativeSdkBuilder builder, Action<BrowserNativeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return AddBrowserCore(builder, configure, OperatingSystem.IsBrowser());
    }

    internal static INativeSdkBuilder AddBrowserCore(INativeSdkBuilder builder, Action<BrowserNativeOptions>? configure, bool isWebAssembly)
    {
        var services = builder.Services;
        var lifetime = isWebAssembly ? ServiceLifetime.Singleton : ServiceLifetime.Scoped;

        builder.SetHost(new NativeHostInfo
        {
            Platform = NativePlatform.Browser,
            Host = isWebAssembly ? NativeHostKind.WebAssembly : NativeHostKind.Server,
            Description = isWebAssembly ? "Blazor WebAssembly in a browser" : "Blazor Server (capabilities run in the user's browser)",
        });

        var options = services.AddOptions<BrowserNativeOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<BrowserNativeOptions>, BrowserNativeOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);

        services.TryAdd(new ServiceDescriptor(typeof(BrowserInterop), typeof(BrowserInterop), lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(BrowserActivationBridge), typeof(BrowserActivationBridge), lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(BrowserStartup), typeof(BrowserStartup), lifetime));
        services.TryAdd(new ServiceDescriptor(
            typeof(BrowserProtocolHandlers),
            static sp => new BrowserProtocolHandlers(
                sp.GetRequiredService<BrowserInterop>(),
                sp.GetRequiredService<IOptions<NativeSdkOptions>>(),
                sp.GetRequiredService<ILogger<BrowserProtocolHandlers>>()),
            lifetime));

        builder
            .AddCapability<ICameraService, BrowserCameraService>(NativeCapabilities.Camera, lifetime)
            .AddCapability<IBarcodeScanner, BrowserBarcodeScanner>(NativeCapabilities.Barcode, lifetime)
            .AddCapability<IFilePickerService, BrowserFilePickerService>(NativeCapabilities.Files, lifetime)
            .AddCapability<ISecureStorage, BrowserSecureStorage>(NativeCapabilities.SecureStorage, lifetime)
            .AddCapability<IShareService, BrowserShareService>(NativeCapabilities.Share, lifetime)
            .AddCapability<IClipboardService, BrowserClipboardService>(NativeCapabilities.Clipboard, lifetime)
            .AddCapability<IPermissionService, BrowserPermissionService>(NativeCapabilities.Permissions, lifetime)
            .AddCapability<INotificationService, BrowserNotificationService>(NativeCapabilities.Notifications, lifetime)
            .AddCapability<IAppLifecycle, BrowserAppLifecycle>(NativeCapabilities.Lifecycle, lifetime)
            .AddCapability<IConnectivityService, BrowserConnectivityService>(NativeCapabilities.Connectivity, lifetime)
            .AddCapability<IBiometricService, BrowserBiometricService>(NativeCapabilities.Biometrics, ServiceLifetime.Singleton)
            .AddCapability<IAppStorage, BrowserAppStorage>(NativeCapabilities.AppStorage, ServiceLifetime.Singleton);

        if (isWebAssembly)
        {
            services.Replace(ServiceDescriptor.Singleton<INativeStateStore, LocalStorageStateStore>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<INativeSdkStartupTask, BrowserStartupTask>());
        }
        else
        {
            // Core registers the activation pipeline as a singleton. On a server that pipeline would be shared by every
            // circuit, so a tap in one user's browser could navigate another user's session. Make it per circuit.
            // (No INativeSdkStartupTask either: startup tasks are resolved from the root provider, which cannot create
            // per-circuit services; BrowserNativeSdkInitializer starts each circuit instead.)
            services.Replace(ServiceDescriptor.Scoped<ActivationService, ActivationService>());
            services.Replace(ServiceDescriptor.Scoped<IActivationPublisher>(static sp => sp.GetRequiredService<ActivationService>()));
            services.Replace(ServiceDescriptor.Scoped<INavigationOutletRegistry>(static sp => sp.GetRequiredService<ActivationService>()));
            builder.AddCapability<IAppActivationService>(NativeCapabilities.DeepLinks, static sp => sp.GetRequiredService<ActivationService>(), ServiceLifetime.Scoped);

            // Connectivity and lifecycle are per circuit (the user's browser). Process-wide services that run on the server,
            // such as the in-process work scheduler and HTTP transfers, observe the server process instead.
            if (!OperatingSystem.IsBrowser())
            {
                services.TryAddKeyedSingleton<IConnectivityService, NetworkInformationConnectivityService>(NativeServiceKeys.Process);
                services.TryAddKeyedSingleton<IAppLifecycle, ManualAppLifecycle>(NativeServiceKeys.Process);
            }
        }

        return builder;
    }
}

/// <summary>Validates <see cref="BrowserNativeOptions"/>.</summary>
internal sealed class BrowserNativeOptionsValidator : IValidateOptions<BrowserNativeOptions>
{
    public ValidateOptionsResult Validate(string? name, BrowserNativeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.ProtocolHandlerQueryParameter) ||
            !options.ProtocolHandlerQueryParameter.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            failures.Add("ProtocolHandlerQueryParameter must be a non-empty name made of ASCII letters, digits, '-', '_' or '.'.");
        }

        if (string.IsNullOrWhiteSpace(options.SecureStorageDatabaseName))
        {
            failures.Add("SecureStorageDatabaseName must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(options.ModulePath))
        {
            failures.Add("ModulePath must not be empty.");
        }

        if (options.InteropTimeout <= TimeSpan.Zero)
        {
            failures.Add("InteropTimeout must be positive.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
