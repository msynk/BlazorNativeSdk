using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>
/// Converts app delegate / scene delegate callbacks into <see cref="AppActivation"/>s routed by
/// <see cref="IAppActivationService"/>. Safe to call before the service provider exists (cold start): activations are buffered.
/// </summary>
/// <remarks>
/// Typical wiring in a MAUI app (<c>ConfigureLifecycleEvents</c>):
/// <code>
/// events.AddiOS(ios => ios
///     .FinishedLaunching((app, options) => { AppleNativeSdk.Initialize(); return true; })
///     .OpenUrl((app, url, options) => AppleActivation.HandleOpenUrl(url))
///     .ContinueUserActivity((app, activity, handler) => AppleActivation.HandleUserActivity(activity))
///     .PerformActionForShortcutItem((app, item, completion) => completion(AppleActivation.HandleShortcutItem(item)))
///     .SceneWillConnect((scene, session, options) => { /* forward options.UrlContexts / UserActivities / ShortcutItem with isColdStart: true */ }));
/// </code>
/// Only URLs whose scheme or host is allowed by <c>DeepLinkOptions</c> are navigated to; handlers still observe every activation.
/// </remarks>
public static class AppleActivation
{
    /// <summary>
    /// Handles a custom-scheme URL or a document opened with the app (<c>application:openURL:options:</c>,
    /// <c>scene:openURLContexts:</c>). File URLs become <see cref="ActivationKind.File"/> activations.
    /// </summary>
    /// <param name="url">The URL.</param>
    /// <param name="isColdStart"><see langword="true"/> when the URL launched the app (launch options or scene connection options).</param>
    /// <returns><see langword="true"/> when an activation was published.</returns>
    public static bool HandleOpenUrl(NSUrl url, bool isColdStart = false)
    {
        ArgumentNullException.ThrowIfNull(url);
        AppActivation activation;
        if (url.IsFileUrl && url.Path is { Length: > 0 } path)
        {
            // Documents opened in place live outside the sandbox; keep access for the life of the process.
            url.StartAccessingSecurityScopedResource();
            activation = new AppActivation
            {
                Kind = ActivationKind.File,
                Uri = Uri.TryCreate(url.AbsoluteString, UriKind.Absolute, out var fileUri) ? fileUri : null,
                Files = [new PhysicalNativeFile(path)],
                IsColdStart = isColdStart,
                Source = AppleHost.AdapterName,
            };
        }
        else if (Uri.TryCreate(url.AbsoluteString, UriKind.Absolute, out var uri))
        {
            activation = new AppActivation
            {
                Kind = ActivationKind.DeepLink,
                Uri = uri,
                Data = AppleActivationMapping.ParseQuery(uri.Query),
                IsColdStart = isColdStart,
                Source = AppleHost.AdapterName,
            };
        }
        else
        {
            AppleLog.ActivationIgnored(AppleServices.Logger, "the URL is not an absolute URI");
            return false;
        }

        return Publish(activation);
    }

    /// <summary>
    /// Handles <c>application:continueUserActivity:</c>. Universal Links (<c>NSUserActivityTypeBrowsingWeb</c>) become
    /// <see cref="ActivationKind.DeepLink"/> activations; other activities (Handoff, Spotlight, Siri) become
    /// <see cref="ActivationKind.Custom"/> activations whose data contains <c>activityType</c> and the string userInfo values.
    /// </summary>
    /// <param name="activity">The user activity.</param>
    /// <param name="isColdStart"><see langword="true"/> when the activity launched the app.</param>
    /// <returns><see langword="true"/> when an activation was published.</returns>
    public static bool HandleUserActivity(NSUserActivity activity, bool isColdStart = false)
    {
        ArgumentNullException.ThrowIfNull(activity);
        Uri? webPage = activity.WebPageUrl is { } web && Uri.TryCreate(web.AbsoluteString, UriKind.Absolute, out var parsed) ? parsed : null;
        var isBrowsingWeb = string.Equals(activity.ActivityType, NSUserActivityType.BrowsingWeb.ToString(), StringComparison.Ordinal);
        if (isBrowsingWeb)
        {
            if (webPage is null)
            {
                AppleLog.ActivationIgnored(AppleServices.Logger, "a browsing-web activity without a web page URL");
                return false;
            }

            return Publish(new AppActivation
            {
                Kind = ActivationKind.DeepLink,
                Uri = webPage,
                Data = AppleActivationMapping.ParseQuery(webPage.Query),
                IsColdStart = isColdStart,
                Source = AppleHost.AdapterName,
            });
        }

        var data = NativeSdkNotificationCenterDelegate.ToStringDictionary(activity.UserInfo);
        data[AppleActivationMapping.ActivityTypeKey] = activity.ActivityType;
        return Publish(new AppActivation
        {
            Kind = ActivationKind.Custom,
            Uri = webPage,
            Data = data,
            IsColdStart = isColdStart,
            Source = AppleHost.AdapterName,
        });
    }

    /// <summary>
    /// Handles a Home Screen quick action (<c>application:performActionForShortcutItem:</c> or the launch/scene connection
    /// options). The activation data contains <c>type</c> and the string userInfo values; a userInfo <c>route</c> starting with
    /// <c>/</c> becomes <see cref="AppActivation.Route"/>.
    /// </summary>
    /// <param name="item">The shortcut item.</param>
    /// <param name="isColdStart"><see langword="true"/> when the quick action launched the app.</param>
    /// <returns><see langword="true"/> when an activation was published.</returns>
    public static bool HandleShortcutItem(UIApplicationShortcutItem item, bool isColdStart = false)
    {
        ArgumentNullException.ThrowIfNull(item);
        var (data, route) = AppleActivationMapping.ForShortcut(item.Type, NativeSdkNotificationCenterDelegate.ToStringDictionary(item.UserInfo));
        return Publish(new AppActivation
        {
            Kind = ActivationKind.Shortcut,
            Route = route,
            Data = data,
            IsColdStart = isColdStart,
            Source = AppleHost.AdapterName,
        });
    }

    private static bool Publish(AppActivation activation)
    {
        if (activation.IsColdStart)
        {
            // A notification response that arrives later is not the launch activation.
            AppleLaunchTracker.MarkColdStartConsumed();
        }

        AppleServices.PublishActivation(activation);
        return true;
    }
}
