using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>
/// Runs when the SDK starts: attaches the container to the static bridge (flushing buffered activations), starts the lifecycle
/// and connectivity observers, and installs the notification delegate when <see cref="AppleNativeSdk.Initialize()"/> was not called.
/// </summary>
internal sealed class AppleStartupTask(IServiceProvider services) : INativeSdkStartupTask
{
    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        AppleServices.Attach(services);

        // Resolve the observers now so state and connectivity are tracked from start-up rather than from first use.
        _ = services.GetService<IAppLifecycle>();
        _ = services.GetService<IConnectivityService>();

        if (!AppleNativeSdk.IsInitialized)
        {
            // Too late for the tap that launched the app and for BGTaskScheduler registration, but later taps are routed.
            AppleLaunchTracker.Start(duringLaunch: false);
            AppleNativeSdk.EnsureNotificationDelegate();
        }

        return ValueTask.CompletedTask;
    }
}
