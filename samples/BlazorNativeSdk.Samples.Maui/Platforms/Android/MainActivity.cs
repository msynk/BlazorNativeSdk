using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.OS;
using AndroidX.Core.View;

namespace BlazorNativeSdk.Samples.Maui;

/// <summary>
/// The launcher activity. <c>SingleTop</c> delivers links to the running activity through <c>OnNewIntent</c>; the MAUI
/// integration forwards both the launch intent and new intents to the SDK, so no activation code is needed here.
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
    /// <inheritdoc />
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        UpdateSystemBarIcons();
    }

    /// <inheritdoc />
    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        UpdateSystemBarIcons(); // UiMode is handled here (ConfigurationChanges), so a theme switch does not recreate the activity.
    }

    // Android 15+ draws edge to edge and ignores the theme's status bar color: the bars are transparent over the page's light
    // or dark background (MainPage), so their icons must be dark in the light theme to stay visible.
    private void UpdateSystemBarIcons()
    {
        if (Window is not { } window || WindowCompat.GetInsetsController(window, window.DecorView) is not { } insets)
        {
            return;
        }

        var light = (Resources?.Configuration?.UiMode & UiMode.NightMask) != UiMode.NightYes;
        insets.AppearanceLightStatusBars = light;
        insets.AppearanceLightNavigationBars = light;
    }
}
