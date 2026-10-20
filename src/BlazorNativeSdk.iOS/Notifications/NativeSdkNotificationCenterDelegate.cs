using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UserNotifications;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>
/// The <c>UNUserNotificationCenter</c> delegate installed by <see cref="AppleNativeSdk.Initialize()"/>. It shows notifications
/// while the app is in the foreground and turns taps and action selections into <see cref="AppActivation"/>s
/// (<see cref="ActivationKind.Notification"/>) that the SDK routes once the UI is ready.
/// </summary>
/// <remarks>
/// iOS delivers the tap that launched the app only to a delegate that was set before <c>FinishedLaunching</c> returned, so call
/// <see cref="AppleNativeSdk.Initialize()"/> from <c>AppDelegate.FinishedLaunching</c> (or the MAUI <c>FinishedLaunching</c>
/// lifecycle event). Apps that need their own delegate can derive from this class, or call
/// <see cref="TryPublishActivation"/> from their delegate's <c>DidReceiveNotificationResponse</c>.
/// </remarks>
public class NativeSdkNotificationCenterDelegate : UNUserNotificationCenterDelegate
{
    private static readonly UNNotificationPresentationOptions s_defaultOptions =
        UNNotificationPresentationOptions.Banner | UNNotificationPresentationOptions.List |
        UNNotificationPresentationOptions.Sound | UNNotificationPresentationOptions.Badge;

    /// <summary>
    /// Overrides <see cref="AppleNativeOptions.ForegroundPresentationOptions"/> for this delegate. <see langword="null"/> uses the
    /// configured options, or banner, list, sound and badge before the service provider is available.
    /// </summary>
    public UNNotificationPresentationOptions? ForegroundPresentationOptions { get; set; }

    /// <inheritdoc />
    public override void WillPresentNotification(UNUserNotificationCenter center, UNNotification notification, Action<UNNotificationPresentationOptions> completionHandler)
    {
        ArgumentNullException.ThrowIfNull(completionHandler);
        completionHandler(GetForegroundPresentationOptions(notification));
    }

    /// <inheritdoc />
    public override void DidReceiveNotificationResponse(UNUserNotificationCenter center, UNNotificationResponse response, Action completionHandler)
    {
        ArgumentNullException.ThrowIfNull(completionHandler);
        try
        {
            TryPublishActivation(response);
        }
        finally
        {
            completionHandler();
        }
    }

    /// <summary>
    /// Publishes the activation for a notification response. Dismissals are ignored. Safe to call before the service provider
    /// is available: the activation is buffered.
    /// </summary>
    /// <returns><see langword="true"/> when an activation was published.</returns>
    public static bool TryPublishActivation(UNNotificationResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.IsDismissAction)
        {
            return false;
        }

        var request = response.Notification.Request;
        var decoded = AppleNotificationMapping.Decode(ToStringDictionary(request.Content.UserInfo), request.Identifier);
        var activation = new AppActivation
        {
            Kind = ActivationKind.Notification,
            NotificationId = decoded.Id,
            Route = decoded.Route,
            Data = decoded.Data,
            ActionId = response.IsDefaultAction ? null : response.ActionIdentifier.ToString(),
            UserText = (response as UNTextInputNotificationResponse)?.UserText,
            IsColdStart = AppleLaunchTracker.TryConsumeColdStart(),

            // Actions without the Foreground option are delivered while the app stays in the background (possibly in a
            // process launched just for this response); they are dispatched without waiting for UI and never navigated.
            IsBackground = !response.IsDefaultAction && IsApplicationInBackground(),
            Source = AppleHost.AdapterName,
        };
        AppleServices.PublishActivation(activation);
        return true;
    }

    private static bool IsApplicationInBackground()
    {
        try
        {
            return UIKit.UIApplication.SharedApplication.ApplicationState == UIKit.UIApplicationState.Background;
        }
        catch (UIKit.UIKitThreadAccessException)
        {
            return false;
        }
    }

    /// <summary>Returns the presentation options for a notification delivered while the app is in the foreground.</summary>
    protected virtual UNNotificationPresentationOptions GetForegroundPresentationOptions(UNNotification notification) =>
        ForegroundPresentationOptions
        ?? AppleServices.Current?.GetService<IOptions<AppleNativeOptions>>()?.Value.ForegroundPresentationOptions
        ?? s_defaultOptions;

    /// <summary>Converts the string-like entries (strings and numbers) of a userInfo dictionary.</summary>
    internal static Dictionary<string, string> ToStringDictionary(NSDictionary? userInfo)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (userInfo is null)
        {
            return result;
        }

        foreach (var (key, value) in userInfo)
        {
            if (key is not NSString name)
            {
                continue;
            }

            var text = value switch
            {
                NSString s => s.ToString(),
                NSNumber n => n.StringValue,
                NSUrl u => u.AbsoluteString,
                _ => null,
            };
            if (text is not null)
            {
                result[name.ToString()] = text;
            }
        }

        return result;
    }
}
