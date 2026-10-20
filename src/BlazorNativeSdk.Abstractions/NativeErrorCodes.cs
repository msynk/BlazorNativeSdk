namespace BlazorNativeSdk;

/// <summary>
/// Well-known, stable error codes carried by <see cref="NativeError.Code"/>. Codes are lowercase,
/// dot-separated strings so that adapters can add their own codes without colliding with these.
/// </summary>
public static class NativeErrorCodes
{
    /// <summary>No adapter for the capability is registered in the current host.</summary>
    public const string NoAdapter = "adapter.missing";

    /// <summary>The platform does not support the capability.</summary>
    public const string NotSupported = "platform.not-supported";

    /// <summary>The capability needs a newer operating system or runtime version.</summary>
    public const string OsVersionTooLow = "platform.os-version";

    /// <summary>The adapter is registered but the host is not ready yet (for example no window or activity is available).</summary>
    public const string HostNotReady = "host.not-ready";

    /// <summary>The browser API requires a secure context (HTTPS or localhost).</summary>
    public const string InsecureContext = "browser.insecure-context";

    /// <summary>The browser API requires transient user activation (call from a click handler).</summary>
    public const string UserGestureRequired = "browser.user-gesture-required";

    /// <summary>The required hardware (camera, biometric sensor, ...) is not present.</summary>
    public const string HardwareUnavailable = "hardware.unavailable";

    /// <summary>The permission was denied.</summary>
    public const string PermissionDenied = "permission.denied";

    /// <summary>The permission is restricted by policy (parental controls, MDM) and cannot be granted by the user.</summary>
    public const string PermissionRestricted = "permission.restricted";

    /// <summary>The app is missing a manifest/Info.plist declaration required by the operation.</summary>
    public const string MissingDeclaration = "app.missing-declaration";

    /// <summary>The user cancelled the operation.</summary>
    public const string Cancelled = "user.cancelled";

    /// <summary>The operation timed out.</summary>
    public const string Timeout = "operation.timeout";

    /// <summary>An argument supplied to the operation was invalid for this platform.</summary>
    public const string InvalidArgument = "operation.invalid-argument";

    /// <summary>The operation failed with an unexpected platform error.</summary>
    public const string PlatformError = "platform.error";

    /// <summary>A resource (file, item, registration) was not found.</summary>
    public const string NotFound = "operation.not-found";

    /// <summary>The operation conflicts with existing state (for example a global shortcut already registered by another app).</summary>
    public const string Conflict = "operation.conflict";

    /// <summary>The payload exceeded a configured or platform size limit.</summary>
    public const string TooLarge = "operation.too-large";

    /// <summary>No biometric or device credential is enrolled.</summary>
    public const string BiometricNotEnrolled = "biometric.not-enrolled";

    /// <summary>Biometric authentication is locked out after too many failed attempts.</summary>
    public const string BiometricLockedOut = "biometric.locked-out";

    /// <summary>The user was not recognized.</summary>
    public const string BiometricAuthenticationFailed = "biometric.authentication-failed";

    /// <summary>The user chose the fallback action (for example "Enter password") instead of authenticating.</summary>
    public const string BiometricFallbackRequested = "biometric.fallback-requested";

    /// <summary>
    /// Protected data cannot be read right now, for example because the device is locked and the item is only accessible
    /// while unlocked (iOS data protection, Android credential-encrypted storage before first unlock).
    /// </summary>
    public const string ProtectedDataUnavailable = "storage.protected-data-unavailable";

    /// <summary>The network is not available.</summary>
    public const string Offline = "network.offline";

    /// <summary>The background execution request was refused or the budget is exhausted.</summary>
    public const string BackgroundExecutionDenied = "background.denied";

    /// <summary>The result has not been initialized (a <c>default</c> struct value was used).</summary>
    public const string Uninitialized = "result.uninitialized";
}
