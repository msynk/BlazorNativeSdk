using System.Globalization;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>
/// Maps LocalAuthentication (<c>LAError</c>) codes to <see cref="NativeError"/> values. Pure: the <c>LAError</c> codes are
/// duplicated as literals so the mapping can be unit tested without the Apple bindings.
/// </summary>
internal static class AppleBiometricErrorMapper
{
    /// <summary>The LocalAuthentication error domain.</summary>
    public const string ErrorDomain = "com.apple.LocalAuthentication";

    // LAError codes (LAStatus in the .NET bindings). Codes that only exist on macOS or are reused across policies are listed
    // too, so the mapping stays valid whatever the binding exposes for the current platform.
    private const long AuthenticationFailed = -1;    // LAErrorAuthenticationFailed
    private const long UserCancel = -2;              // LAErrorUserCancel
    private const long UserFallback = -3;            // LAErrorUserFallback
    private const long SystemCancel = -4;            // LAErrorSystemCancel
    private const long PasscodeNotSet = -5;          // LAErrorPasscodeNotSet
    private const long BiometryNotAvailable = -6;    // LAErrorBiometryNotAvailable (formerly TouchIDNotAvailable)
    private const long BiometryNotEnrolled = -7;     // LAErrorBiometryNotEnrolled
    private const long BiometryLockout = -8;         // LAErrorBiometryLockout
    private const long AppCancel = -9;               // LAErrorAppCancel
    private const long CompanionNotAvailable = -11;  // LAErrorCompanionNotAvailable (formerly WatchNotAvailable, macOS)
    private const long BiometryNotPaired = -12;      // LAErrorBiometryNotPaired (macOS)
    private const long BiometryDisconnected = -13;   // LAErrorBiometryDisconnected (macOS)
    private const long NotInteractive = -1004;       // LAErrorNotInteractive

    /// <summary>Maps an <c>LAError</c> code.</summary>
    /// <param name="code">The error code (<c>LAStatus</c> value).</param>
    /// <param name="isFaceIdUsageDenied">
    /// <see langword="true"/> when the device has Face ID but the user denied this app the use of it, which LocalAuthentication
    /// reports as <c>LAErrorBiometryNotAvailable</c>.
    /// </param>
    /// <param name="description">The localized platform description (diagnostics only).</param>
    public static NativeError Map(long code, bool isFaceIdUsageDenied, string? description = null)
    {
        var capability = NativeCapabilities.Biometrics;
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["domain"] = ErrorDomain,
            ["code"] = code.ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrEmpty(description))
        {
            details["description"] = description;
        }

        NativeError error = code switch
        {
            UserCancel or SystemCancel or AppCancel =>
                NativeError.Cancelled(capability, code == UserCancel
                    ? "The user cancelled authentication."
                    : "Authentication was cancelled by the system or the app (for example the app moved to the background)."),
            UserFallback =>
                NativeError.Failed(capability, NativeErrorCodes.BiometricFallbackRequested,
                    "The user chose the fallback button. Offer an alternative sign-in method, or set AllowDeviceCredential to let the system ask for the passcode."),
            BiometryNotEnrolled =>
                NativeError.Unavailable(capability, NativeErrorCodes.BiometricNotEnrolled, "No Face ID, Touch ID or Optic ID identity is enrolled."),
            PasscodeNotSet =>
                NativeError.Unavailable(capability, NativeErrorCodes.BiometricNotEnrolled, "No device passcode is set, so neither biometrics nor the device credential can be used."),
            BiometryLockout =>
                NativeError.Unavailable(capability, NativeErrorCodes.BiometricLockedOut,
                    "Biometry is locked after too many failed attempts. The user must enter the device passcode (AllowDeviceCredential) to unlock it."),
            BiometryNotAvailable when isFaceIdUsageDenied =>
                NativeError.PermissionDenied(capability, NativePermissions.Biometrics, PermissionStatus.Denied,
                    "The user denied this app the use of Face ID. Direct the user to the app's page in Settings."),
            BiometryNotAvailable or BiometryNotPaired or BiometryDisconnected or CompanionNotAvailable =>
                NativeError.Unavailable(capability, NativeErrorCodes.HardwareUnavailable, "Biometric hardware is not available on this device."),
            AuthenticationFailed =>
                NativeError.Failed(capability, NativeErrorCodes.BiometricAuthenticationFailed, "The user could not be verified."),
            NotInteractive =>
                NativeError.Unavailable(capability, NativeErrorCodes.HostNotReady, "Authentication needs to show UI, but the app cannot present UI right now."),
            _ => NativeError.Failed(capability, NativeErrorCodes.PlatformError, description ?? $"LocalAuthentication failed with code {code}."),
        };

        return error with { Details = details };
    }

    /// <summary>
    /// Maps an <c>NSError</c> reported by LocalAuthentication. Errors from another domain are platform errors.
    /// </summary>
    /// <param name="domain">The error domain.</param>
    /// <param name="code">The error code.</param>
    /// <param name="isFaceIdUsageDenied">See <see cref="Map(long, bool, string?)"/>.</param>
    /// <param name="description">The localized platform description.</param>
    public static NativeError Map(string domain, long code, bool isFaceIdUsageDenied, string? description) =>
        string.Equals(domain, ErrorDomain, StringComparison.Ordinal)
            ? Map(code, isFaceIdUsageDenied, description)
            : NativeError.Failed(NativeCapabilities.Biometrics, NativeErrorCodes.PlatformError, description ?? string.Empty) with
            {
                Details = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["domain"] = domain,
                    ["code"] = code.ToString(CultureInfo.InvariantCulture),
                },
            };
}
