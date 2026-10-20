using Android.App;
using Android.Content;
using Android.Content.PM;

namespace BlazorNativeSdk.Samples.Maui;

/// <summary>
/// The launcher activity. <c>SingleTop</c> delivers links to the running activity through <c>OnNewIntent</c>; the MAUI
/// integration forwards both the launch intent and new intents to the SDK, so no code is needed here.
/// </summary>
[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout |
                           ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
// Custom scheme: bnssample://orders/42 -> /orders/42
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "bnssample")]
// Verified App Links: https://samples.blazornativesdk.dev/orders/42 -> /orders/42
// (requires https://samples.blazornativesdk.dev/.well-known/assetlinks.json with the signing certificate's SHA-256).
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "https",
    DataHost = "samples.blazornativesdk.dev",
    AutoVerify = true)]
public class MainActivity : MauiAppCompatActivity
{
}
