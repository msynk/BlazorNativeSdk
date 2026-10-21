using BlazorNativeSdk.Lifecycle;
using MauiConnectionProfile = Microsoft.Maui.Networking.ConnectionProfile;
using MauiNetworkAccess = Microsoft.Maui.Networking.NetworkAccess;
using NetworkAccess = BlazorNativeSdk.Lifecycle.NetworkAccess;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>Pure connectivity mapping used by <see cref="AndroidConnectivityService"/>.</summary>
internal static class AndroidConnectivityMapper
{
    // ConnectivityManager.RESTRICT_BACKGROUND_STATUS_*
    public const int RestrictBackgroundDisabled = 1;
    public const int RestrictBackgroundWhitelisted = 2;
    public const int RestrictBackgroundEnabled = 3;

    public static NetworkAccess MapAccess(MauiNetworkAccess access) => access switch
    {
        MauiNetworkAccess.None => NetworkAccess.None,
        MauiNetworkAccess.Local => NetworkAccess.Local,
        MauiNetworkAccess.ConstrainedInternet => NetworkAccess.ConstrainedInternet,
        MauiNetworkAccess.Internet => NetworkAccess.Internet,
        _ => NetworkAccess.Unknown,
    };

    public static ConnectionType MapProfile(MauiConnectionProfile profile) => profile switch
    {
        MauiConnectionProfile.WiFi => ConnectionType.WiFi,
        MauiConnectionProfile.Cellular => ConnectionType.Cellular,
        MauiConnectionProfile.Ethernet => ConnectionType.Ethernet,
        MauiConnectionProfile.Bluetooth => ConnectionType.Bluetooth,
        _ => ConnectionType.Other,
    };

    /// <summary>
    /// Data Saver is reported as enabled only when it restricts this app: when the user allowed the app unrestricted data
    /// (<c>RESTRICT_BACKGROUND_STATUS_WHITELISTED</c>), the app has no reason to reduce usage.
    /// </summary>
    public static bool? MapDataSaver(int restrictBackgroundStatus) => restrictBackgroundStatus switch
    {
        RestrictBackgroundEnabled => true,
        RestrictBackgroundDisabled or RestrictBackgroundWhitelisted => false,
        _ => null,
    };

    public static bool HasChanged(ConnectivitySnapshot previous, ConnectivitySnapshot current) =>
        previous.Access != current.Access ||
        previous.IsMetered != current.IsMetered ||
        previous.IsDataSaverEnabled != current.IsDataSaverEnabled ||
        !previous.ConnectionTypes.SequenceEqual(current.ConnectionTypes);
}
