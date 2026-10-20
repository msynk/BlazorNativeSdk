#if IOS || MACCATALYST
using BlazorNativeSdk.ApplePlatform;
using Foundation;
using Microsoft.Maui.LifecycleEvents;
using UIKit;

namespace BlazorNativeSdk.Maui;

/// <summary>
/// Wires the iOS / Mac Catalyst app delegate and scene delegate callbacks that MAUI exposes to the Apple adapter:
/// SDK initialization before launch completes, and URL, Universal Link / Handoff and quick action activations for apps with
/// and without scenes.
/// </summary>
internal static class AppleLifecycleWiring
{
    private const string UrlKind = "url";
    private const string ActivityKind = "activity";
    private const string ShortcutKind = "shortcut";

    // Activities MAUI uses internally to open windows and restore their state.
    private const string MauiActivityPrefix = "__MAUI";
    private const string MauiWindowIdKey = "__MAUI_WINDOW_ID__";
    private const string LaunchActivityTypeKey = "UIApplicationLaunchOptionsUserActivityTypeKey";

    /// <summary>Registers the lifecycle handlers.</summary>
    public static void Configure(IiOSLifecycleBuilder ios, MauiIntegration integration)
    {
        var replay = integration.ReplayFilter;
        ios
            .WillFinishLaunching((application, launchOptions) =>
            {
                // BGTaskScheduler handlers and the notification delegate must be installed before launch completes.
                // The MAUI initializer normally did this already while the app was built; Initialize is idempotent.
                integration.InitializeAppleSdk();
                return true;
            })
            .FinishedLaunching((application, launchOptions) =>
            {
                integration.Tracker?.EnsureAttached();

                // Apps without scenes receive the launch URL, user activity or quick action again through the delegate
                // callbacks below (MAUI returns true from didFinishLaunching); publish them there, flagged as the cold start.
                ExpectLaunchActivations(replay, launchOptions);
                return true;
            })
            .OpenUrl((application, url, options) => HandleUrl(replay, url))
            .ContinueUserActivity((application, activity, restorationHandler) => HandleActivity(replay, activity))
            .PerformActionForShortcutItem((application, item, completion) => completion(HandleShortcut(replay, item)))
            .SceneWillConnect((scene, session, connectionOptions) => OnSceneWillConnect(integration, replay, connectionOptions))
            .SceneOpenUrl((scene, urlContexts) => HandleUrlContexts(replay, urlContexts))
            .SceneContinueUserActivity((scene, activity) => HandleActivity(replay, activity));
    }

    private static void ExpectLaunchActivations(ActivationReplayFilter replay, NSDictionary? launchOptions)
    {
        if (launchOptions is null)
        {
            return;
        }

        if (launchOptions[UIApplication.LaunchOptionsUrlKey] is NSUrl url)
        {
            replay.ExpectColdStart(UrlKind, url.AbsoluteString);
        }

        if (launchOptions[UIApplication.LaunchOptionsUserActivityDictionaryKey] is NSDictionary activity)
        {
            replay.ExpectColdStart(ActivityKind, (activity[LaunchActivityTypeKey] as NSString)?.ToString());
        }

        if (launchOptions[UIApplication.LaunchOptionsShortcutItemKey] is UIApplicationShortcutItem shortcut)
        {
            replay.ExpectColdStart(ShortcutKind, shortcut.Type);
        }
    }

    private static void OnSceneWillConnect(MauiIntegration integration, ActivationReplayFilter replay, UISceneConnectionOptions connectionOptions)
    {
        integration.Tracker?.EnsureAttached();

        // Connection options carry what launched (or created) the scene; UIKit does not deliver them again through
        // scene:openURLContexts: / scene:continueUserActivity:, so publish them here.
        var isColdStart = integration.TryClaimFirstScene();
        foreach (var context in Enumerate(connectionOptions.UrlContexts))
        {
            if (context.Url is { } url)
            {
                AppleActivation.HandleOpenUrl(url, isColdStart);
            }
        }

        foreach (var activity in Enumerate(connectionOptions.UserActivities))
        {
            if (!IsMauiActivity(activity))
            {
                replay.SuppressReplay(ActivityKind, ActivityKey(activity));
                AppleActivation.HandleUserActivity(activity, isColdStart);
            }
        }

        if (connectionOptions.ShortcutItem is { } shortcut)
        {
            // MAUI replays the launch quick action through PerformActionForShortcutItem once the scene is active.
            replay.SuppressReplay(ShortcutKind, shortcut.Type);
            AppleActivation.HandleShortcutItem(shortcut, isColdStart);
        }
    }

    private static bool HandleUrlContexts(ActivationReplayFilter replay, NSSet<UIOpenUrlContext> urlContexts)
    {
        var handled = false;
        foreach (var context in Enumerate(urlContexts))
        {
            if (context.Url is { } url)
            {
                handled |= HandleUrl(replay, url);
            }
        }

        return handled;
    }

    private static bool HandleUrl(ActivationReplayFilter replay, NSUrl url) =>
        replay.Classify(UrlKind, url.AbsoluteString) switch
        {
            ActivationDelivery.Replay => true,
            ActivationDelivery.ColdStart => AppleActivation.HandleOpenUrl(url, isColdStart: true),
            _ => AppleActivation.HandleOpenUrl(url),
        };

    private static bool HandleActivity(ActivationReplayFilter replay, NSUserActivity activity)
    {
        if (IsMauiActivity(activity))
        {
            return false;
        }

        var delivery = replay.Classify(ActivityKind, ActivityKey(activity));
        if (delivery == ActivationDelivery.Normal)
        {
            // A launch user activity is matched on its type alone (launch options do not carry the activity itself).
            delivery = replay.Classify(ActivityKind, activity.ActivityType);
        }

        return delivery switch
        {
            ActivationDelivery.Replay => true,
            ActivationDelivery.ColdStart => AppleActivation.HandleUserActivity(activity, isColdStart: true),
            _ => AppleActivation.HandleUserActivity(activity),
        };
    }

    private static bool HandleShortcut(ActivationReplayFilter replay, UIApplicationShortcutItem item) =>
        replay.Classify(ShortcutKind, item.Type) switch
        {
            ActivationDelivery.Replay => true,
            ActivationDelivery.ColdStart => AppleActivation.HandleShortcutItem(item, isColdStart: true),
            _ => AppleActivation.HandleShortcutItem(item),
        };

    private static string ActivityKey(NSUserActivity activity) =>
        activity.ActivityType + "|" + activity.WebPageUrl?.AbsoluteString;

    private static bool IsMauiActivity(NSUserActivity activity)
    {
        if (activity.ActivityType.StartsWith(MauiActivityPrefix, StringComparison.Ordinal))
        {
            return true;
        }

        using var key = new NSString(MauiWindowIdKey);
        return activity.UserInfo?.ContainsKey(key) == true;
    }

    private static T[] Enumerate<T>(NSSet<T>? set)
        where T : NSObject
    {
        if (set is null)
        {
            return [];
        }

        try
        {
            return set.ToArray();
        }
        catch (InvalidCastException)
        {
            // Some bindings surface unexpected element types; ignore them rather than failing the launch.
            return [];
        }
    }
}
#endif
