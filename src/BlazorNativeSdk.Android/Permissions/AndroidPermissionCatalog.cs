namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>How a <see cref="NativePermission"/> is enforced on a given Android API level.</summary>
internal enum AndroidPermissionKind
{
    /// <summary>Android has no such permission.</summary>
    Unsupported,

    /// <summary>The operation is not gated by a runtime permission on this API level.</summary>
    NotRequired,

    /// <summary>One or more dangerous (runtime) permissions.</summary>
    Runtime,

    /// <summary>Notifications below Android 13: no permission, but the user can disable notifications in settings.</summary>
    LegacyNotifications,

    /// <summary>Exact alarms on Android 12+: a special app-op granted in system settings.</summary>
    ExactAlarms,
}

/// <summary>The Android enforcement of a permission.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="AndroidPermissions">The runtime permissions that must all be granted (for <see cref="AndroidPermissionKind.Runtime"/>).</param>
/// <param name="PartialAccessPermission">
/// A permission whose grant means partial access (<see cref="PermissionStatus.Limited"/>), such as
/// <c>READ_MEDIA_VISUAL_USER_SELECTED</c> on Android 14+. Requested together with the main permissions when declared.
/// </param>
internal sealed record AndroidPermissionPlan(AndroidPermissionKind Kind, IReadOnlyList<string> AndroidPermissions, string? PartialAccessPermission = null)
{
    public static AndroidPermissionPlan Unsupported { get; } = new(AndroidPermissionKind.Unsupported, []);

    public static AndroidPermissionPlan NotRequired { get; } = new(AndroidPermissionKind.NotRequired, []);

    public static AndroidPermissionPlan Runtime(params string[] permissions) => new(AndroidPermissionKind.Runtime, permissions);
}

/// <summary>
/// Maps <see cref="NativePermissions"/> to Android permissions per API level. Pure: depends only on the API level, so it can
/// be unit tested without a device.
/// </summary>
internal static class AndroidPermissionCatalog
{
    public const string Camera = "android.permission.CAMERA";
    public const string RecordAudio = "android.permission.RECORD_AUDIO";
    public const string ReadMediaImages = "android.permission.READ_MEDIA_IMAGES";
    public const string ReadMediaVisualUserSelected = "android.permission.READ_MEDIA_VISUAL_USER_SELECTED";
    public const string ReadExternalStorage = "android.permission.READ_EXTERNAL_STORAGE";
    public const string WriteExternalStorage = "android.permission.WRITE_EXTERNAL_STORAGE";
    public const string PostNotifications = "android.permission.POST_NOTIFICATIONS";
    public const string ScheduleExactAlarm = "android.permission.SCHEDULE_EXACT_ALARM";
    public const string UseExactAlarm = "android.permission.USE_EXACT_ALARM";
    public const string AccessNetworkState = "android.permission.ACCESS_NETWORK_STATE";

    private const string AndroidPermissionPrefix = "android.permission.";

    /// <summary>Resolves the plan for <paramref name="permission"/> on <paramref name="apiLevel"/>.</summary>
    /// <remarks>
    /// Besides the well-known <see cref="NativePermissions"/>, any name starting with <c>android.permission.</c> is treated
    /// as a single runtime permission, so apps can check and request permissions this SDK does not model (for example
    /// <c>android.permission.ACCESS_FINE_LOCATION</c>).
    /// </remarks>
    public static AndroidPermissionPlan Resolve(NativePermission permission, int apiLevel)
    {
        var name = permission.Name ?? string.Empty;
        if (name.StartsWith(AndroidPermissionPrefix, StringComparison.Ordinal) && name.Length > AndroidPermissionPrefix.Length)
        {
            return AndroidPermissionPlan.Runtime(name);
        }

        return name.ToLowerInvariant() switch
        {
            "camera" => AndroidPermissionPlan.Runtime(Camera),
            "microphone" => AndroidPermissionPlan.Runtime(RecordAudio),
            "photos" => apiLevel >= 33
                ? new AndroidPermissionPlan(AndroidPermissionKind.Runtime, [ReadMediaImages], apiLevel >= 34 ? ReadMediaVisualUserSelected : null)
                : AndroidPermissionPlan.Runtime(ReadExternalStorage),
            "photos-add-only" => apiLevel >= 29 ? AndroidPermissionPlan.NotRequired : AndroidPermissionPlan.Runtime(WriteExternalStorage),
            "notifications" => apiLevel >= 33
                ? AndroidPermissionPlan.Runtime(PostNotifications)
                : new AndroidPermissionPlan(AndroidPermissionKind.LegacyNotifications, []),
            "storage-read" => apiLevel >= 33 ? AndroidPermissionPlan.NotRequired : AndroidPermissionPlan.Runtime(ReadExternalStorage),
            "storage-write" => apiLevel >= 29 ? AndroidPermissionPlan.NotRequired : AndroidPermissionPlan.Runtime(WriteExternalStorage),
            "exact-alarms" => apiLevel >= 31
                ? new AndroidPermissionPlan(AndroidPermissionKind.ExactAlarms, [ScheduleExactAlarm, UseExactAlarm])
                : AndroidPermissionPlan.NotRequired,
            "biometrics" or "clipboard-read" => AndroidPermissionPlan.NotRequired,
            _ => AndroidPermissionPlan.Unsupported,
        };
    }
}

/// <summary>Maps Microsoft.Maui.Essentials permission states to <see cref="PermissionStatus"/>. Pure.</summary>
internal static class AndroidPermissionMapper
{
    /// <summary>Maps an Essentials status.</summary>
    public static PermissionStatus Map(Microsoft.Maui.ApplicationModel.PermissionStatus status) => status switch
    {
        Microsoft.Maui.ApplicationModel.PermissionStatus.Granted => PermissionStatus.Granted,
        Microsoft.Maui.ApplicationModel.PermissionStatus.Denied => PermissionStatus.Denied,
        Microsoft.Maui.ApplicationModel.PermissionStatus.Disabled => PermissionStatus.Restricted,
        Microsoft.Maui.ApplicationModel.PermissionStatus.Restricted => PermissionStatus.Restricted,
        Microsoft.Maui.ApplicationModel.PermissionStatus.Limited => PermissionStatus.Limited,
        _ => PermissionStatus.Unknown,
    };

    /// <summary>
    /// Android reports "denied" both before the first request and after a denial. A denied permission that was never
    /// requested by this app and for which Android does not suggest a rationale has not been decided yet.
    /// </summary>
    public static PermissionStatus Refine(PermissionStatus status, bool requestedBefore, bool shouldShowRationale) =>
        status == PermissionStatus.Denied && !requestedBefore && !shouldShowRationale ? PermissionStatus.NotDetermined : status;

    /// <summary>
    /// Notifications below Android 13 have no runtime permission: <see cref="PermissionStatus.NotRequired"/>, unless the user
    /// disabled the app's notifications in settings (<see cref="PermissionStatus.Denied"/>; no prompt can re-enable them).
    /// </summary>
    public static PermissionStatus MapLegacyNotifications(bool notificationsEnabled) =>
        notificationsEnabled ? PermissionStatus.NotRequired : PermissionStatus.Denied;

    /// <summary>
    /// Exact alarms on Android 12+ (<c>AlarmManager.canScheduleExactAlarms()</c>): an app-op the user grants in system
    /// settings, so it is undecided until the app asked for it once.
    /// </summary>
    public static PermissionStatus MapExactAlarms(bool canScheduleExactAlarms, bool requestedBefore) =>
        canScheduleExactAlarms ? PermissionStatus.Granted : requestedBefore ? PermissionStatus.Denied : PermissionStatus.NotDetermined;
}
