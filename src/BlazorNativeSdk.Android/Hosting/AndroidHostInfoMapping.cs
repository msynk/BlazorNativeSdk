namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>Pure mapping of Android configuration values to <see cref="NativeHostInfo"/> fields.</summary>
internal static class AndroidHostInfoMapping
{
    // Configuration.UI_MODE_TYPE_*
    private const int UiModeTypeTelevision = 4;
    private const int UiModeTypeWatch = 6;

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
