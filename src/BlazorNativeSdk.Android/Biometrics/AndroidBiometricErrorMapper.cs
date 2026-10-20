using BlazorNativeSdk.Devices;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>
/// Pure mapping logic for AndroidX Biometric: authenticator selection per API level, <c>canAuthenticate</c> results and
/// <c>BiometricPrompt</c> error codes. The AndroidX constants are duplicated as literals so the mapping can be unit
/// tested without the bindings.
/// </summary>
internal static class AndroidBiometricErrorMapper
{
    // BiometricManager.Authenticators
    public const int BiometricStrong = 0x000F;
    public const int BiometricWeak = 0x00FF;
    public const int DeviceCredential = 0x8000;

    // BiometricManager.BIOMETRIC_*
    public const int Success = 0;
    public const int StatusUnknown = -1;
    public const int ErrorUnsupported = -2;
    public const int ErrorHwUnavailable = 1;
    public const int ErrorNoneEnrolled = 11;
    public const int ErrorNoHardware = 12;
    public const int ErrorSecurityUpdateRequired = 15;

    // BiometricPrompt.ERROR_*
    public const int PromptHwUnavailable = 1;
    public const int PromptUnableToProcess = 2;
    public const int PromptTimeout = 3;
    public const int PromptNoSpace = 4;
    public const int PromptCanceled = 5;
    public const int PromptLockout = 7;
    public const int PromptVendor = 8;
    public const int PromptLockoutPermanent = 9;
    public const int PromptUserCanceled = 10;
    public const int PromptNoBiometrics = 11;
    public const int PromptHwNotPresent = 12;
    public const int PromptNegativeButton = 13;
    public const int PromptNoDeviceCredential = 14;
    public const int PromptSecurityUpdateRequired = 15;

    // BiometricPrompt.AUTHENTICATION_RESULT_TYPE_*
    public const int ResultTypeUnknown = -1;
    public const int ResultTypeDeviceCredential = 1;
    public const int ResultTypeBiometric = 2;

    /// <summary>
    /// Chooses the allowed authenticators. AndroidX Biometric cannot combine <c>BIOMETRIC_STRONG</c> with
    /// <c>DEVICE_CREDENTIAL</c> on API 28–29; there the strength requirement wins and the credential fallback is dropped.
    /// </summary>
    /// <returns>The authenticator flags and whether the device credential is allowed (which forbids a negative button).</returns>
    public static (int Authenticators, bool DeviceCredentialAllowed) ResolveAuthenticators(int apiLevel, bool requireStrong, bool allowDeviceCredential)
    {
        var biometric = requireStrong ? BiometricStrong : BiometricWeak;
        if (!allowDeviceCredential)
        {
            return (biometric, false);
        }

        if (requireStrong && apiLevel is 28 or 29)
        {
            return (biometric, false);
        }

        return (biometric | DeviceCredential, true);
    }

    /// <summary>Maps a non-success <c>BiometricManager.canAuthenticate</c> result to an error.</summary>
    public static NativeError MapCanAuthenticate(int code) => (code switch
    {
        ErrorNoneEnrolled => NativeError.Unavailable(NativeCapabilities.Biometrics, NativeErrorCodes.BiometricNotEnrolled,
            "No biometric (or device credential, when allowed) is enrolled on this device."),
        ErrorNoHardware or ErrorUnsupported => NativeError.Unavailable(NativeCapabilities.Biometrics, NativeErrorCodes.HardwareUnavailable,
            "This device has no biometric hardware that satisfies the request."),
        ErrorHwUnavailable => NativeError.Unavailable(NativeCapabilities.Biometrics, NativeErrorCodes.HardwareUnavailable,
            "The biometric hardware is temporarily unavailable."),
        ErrorSecurityUpdateRequired => NativeError.Unavailable(NativeCapabilities.Biometrics, NativeErrorCodes.HardwareUnavailable,
            "A security vulnerability was found in the biometric sensor; a system update is required."),
        _ => NativeError.Failed(NativeCapabilities.Biometrics, NativeErrorCodes.PlatformError,
            $"Biometric authentication is not available (status {code})."),
    }) with { Details = CodeDetails("canAuthenticate", code) };

    /// <summary>Maps a terminal <c>BiometricPrompt</c> error. <c>onAuthenticationFailed</c> is a soft failure and never reaches this method.</summary>
    public static NativeError MapPromptError(int code, string? message)
    {
        var capability = NativeCapabilities.Biometrics;
        var text = string.IsNullOrWhiteSpace(message) ? $"Biometric authentication failed (error {code})." : message;
        return (code switch
        {
            PromptUserCanceled or PromptNegativeButton => NativeError.Cancelled(capability),
            PromptCanceled => NativeError.Cancelled(capability, "Authentication was cancelled by the system (for example the app went to the background)."),
            PromptLockout => NativeError.Unavailable(capability, NativeErrorCodes.BiometricLockedOut, "Too many attempts; biometrics are temporarily locked."),
            PromptLockoutPermanent => NativeError.Unavailable(capability, NativeErrorCodes.BiometricLockedOut,
                "Too many attempts; biometrics are locked until the user unlocks the device with the device credential."),
            PromptNoBiometrics => NativeError.Unavailable(capability, NativeErrorCodes.BiometricNotEnrolled, "No biometric is enrolled."),
            PromptNoDeviceCredential => NativeError.Unavailable(capability, NativeErrorCodes.BiometricNotEnrolled, "No PIN, pattern or password is set."),
            PromptHwNotPresent => NativeError.Unavailable(capability, NativeErrorCodes.HardwareUnavailable, "No biometric hardware is present."),
            PromptHwUnavailable => NativeError.Unavailable(capability, NativeErrorCodes.HardwareUnavailable, "The biometric hardware is unavailable."),
            PromptSecurityUpdateRequired => NativeError.Unavailable(capability, NativeErrorCodes.HardwareUnavailable,
                "A security update is required before the biometric sensor can be used."),
            PromptTimeout => NativeError.Failed(capability, NativeErrorCodes.Timeout, "Authentication timed out."),
            _ => NativeError.Failed(capability, NativeErrorCodes.PlatformError, text),
        }) with { Details = CodeDetails("biometricPrompt", code) };
    }

    /// <summary>Maps <c>AuthenticationResult.getAuthenticationType()</c> to the verification method.</summary>
    /// <param name="authenticationType">The AndroidX result type.</param>
    /// <param name="biometricHardware">The biometric kinds present on the device.</param>
    public static BiometricKinds MapMethod(int authenticationType, BiometricKinds biometricHardware)
    {
        if (authenticationType == ResultTypeDeviceCredential)
        {
            return BiometricKinds.DeviceCredential;
        }

        if (authenticationType != ResultTypeBiometric)
        {
            return BiometricKinds.None;
        }

        // Android does not say which modality was used; report it only when the device has exactly one.
        var kinds = biometricHardware & (BiometricKinds.Fingerprint | BiometricKinds.Face | BiometricKinds.Iris);
        return kinds is BiometricKinds.Fingerprint or BiometricKinds.Face or BiometricKinds.Iris ? kinds : BiometricKinds.Other;
    }

    /// <summary>Builds <see cref="BiometricInfo"/> from <c>canAuthenticate</c> results and device features.</summary>
    public static BiometricInfo CreateInfo(int weakResult, int strongResult, BiometricKinds hardwareKinds, bool deviceSecure)
    {
        var hardwarePresent = weakResult is Success or ErrorNoneEnrolled or ErrorHwUnavailable or ErrorSecurityUpdateRequired;
        var biometricEnrolled = weakResult == Success;
        var kinds = BiometricKinds.None;
        if (biometricEnrolled)
        {
            kinds |= hardwareKinds == BiometricKinds.None ? BiometricKinds.Other : hardwareKinds;
        }

        if (deviceSecure)
        {
            kinds |= BiometricKinds.DeviceCredential;
        }

        return new BiometricInfo
        {
            IsHardwarePresent = hardwarePresent,
            IsEnrolled = hardwarePresent ? biometricEnrolled : deviceSecure,
            AvailableKinds = kinds,
            Strength = strongResult == Success ? BiometricStrength.Strong : biometricEnrolled ? BiometricStrength.Weak : BiometricStrength.Unknown,
        };
    }

    private static Dictionary<string, string> CodeDetails(string source, int code) => new()
    {
        ["android.source"] = source,
        ["android.code"] = code.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
