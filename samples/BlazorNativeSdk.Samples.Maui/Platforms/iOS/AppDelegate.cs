using Foundation;

namespace BlazorNativeSdk.Samples.Maui;

/// <summary>
/// The app delegate. The MAUI integration initializes the SDK in willFinishLaunching (BGTaskScheduler handlers, notification
/// delegate) and routes URLs, Universal Links and quick actions, so no code is needed here.
/// </summary>
[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    /// <inheritdoc />
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
