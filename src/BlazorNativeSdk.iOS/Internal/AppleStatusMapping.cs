using AVFoundation;
using Photos;
using UserNotifications;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>Pure mappings from Apple authorization states to <see cref="PermissionStatus"/>.</summary>
internal static class AppleStatusMapping
{
    /// <summary>Maps an AVFoundation (camera, microphone) authorization status.</summary>
    public static PermissionStatus FromCapture(AVAuthorizationStatus status) => status switch
    {
        AVAuthorizationStatus.NotDetermined => PermissionStatus.NotDetermined,
        AVAuthorizationStatus.Restricted => PermissionStatus.Restricted,
        AVAuthorizationStatus.Denied => PermissionStatus.Denied,
        AVAuthorizationStatus.Authorized => PermissionStatus.Granted,
        _ => PermissionStatus.Unknown,
    };

    /// <summary>Maps a photo library authorization status. "Limited" (selected photos only) maps to <see cref="PermissionStatus.Limited"/>.</summary>
    public static PermissionStatus FromPhotos(PHAuthorizationStatus status) => status switch
    {
        PHAuthorizationStatus.NotDetermined => PermissionStatus.NotDetermined,
        PHAuthorizationStatus.Restricted => PermissionStatus.Restricted,
        PHAuthorizationStatus.Denied => PermissionStatus.Denied,
        PHAuthorizationStatus.Authorized => PermissionStatus.Granted,
        PHAuthorizationStatus.Limited => PermissionStatus.Limited,
        _ => PermissionStatus.Unknown,
    };

    /// <summary>
    /// Maps a notification authorization status. Provisional (quiet delivery to Notification Center) and ephemeral (App Clips)
    /// authorizations map to <see cref="PermissionStatus.Limited"/>.
    /// </summary>
    public static PermissionStatus FromNotifications(UNAuthorizationStatus status) => status switch
    {
        UNAuthorizationStatus.NotDetermined => PermissionStatus.NotDetermined,
        UNAuthorizationStatus.Denied => PermissionStatus.Denied,
        UNAuthorizationStatus.Authorized => PermissionStatus.Granted,
        UNAuthorizationStatus.Provisional or UNAuthorizationStatus.Ephemeral => PermissionStatus.Limited,
        _ => PermissionStatus.Unknown,
    };

    /// <summary>Maps a permission status to a capability availability.</summary>
    public static CapabilityAvailability ToAvailability(PermissionStatus status) => status switch
    {
        PermissionStatus.Granted or PermissionStatus.Limited or PermissionStatus.NotRequired => CapabilityAvailability.Available,
        PermissionStatus.NotDetermined => CapabilityAvailability.RequiresPermission,
        PermissionStatus.Denied or PermissionStatus.Restricted => CapabilityAvailability.PermissionDenied,
        _ => CapabilityAvailability.Unknown,
    };
}
