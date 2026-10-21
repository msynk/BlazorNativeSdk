using AVFoundation;
using Photos;
using UserNotifications;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>Maps Apple authorization states to <see cref="PermissionStatus"/> (see <see cref="AppleAuthorizationMapping"/>).</summary>
internal static class AppleStatusMapping
{
    /// <summary>Maps an AVFoundation (camera, microphone) authorization status.</summary>
    public static PermissionStatus FromCapture(AVAuthorizationStatus status) => AppleAuthorizationMapping.FromCapture((long)status);

    /// <summary>Maps a photo library authorization status. "Limited" (selected photos only) maps to <see cref="PermissionStatus.Limited"/>.</summary>
    public static PermissionStatus FromPhotos(PHAuthorizationStatus status) => AppleAuthorizationMapping.FromPhotos((long)status);

    /// <summary>
    /// Maps a notification authorization status. Provisional (quiet delivery to Notification Center) and ephemeral (App Clips)
    /// authorizations map to <see cref="PermissionStatus.Limited"/>.
    /// </summary>
    public static PermissionStatus FromNotifications(UNAuthorizationStatus status) => AppleAuthorizationMapping.FromNotifications((long)status);
}
