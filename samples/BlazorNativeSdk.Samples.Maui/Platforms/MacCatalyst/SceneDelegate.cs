using Foundation;

namespace BlazorNativeSdk.Samples.Maui;

/// <summary>
/// The scene delegate named in Info.plist (UIApplicationSceneManifest). Multiple scenes let IWindowManager open additional
/// windows; the MAUI integration routes URLs and activities delivered to scenes.
/// </summary>
[Register("SceneDelegate")]
public class SceneDelegate : MauiUISceneDelegate
{
}
