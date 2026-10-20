using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.Lifecycle;
using BlazorNativeSdk.Maui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.LifecycleEvents;
#if WINDOWS
using BlazorNativeSdk.WindowsPlatform;
#endif

namespace BlazorNativeSdk;

/// <summary>Registers BlazorNativeSdk in a .NET MAUI app.</summary>
public static class MauiNativeSdkAppBuilderExtensions
{
    /// <summary>
    /// Adds BlazorNativeSdk to a .NET MAUI Blazor Hybrid app: registers the platform adapter of the current target, wires the
    /// MAUI lifecycle to deep links, notification taps and quick actions, starts the SDK when the app is built and, on desktop
    /// targets, provides window management.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b>Android</b>: <c>AddAndroid()</c>; launch and new intents of MAUI activities go to <c>AndroidActivation</c>.</item>
    /// <item><b>iOS / Mac Catalyst</b>: <c>AddApple()</c>; <c>AppleNativeSdk.Initialize</c> runs before launch completes; URLs,
    /// Universal Links / Handoff and quick actions are routed for apps with and without scenes.</item>
    /// <item><b>Windows</b>: <c>AddDesktop()</c> and <c>AddWindows()</c> with a MAUI host bridge; single instance (unpackaged:
    /// Desktop single-instance service, packaged: Windows App SDK <c>AppInstance</c>), the launch activation, a window-driven
    /// <see cref="IAppLifecycle"/> and <see cref="IWindowManager"/>.</item>
    /// <item><b>Mac Catalyst</b> additionally provides <see cref="IWindowManager"/> (needs multiple scenes).</item>
    /// </list>
    /// <paramref name="configure"/> runs after the platform adapter is registered, so capabilities and adapter options set
    /// there (for example <c>sdk.AddWindows(o =&gt; ...)</c> again inside <c>#if WINDOWS</c>) override the defaults.
    /// Calling this method again only applies more configuration.
    /// </remarks>
    /// <param name="builder">The MAUI app builder.</param>
    /// <param name="configure">Configures the SDK (deep links, workers, adapter options, custom capabilities).</param>
    /// <param name="options">Configures <see cref="MauiNativeOptions"/>.</param>
    /// <returns>The builder.</returns>
    public static MauiAppBuilder UseBlazorNativeSdk(this MauiAppBuilder builder, Action<INativeSdkBuilder>? configure = null, Action<MauiNativeOptions>? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        var optionsBuilder = services.AddOptions<MauiNativeOptions>();
        if (options is not null)
        {
            optionsBuilder.Configure(options);
        }

        if (services.Any(static d => d.ServiceType == typeof(MauiIntegration)))
        {
            services.AddBlazorNativeSdk(configure);
            return builder;
        }

        var integration = new MauiIntegration();
        services.AddSingleton(integration);
        services.AddBlazorNativeSdk(sdk =>
        {
            AddPlatformAdapter(sdk);
            AddMauiServices(sdk);
            configure?.Invoke(sdk);

            // Android and iOS adapters provide a lifecycle; fall back to window events only if nothing else does.
            if (!sdk.Services.Any(static d => d.ServiceType == typeof(IAppLifecycle)))
            {
                sdk.AddCapability<IAppLifecycle, MauiWindowLifecycle>(NativeCapabilities.Lifecycle);
            }
        });

        builder.ConfigureLifecycleEvents(integration.ConfigureLifecycleEvents);
        return builder;
    }

    private static void AddPlatformAdapter(INativeSdkBuilder sdk)
    {
#if ANDROID
        sdk.AddAndroid();
#elif IOS || MACCATALYST
        sdk.AddApple();
#elif WINDOWS
        // Desktop first so the Windows adapter overrides the shared desktop defaults (app storage, connectivity).
        sdk.AddDesktop();
        sdk.AddWindows();
        sdk.SetHost(MauiWindowsHost.Create());
#else
        _ = sdk;
#endif
    }

    private static void AddMauiServices(INativeSdkBuilder sdk)
    {
        var services = sdk.Services;
        services.TryAddSingleton<MauiUIDispatcher>();
        services.TryAddSingleton<MauiWindowTracker>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMauiInitializeService, MauiSdkInitializer>());

#if WINDOWS
        services.TryAddSingleton<MauiWindowsHostBridge>();
        services.Replace(ServiceDescriptor.Singleton<IWindowsHostBridge>(static sp => sp.GetRequiredService<MauiWindowsHostBridge>()));
        services.TryAddSingleton<WindowsInstanceCoordinator>();
        sdk.AddCapability<IAppLifecycle, MauiWindowLifecycle>(NativeCapabilities.Lifecycle);
        sdk.AddCapability<IWindowManager, MauiWindowManager>(NativeCapabilities.Windows);
        sdk.AddActivationHandler<MauiFocusOnActivationHandler>();
#elif MACCATALYST
        sdk.AddCapability<IWindowManager, MauiWindowManager>(NativeCapabilities.Windows);
#endif
    }
}
