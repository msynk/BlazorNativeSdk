using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;
using BlazorNativeSdk.Storage;
using BlazorNativeSdk.Unsupported;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk;

/// <summary>Registers BlazorNativeSdk services.</summary>
public static class NativeSdkServiceCollectionExtensions
{
    /// <summary>
    /// Adds BlazorNativeSdk. Configure adapters in <paramref name="configure"/>, for example
    /// <c>services.AddBlazorNativeSdk(sdk =&gt; sdk.AddBrowser())</c>. Every capability without an adapter is registered
    /// with a fallback that reports <see cref="NativeResultStatus.Unsupported"/>, so injecting any capability interface always works.
    /// </summary>
    public static IServiceCollection AddBlazorNativeSdk(this IServiceCollection services, Action<INativeSdkBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var builder = new NativeSdkBuilder(services);
        configure?.Invoke(builder);
        AddCoreServices(services);
        AddFallbacks(builder);
        return services;
    }

    /// <summary>Registers a background worker under <paramref name="name"/> (resolved per run from a new scope).</summary>
    public static IServiceCollection AddBackgroundWorker<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TWorker>(this IServiceCollection services, string name)
        where TWorker : class, IBackgroundWorker
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        services.AddKeyedTransient<IBackgroundWorker, TWorker>(name);
        return services;
    }

    /// <summary>Registers a background worker under <paramref name="name"/>.</summary>
    public static INativeSdkBuilder AddBackgroundWorker<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TWorker>(this INativeSdkBuilder builder, string name)
        where TWorker : class, IBackgroundWorker
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddBackgroundWorker<TWorker>(name);
        return builder;
    }

    /// <summary>
    /// Registers an <see cref="IActivationHandler"/> (resolved from a new scope per activation). Use it for activations that
    /// must be handled even when no component is rendered, such as background notification actions.
    /// </summary>
    public static INativeSdkBuilder AddActivationHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(this INativeSdkBuilder builder)
        where THandler : class, IActivationHandler
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IActivationHandler, THandler>());
        return builder;
    }

    /// <summary>Registers an <see cref="IActivationTranslator"/>. Translators run in registration order.</summary>
    public static INativeSdkBuilder AddActivationTranslator<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTranslator>(this INativeSdkBuilder builder)
        where TTranslator : class, IActivationTranslator
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IActivationTranslator, TTranslator>());
        return builder;
    }

    /// <summary>Uses a durable, file-based <see cref="INativeStateStore"/>. The default directory is <c>{AppData}/state</c>.</summary>
    [UnsupportedOSPlatform("browser")]
    public static INativeSdkBuilder AddFileStateStore(this INativeSdkBuilder builder, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Replace(ServiceDescriptor.Singleton<INativeStateStore>(sp =>
        {
            if (directory is not null)
            {
                return new FileStateStore(directory);
            }

            var appData = sp.GetRequiredService<IAppStorage>().GetDirectory(AppDirectory.AppData);
            return new FileStateStore(Path.Combine(appData.GetValueOrThrow(), "state"));
        }));
        return builder;
    }

    /// <summary>Uses <see cref="PhysicalAppStorage"/> (user profile folders) for <see cref="IAppStorage"/>.</summary>
    [UnsupportedOSPlatform("browser")]
    public static INativeSdkBuilder AddPhysicalAppStorage(this INativeSdkBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddCapability<IAppStorage, PhysicalAppStorage>(NativeCapabilities.AppStorage);
    }

    /// <summary>Uses <see cref="NetworkInformationConnectivityService"/> for <see cref="IConnectivityService"/>.</summary>
    [UnsupportedOSPlatform("browser")]
    public static INativeSdkBuilder AddSystemConnectivity(this INativeSdkBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddCapability<IConnectivityService, NetworkInformationConnectivityService>(NativeCapabilities.Connectivity);
    }

    /// <summary>Adds the persisted in-process work scheduler (see <see cref="InProcessBackgroundWorkScheduler"/>).</summary>
    public static INativeSdkBuilder AddInProcessBackgroundWork(this INativeSdkBuilder builder, Action<InProcessWorkOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (configure is not null)
        {
            builder.Services.Configure(configure);
        }

        // The scheduler is process-wide: it observes the process's connectivity and lifecycle (see NativeServiceKeys.Process).
        builder.Services.Replace(ServiceDescriptor.Singleton(static sp => new InProcessBackgroundWorkScheduler(
            sp,
            sp.GetRequiredService<INativeStateStore>(),
            NativeServiceKeys.ProcessConnectivity(sp),
            NativeServiceKeys.ProcessLifecycle(sp),
            sp.GetRequiredService<IOptions<InProcessWorkOptions>>(),
            sp.GetRequiredService<ILogger<InProcessBackgroundWorkScheduler>>(),
            sp.GetService<TimeProvider>())));
        builder.AddCapability<IBackgroundWorkScheduler, InProcessBackgroundWorkScheduler>(NativeCapabilities.BackgroundWork);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<INativeSdkStartupTask, InProcessBackgroundWorkScheduler>(
            static sp => sp.GetRequiredService<InProcessBackgroundWorkScheduler>()));
        return builder;
    }

    /// <summary>Adds the resumable HTTP transfer queue (see <see cref="HttpTransferService"/>).</summary>
    [UnsupportedOSPlatform("browser")]
    public static INativeSdkBuilder AddHttpTransfers(this INativeSdkBuilder builder, Action<HttpTransferOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (configure is not null)
        {
            builder.Services.Configure(configure);
        }

        // The queue is process-wide: it observes the process's connectivity (see NativeServiceKeys.Process).
        builder.Services.Replace(ServiceDescriptor.Singleton(static sp => new HttpTransferService(
            sp,
            sp.GetRequiredService<INativeStateStore>(),
            sp.GetRequiredService<IAppStorage>(),
            NativeServiceKeys.ProcessConnectivity(sp),
            sp.GetRequiredService<IOptions<HttpTransferOptions>>(),
            sp.GetRequiredService<ILogger<HttpTransferService>>(),
            sp.GetService<TimeProvider>())));
        builder.AddCapability<ITransferService, HttpTransferService>(NativeCapabilities.Transfers);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<INativeSdkStartupTask, HttpTransferService>(
            static sp => sp.GetRequiredService<HttpTransferService>()));

        // Scheduled only with HttpTransferOptions.UseBackgroundWork; registered always so pending work finds its worker.
        builder.Services.TryAddKeyedTransient<IBackgroundWorker, HttpTransferBackgroundWorker>(HttpTransferService.BackgroundWorkName);
        return builder;
    }

    /// <summary>Configures deep-link routing rules.</summary>
    public static INativeSdkBuilder ConfigureDeepLinks(this INativeSdkBuilder builder, Action<DeepLinkOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        return builder.Configure(o => configure(o.DeepLinks));
    }

    private static void AddCoreServices(IServiceCollection services)
    {
        services.AddOptions();
        if (!services.Any(d => d.ServiceType == typeof(ILoggerFactory)))
        {
            // Hosts normally register logging first; fall back to a no-op logger so the SDK never fails to resolve.
            services.TryAddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.TryAdd(ServiceDescriptor.Singleton(typeof(ILogger<>), typeof(Logger<>)));
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(_ => NativeHostDetector.Detect());
        services.TryAddSingleton<NativeSdkRuntime>();
        services.TryAddScoped<ICapabilityRegistry, CapabilityRegistry>();
        services.TryAddScoped<Components.NativeWindowScope>();

        services.TryAddSingleton<ActivationService>();
        services.TryAddSingleton<IActivationPublisher>(static sp => sp.GetRequiredService<ActivationService>());
        services.TryAddSingleton<INavigationOutletRegistry>(static sp => sp.GetRequiredService<ActivationService>());

        services.TryAddSingleton<INativeStateStore, InMemoryStateStore>();
        services.TryAddSingleton<IWindowPlacementStore, StateStoreWindowPlacementStore>();
    }

    private static void AddFallbacks(NativeSdkBuilder builder)
    {
        var services = builder.Services;

        // Core implementations that are real everywhere.
        AddDefault<IAppActivationService>(builder, NativeCapabilities.DeepLinks, static sp => sp.GetRequiredService<ActivationService>());
        AddDefault<IWorkflowCheckpointStore>(builder, NativeCapabilities.WorkflowCheckpoints,
            static sp => new StateStoreWorkflowCheckpointStore(sp.GetRequiredService<INativeStateStore>(), sp.GetService<TimeProvider>()));
        AddDefault<IAppLifecycle>(builder, NativeCapabilities.Lifecycle,
            static sp => new ManualAppLifecycle(sp.GetService<TimeProvider>(), sp.GetService<ILogger<ManualAppLifecycle>>()));

        // Honest "unsupported" fallbacks.
        AddDefault<ICameraService>(builder, NativeCapabilities.Camera, static sp => new UnsupportedCameraService(Host(sp)));
        AddDefault<IBarcodeScanner>(builder, NativeCapabilities.Barcode, static sp => new UnsupportedBarcodeScanner(Host(sp)));
        AddDefault<IFilePickerService>(builder, NativeCapabilities.Files, static sp => new UnsupportedFilePickerService(Host(sp)));
        AddDefault<IAppStorage>(builder, NativeCapabilities.AppStorage, static sp => new UnsupportedAppStorage(Host(sp)));
        AddDefault<ISecureStorage>(builder, NativeCapabilities.SecureStorage, static sp => new UnsupportedSecureStorage(Host(sp)));
        AddDefault<IShareService>(builder, NativeCapabilities.Share, static sp => new UnsupportedShareService(Host(sp)));
        AddDefault<IBiometricService>(builder, NativeCapabilities.Biometrics, static sp => new UnsupportedBiometricService(Host(sp)));
        AddDefault<IClipboardService>(builder, NativeCapabilities.Clipboard, static sp => new UnsupportedClipboardService(Host(sp)));
        AddDefault<IPermissionService>(builder, NativeCapabilities.Permissions, static sp => new UnsupportedPermissionService(Host(sp)));
        AddDefault<INotificationService>(builder, NativeCapabilities.Notifications, static sp => new UnsupportedNotificationService(Host(sp)));
        AddDefault<IConnectivityService>(builder, NativeCapabilities.Connectivity, static sp => new UnsupportedConnectivityService(Host(sp)));
        AddDefault<IBackgroundWorkScheduler>(builder, NativeCapabilities.BackgroundWork, static sp => new UnsupportedBackgroundWorkScheduler(Host(sp)));
        AddDefault<ITransferService>(builder, NativeCapabilities.Transfers, static sp => new UnsupportedTransferService(Host(sp)));
        AddDefault<IWindowManager>(builder, NativeCapabilities.Windows, static sp => new UnsupportedWindowManager(Host(sp)));
        AddDefault<IScreenService>(builder, NativeCapabilities.Screens, static sp => new UnsupportedScreenService(Host(sp)));
        AddDefault<IGlobalShortcutService>(builder, NativeCapabilities.GlobalShortcuts, static sp => new UnsupportedGlobalShortcutService(Host(sp)));
        AddDefault<ITrayService>(builder, NativeCapabilities.Tray, static sp => new UnsupportedTrayService(Host(sp)));
        AddDefault<INativeMenuService>(builder, NativeCapabilities.Menus, static sp => new UnsupportedNativeMenuService(Host(sp)));
        AddDefault<IDesktopFileOperations>(builder, NativeCapabilities.DesktopFiles, static sp => new UnsupportedDesktopFileOperations(Host(sp)));
        AddDefault<IIpcService>(builder, NativeCapabilities.Ipc, static sp => new UnsupportedIpcService(Host(sp)));
        AddDefault<ISingleInstanceService>(builder, NativeCapabilities.SingleInstance, static sp => new UnsupportedSingleInstanceService(Host(sp)));
        AddDefault<IProtocolRegistrationService>(builder, NativeCapabilities.ProtocolRegistration, static sp => new UnsupportedProtocolRegistrationService(Host(sp)));

        _ = services;

        static NativeHostInfo Host(IServiceProvider sp) => sp.GetRequiredService<NativeHostInfo>();
    }

    private static void AddDefault<TService>(NativeSdkBuilder builder, CapabilityId capability, Func<IServiceProvider, TService> factory)
        where TService : class, INativeCapability
    {
        if (builder.Services.Any(d => d.ServiceType == typeof(TService)))
        {
            // An adapter or the application registered a provider; make sure the registry knows about it.
            if (!builder.Services.Any(d => d.ImplementationInstance is CapabilityRegistration r && r.Capability == capability))
            {
                builder.RegisterCapability(capability, typeof(TService));
            }

            return;
        }

        builder.Services.AddSingleton(factory);
        builder.RegisterCapability(capability, typeof(TService));
    }
}
