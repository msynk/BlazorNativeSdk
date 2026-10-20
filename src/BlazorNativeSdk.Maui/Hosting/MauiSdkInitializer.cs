using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Maui.Hosting;
#if IOS || MACCATALYST
using BlazorNativeSdk.ApplePlatform;
#endif

namespace BlazorNativeSdk.Maui;

/// <summary>
/// Starts BlazorNativeSdk while the MAUI app is built (<see cref="IMauiInitializeService"/>), not when the first component
/// renders: a process that the OS started for background work (Android WorkManager, iOS BGTaskScheduler) or a notification
/// action finds the services running even though no UI ever appears.
/// </summary>
/// <remarks>
/// MAUI builds the app on the main thread before any window exists: in <c>MauiApplication.OnCreate</c> (Android),
/// <c>application:willFinishLaunchingWithOptions:</c> (iOS, Mac Catalyst) and <c>OnLaunched</c> (Windows).
/// </remarks>
internal sealed partial class MauiSdkInitializer : IMauiInitializeService
{
    private readonly MauiIntegration _integration;

    /// <summary>Creates the initializer.</summary>
    public MauiSdkInitializer(MauiIntegration integration) => _integration = integration;

    /// <inheritdoc />
    public void Initialize(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.GetRequiredService<IOptions<MauiNativeOptions>>().Value.Validate();
        _integration.Attach(services);

#if WINDOWS
        // Single instance first: a secondary instance hands over its activation and ends here, before it starts persisted
        // work or transfers that belong to the primary instance.
        var coordinator = services.GetRequiredService<WindowsInstanceCoordinator>();
        coordinator.InitializeLaunch();
#endif

#if IOS || MACCATALYST
        // Still inside willFinishLaunching: the only moment iOS accepts BGTaskScheduler registrations and guarantees the
        // notification delegate sees the tap that launched the app. AttachServices then starts the runtime.
        _integration.InitializeAppleSdk();
        AppleNativeSdk.AttachServices(services);
#else
        services.GetRequiredService<NativeSdkRuntime>().EnsureStarted();
#endif

        // Observe the lifecycle and windows from the start (the main window is created right after this returns).
        try
        {
            _ = services.GetService<IAppLifecycle>();
            _ = services.GetService<IWindowManager>();
        }
        catch (Exception ex)
        {
            LogEagerResolutionFailed(services.GetService<ILogger<MauiSdkInitializer>>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MauiSdkInitializer>.Instance, ex);
        }

#if WINDOWS
        coordinator.StartBackgroundWork();
#endif
    }

    [LoggerMessage(EventId = 8601, Level = LogLevel.Error, Message = "Creating the lifecycle or window services at startup failed; they are created on first use instead.")]
    private static partial void LogEagerResolutionFailed(ILogger logger, Exception exception);
}
