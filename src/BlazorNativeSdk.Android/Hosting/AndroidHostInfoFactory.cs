using System.Globalization;
using Android.Content.Res;
using Android.OS;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>Builds <see cref="NativeHostInfo"/> for Android.</summary>
internal static class AndroidHostInfoFactory
{
    // Configuration.UI_MODE_TYPE_*
    private const int UiModeTypeTelevision = 4;
    private const int UiModeTypeWatch = 6;

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
            FormFactor = MapFormFactor(uiModeType, smallestWidth),
            OperatingSystemVersion = ParseVersion(release),
            Description = string.Create(CultureInfo.InvariantCulture, $"{host} on Android {release} (API {apiLevel})"),
        };
    }

    /// <summary>Maps the UI mode type and smallest screen width (dp) to a form factor. Pure.</summary>
    public static DeviceFormFactor MapFormFactor(int uiModeType, int smallestScreenWidthDp) => uiModeType switch
    {
        UiModeTypeTelevision => DeviceFormFactor.TV,
        UiModeTypeWatch => DeviceFormFactor.Watch,
        _ => smallestScreenWidthDp >= 600 ? DeviceFormFactor.Tablet : DeviceFormFactor.Phone,
    };

    /// <summary>Parses <c>Build.VERSION.RELEASE</c> ("14", "8.1.0"); codenames of preview builds yield <see langword="null"/>. Pure.</summary>
    public static Version? ParseVersion(string? release)
    {
        if (string.IsNullOrWhiteSpace(release))
        {
            return null;
        }

        var text = release.Contains('.', StringComparison.Ordinal) ? release : release + ".0";
        return Version.TryParse(text, out var version) ? version : null;
    }
}
