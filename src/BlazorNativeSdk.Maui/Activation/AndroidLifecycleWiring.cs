#if ANDROID
using Android.Content;
using BlazorNativeSdk.AndroidPlatform;
using Microsoft.Maui;
using Microsoft.Maui.LifecycleEvents;

namespace BlazorNativeSdk.Maui;

/// <summary>Wires MAUI activity events to <see cref="AndroidActivation"/>.</summary>
/// <remarks>
/// Only MAUI activities are considered: helper activities such as the Essentials media/file picker activity also raise
/// lifecycle events and must not be taken for a launch. A restored activity (non-null saved state) never replays its launch
/// intent. The cold start is the first launch intent in the process, or, when the first activity was restored after the OS
/// killed the process, the intent that re-created it (see <see cref="AndroidLaunchTracker"/>).
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
                var launch = integration.AndroidLaunches.OnCreate(restored: savedInstanceState is not null);
                if (savedInstanceState is null)
                {
                    AndroidActivation.HandleIntent(activity.Intent, isColdStart: launch == AndroidLaunchKind.ColdStart);
                }
            })
            .OnNewIntent((activity, intent) =>
            {
                if (activity is MauiAppCompatActivity)
                {
                    AndroidActivation.HandleIntent(intent, isColdStart: integration.AndroidLaunches.OnNewIntent());
                }
            })
            .OnResume(activity =>
            {
                if (activity is MauiAppCompatActivity && integration.AndroidLaunches.OnResume())
                {
                    // Reopened from recents after the process was killed: publish a plain cold-start launch in place of
                    // the old launch intent, which must not be replayed.
                    using var launch = new Intent(Intent.ActionMain);
                    AndroidActivation.HandleIntent(launch, isColdStart: true);
                }
            });
    }
}
#endif
