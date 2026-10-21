namespace BlazorNativeSdk.ApplePlatform;

/// <summary>
/// Maps raw Apple authorization status values to <see cref="PermissionStatus"/>. Pure: the platform constants are duplicated
/// as literals so the mapping can be unit tested without the Apple bindings; <see cref="AppleStatusMapping"/> passes the
/// typed values in.
/// </summary>
internal static class AppleAuthorizationMapping
{
    // AVAuthorizationStatus (AVFoundation) and PHAuthorizationStatus (Photos) share NotDetermined..Authorized.
    private const long NotDetermined = 0;         // AVAuthorizationStatusNotDetermined, PHAuthorizationStatusNotDetermined
    private const long Restricted = 1;            // AVAuthorizationStatusRestricted, PHAuthorizationStatusRestricted
    private const long Denied = 2;                // AVAuthorizationStatusDenied, PHAuthorizationStatusDenied
    private const long Authorized = 3;            // AVAuthorizationStatusAuthorized, PHAuthorizationStatusAuthorized
    private const long PhotosLimited = 4;         // PHAuthorizationStatusLimited

    // UNAuthorizationStatus (UserNotifications).
    private const long NotificationsNotDetermined = 0;  // UNAuthorizationStatusNotDetermined
    private const long NotificationsDenied = 1;         // UNAuthorizationStatusDenied
    private const long NotificationsAuthorized = 2;     // UNAuthorizationStatusAuthorized
    private const long NotificationsProvisional = 3;    // UNAuthorizationStatusProvisional
    private const long NotificationsEphemeral = 4;      // UNAuthorizationStatusEphemeral

    /// <summary>Maps an <c>AVAuthorizationStatus</c> (camera, microphone).</summary>
    public static PermissionStatus FromCapture(long status) => status switch
    {
        NotDetermined => PermissionStatus.NotDetermined,
        Restricted => PermissionStatus.Restricted,
        Denied => PermissionStatus.Denied,
        Authorized => PermissionStatus.Granted,
        _ => PermissionStatus.Unknown,
    };

    /// <summary>Maps a <c>PHAuthorizationStatus</c>. "Limited" (selected photos only) maps to <see cref="PermissionStatus.Limited"/>.</summary>
    public static PermissionStatus FromPhotos(long status) => status switch
    {
        NotDetermined => PermissionStatus.NotDetermined,
        Restricted => PermissionStatus.Restricted,
        Denied => PermissionStatus.Denied,
        Authorized => PermissionStatus.Granted,
        PhotosLimited => PermissionStatus.Limited,
        _ => PermissionStatus.Unknown,
    };

    /// <summary>
    /// Maps a <c>UNAuthorizationStatus</c>. Provisional (quiet delivery to Notification Center) and ephemeral (App Clips)
    /// authorizations map to <see cref="PermissionStatus.Limited"/>.
    /// </summary>
    public static PermissionStatus FromNotifications(long status) => status switch
    {
        NotificationsNotDetermined => PermissionStatus.NotDetermined,
        NotificationsDenied => PermissionStatus.Denied,
        NotificationsAuthorized => PermissionStatus.Granted,
        NotificationsProvisional or NotificationsEphemeral => PermissionStatus.Limited,
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
