using System.Globalization;
using LocalAuthentication;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>Maps LocalAuthentication (<c>LAError</c>) codes to <see cref="NativeError"/> values.</summary>
internal static class AppleBiometricErrorMapper
{
    /// <summary>The LocalAuthentication error domain.</summary>
    public const string ErrorDomain = "com.apple.LocalAuthentication";

    // Codes that only exist on macOS (or are reused across policies) are compared numerically so the mapping stays valid
    // whatever the binding exposes for the current platform.
    private const long CompanionNotAvailable = -11;
    private const long BiometryNotPaired = -12;
    private const long BiometryDisconnected = -13;

    /// <summary>Maps an <c>LAError</c> code.</summary>
    /// <param name="code">The error code (<see cref="LAStatus"/> value).</param>
    /// <param name="isFaceIdUsageDenied">
    /// <see langword="true"/> when the device has Face ID but the user denied this app the use of it, which LocalAuthentication
    /// reports as <see cref="LAStatus.BiometryNotAvailable"/>.
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
            (long)LAStatus.UserCancel or (long)LAStatus.SystemCancel or (long)LAStatus.AppCancel =>
                NativeError.Cancelled(capability, code == (long)LAStatus.UserCancel
                    ? "The user cancelled authentication."
                    : "Authentication was cancelled by the system or the app (for example the app moved to the background)."),
            (long)LAStatus.UserFallback =>
                NativeError.Failed(capability, NativeErrorCodes.BiometricFallbackRequested,
                    "The user chose the fallback button. Offer an alternative sign-in method, or set AllowDeviceCredential to let the system ask for the passcode."),
            (long)LAStatus.BiometryNotEnrolled =>
                NativeError.Unavailable(capability, NativeErrorCodes.BiometricNotEnrolled, "No Face ID, Touch ID or Optic ID identity is enrolled."),
            (long)LAStatus.PasscodeNotSet =>
                NativeError.Unavailable(capability, NativeErrorCodes.BiometricNotEnrolled, "No device passcode is set, so neither biometrics nor the device credential can be used."),
            (long)LAStatus.BiometryLockout =>
                NativeError.Unavailable(capability, NativeErrorCodes.BiometricLockedOut,
                    "Biometry is locked after too many failed attempts. The user must enter the device passcode (AllowDeviceCredential) to unlock it."),
            (long)LAStatus.BiometryNotAvailable when isFaceIdUsageDenied =>
                NativeError.PermissionDenied(capability, NativePermissions.Biometrics, PermissionStatus.Denied,
                    "The user denied this app the use of Face ID. Direct the user to the app's page in Settings."),
            (long)LAStatus.BiometryNotAvailable or BiometryNotPaired or BiometryDisconnected or CompanionNotAvailable =>
                NativeError.Unavailable(capability, NativeErrorCodes.HardwareUnavailable, "Biometric hardware is not available on this device."),
            (long)LAStatus.AuthenticationFailed =>
                NativeError.Failed(capability, NativeErrorCodes.BiometricAuthenticationFailed, "The user could not be verified."),
            (long)LAStatus.NotInteractive =>
                NativeError.Unavailable(capability, NativeErrorCodes.HostNotReady, "Authentication needs to show UI, but the app cannot present UI right now."),
            _ => NativeError.Failed(capability, NativeErrorCodes.PlatformError, description ?? $"LocalAuthentication failed with code {code}."),
        };

        return error with { Details = details };
    }

    /// <summary>Maps an <see cref="NSError"/> from LocalAuthentication.</summary>
    public static NativeError Map(NSError error, bool isFaceIdUsageDenied) =>
        string.Equals(error.Domain, ErrorDomain, StringComparison.Ordinal)
            ? Map((long)error.Code, isFaceIdUsageDenied, error.LocalizedDescription)
            : NativeError.Failed(NativeCapabilities.Biometrics, NativeErrorCodes.PlatformError, error.LocalizedDescription) with
            {
                Details = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["domain"] = error.Domain,
                    ["code"] = ((long)error.Code).ToString(CultureInfo.InvariantCulture),
                },
            };
}
