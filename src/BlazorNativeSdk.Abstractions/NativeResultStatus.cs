namespace BlazorNativeSdk;

/// <summary>
/// Describes the outcome of a native operation. Every BlazorNativeSdk operation reports one of these
/// values instead of hiding platform differences behind exceptions or silent no-ops.
/// </summary>
public enum NativeResultStatus
{
    /// <summary>The operation completed successfully.</summary>
    Success = 0,

    /// <summary>
    /// The current platform or host cannot perform this operation at all (for example biometrics in a browser,
    /// or a tray icon on a phone). Retrying will not help; the UI should hide or replace the feature.
    /// </summary>
    Unsupported = 1,

    /// <summary>
    /// The user or the operating system denied a permission that the operation requires.
    /// Inspect <see cref="NativeError.PermissionStatus"/> to decide whether to ask again or direct the user to settings.
    /// </summary>
    PermissionDenied = 2,

    /// <summary>The user dismissed the native UI (picker, scanner, share sheet, biometric prompt).</summary>
    Cancelled = 3,

    /// <summary>
    /// The platform supports the operation, but it cannot run right now: hardware is missing, a feature is
    /// not configured (for example no enrolled fingerprint), the page is not in a secure context, or a user
    /// gesture is required. The <see cref="NativeError.Code"/> explains the condition.
    /// </summary>
    Unavailable = 4,

    /// <summary>The operation was attempted and failed unexpectedly.</summary>
    Failed = 5,
}
