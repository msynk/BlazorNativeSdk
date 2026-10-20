#if ANDROID
using BlazorNativeSdk.AndroidPlatform;
using Microsoft.Maui;
using Microsoft.Maui.LifecycleEvents;

namespace BlazorNativeSdk.Maui;

/// <summary>Wires MAUI activity events to <see cref="AndroidActivation"/>.</summary>
/// <remarks>
/// Only MAUI activities are considered: helper activities such as the Essentials media/file picker activity also raise
/// lifecycle events and must not be taken for a launch. A restored activity (non-null saved state) never replays its launch
/// intent; the first launch intent in the process is the cold start.
/// </remarks>
internal static class AndroidLifecycleWiring
{
    /// <summary>Registers the lifecycle handlers.</summary>
    public static void Configure(IAndroidLifecycleBuilder android, MauiIntegration integration)
    {
        android
            .OnCreate((activity, savedInstanceState) =>
            {
                if (activity is not MauiAppCompatActivity)
                {
                    return;
                }

                integration.Tracker?.EnsureAttached();
                var isFirstLaunch = integration.TryClaimFirstLaunch();
                if (savedInstanceState is null)
                {
                    AndroidActivation.HandleIntent(activity.Intent, isColdStart: isFirstLaunch);
                }
            })
            .OnNewIntent((activity, intent) =>
            {
                if (activity is MauiAppCompatActivity)
                {
                    AndroidActivation.HandleIntent(intent, isColdStart: false);
                }
            });
    }
}
#endif
