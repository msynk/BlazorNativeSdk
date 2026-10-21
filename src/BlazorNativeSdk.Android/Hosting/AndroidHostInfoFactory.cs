using System.Globalization;
using Android.Content.Res;
using Android.OS;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>Builds <see cref="NativeHostInfo"/> for Android.</summary>
internal static class AndroidHostInfoFactory
{
    public static NativeHostInfo Create(NativeHostKind host)
    {
        var configuration = AndroidContext.App.Resources?.Configuration;
        var uiModeType = configuration is null ? 0 : (int)(configuration.UiMode & UiMode.TypeMask);
        var smallestWidth = configuration?.SmallestScreenWidthDp ?? 0;
        var release = Build.VERSION.Release;
        var apiLevel = (int)Build.VERSION.SdkInt;
        return new NativeHostInfo
        {
            Platform = NativePlatform.Android,
            Host = host,
            FormFactor = AndroidHostInfoMapping.MapFormFactor(uiModeType, smallestWidth),
            OperatingSystemVersion = AndroidHostInfoMapping.ParseVersion(release),
            Description = string.Create(CultureInfo.InvariantCulture, $"{host} on Android {release} (API {apiLevel})"),
        };
    }
}
