using BlazorNativeSdk.ApplePlatform;
using Foundation;
using UIKit;

namespace BlazorNativeSdk.Samples.Maui;

/// <summary>
/// The app delegate. The MAUI integration initializes the SDK in willFinishLaunching (BGTaskScheduler handlers, notification
/// delegate) and routes URLs, Universal Links and quick actions. MAUI has no lifecycle event for background URL sessions, so
/// that callback is forwarded here.
/// </summary>
[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    /// <inheritdoc />
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    /// <summary>iOS relaunched the app to deliver finished background transfers.</summary>
    [Export("application:handleEventsForBackgroundURLSession:completionHandler:")]
    public void HandleEventsForBackgroundUrl(UIApplication application, string sessionIdentifier, Action completionHandler)
    {
        if (!AppleBackgroundTransfers.HandleEventsForBackgroundUrl(sessionIdentifier, completionHandler))
        {
            completionHandler();
        }
    }
}
