namespace BlazorNativeSdk;

/// <summary>The state of a runtime permission.</summary>
public enum PermissionStatus
{
    /// <summary>The state could not be determined.</summary>
    Unknown = 0,

    /// <summary>The user has not been asked yet; requesting will show a prompt.</summary>
    NotDetermined,

    /// <summary>The permission is granted.</summary>
    Granted,

    /// <summary>The permission is partially granted (for example limited photo library access on iOS, or approximate location).</summary>
    Limited,

    /// <summary>The user denied the permission. On some platforms requesting again will not show a prompt; use <see cref="IPermissionService.OpenAppSettingsAsync"/>.</summary>
    Denied,

    /// <summary>The permission is restricted by policy and cannot be changed by the user.</summary>
    Restricted,

    /// <summary>The platform does not gate this operation behind a runtime permission.</summary>
    NotRequired,
}

/// <summary>Identifies a runtime permission. Well-known values are exposed by <see cref="NativePermissions"/>.</summary>
/// <param name="Name">The permission name.</param>
public readonly record struct NativePermission(string Name)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>Well-known permissions. Adapters map these to platform permissions.</summary>
public static class NativePermissions
{
    /// <summary>Camera access.</summary>
    public static readonly NativePermission Camera = new("camera");

    /// <summary>Microphone access (needed to record video with sound).</summary>
    public static readonly NativePermission Microphone = new("microphone");

    /// <summary>Read access to the photo library.</summary>
    public static readonly NativePermission Photos = new("photos");

    /// <summary>Permission to add items to the photo library.</summary>
    public static readonly NativePermission PhotosAddOnly = new("photos-add-only");

    /// <summary>Posting notifications.</summary>
    public static readonly NativePermission Notifications = new("notifications");

    /// <summary>Biometric authentication (Face ID usage on iOS).</summary>
    public static readonly NativePermission Biometrics = new("biometrics");

    /// <summary>Reading the clipboard (browsers).</summary>
    public static readonly NativePermission ClipboardRead = new("clipboard-read");

    /// <summary>Legacy shared storage read access (Android 12 and earlier).</summary>
    public static readonly NativePermission StorageRead = new("storage-read");

    /// <summary>Legacy shared storage write access (Android 9 and earlier).</summary>
    public static readonly NativePermission StorageWrite = new("storage-write");

    /// <summary>Scheduling exact alarms (Android 12+), used for precisely timed notifications.</summary>
    public static readonly NativePermission ExactAlarms = new("exact-alarms");
}

/// <summary>Checks and requests runtime permissions.</summary>
public interface IPermissionService : INativeCapability
{
    /// <summary>Checks a permission without prompting.</summary>
    /// <returns>The status, or <see cref="NativeResultStatus.Unsupported"/> when the platform has no such permission.</returns>
    ValueTask<NativeResult<PermissionStatus>> CheckAsync(NativePermission permission, CancellationToken cancellationToken = default);

    /// <summary>Requests a permission, prompting the user when the platform allows it.</summary>
    /// <remarks>Browsers require this to be called from a user gesture (click handler) for most permissions.</remarks>
    ValueTask<NativeResult<PermissionStatus>> RequestAsync(NativePermission permission, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns whether the app should explain why it needs the permission before requesting it again
    /// (Android's <c>shouldShowRequestPermissionRationale</c>). Always <see langword="false"/> on other platforms.
    /// </summary>
    ValueTask<NativeResult<bool>> ShouldShowRationaleAsync(NativePermission permission, CancellationToken cancellationToken = default);

    /// <summary>Opens the app's page in the system settings so the user can change permissions.</summary>
    ValueTask<NativeResult> OpenAppSettingsAsync(CancellationToken cancellationToken = default);
}
