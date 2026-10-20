namespace BlazorNativeSdk.Devices;

/// <summary>Kinds of user verification.</summary>
[Flags]
public enum BiometricKinds
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Fingerprint (Touch ID, Android fingerprint, Windows Hello fingerprint).</summary>
    Fingerprint = 1,

    /// <summary>Face recognition (Face ID, Android face unlock, Windows Hello face).</summary>
    Face = 2,

    /// <summary>Iris recognition.</summary>
    Iris = 4,

    /// <summary>Device credential: PIN, pattern or password.</summary>
    DeviceCredential = 8,

    /// <summary>A biometric of an unspecified kind.</summary>
    Other = 16,
}

/// <summary>Strength class of the available biometrics.</summary>
public enum BiometricStrength
{
    /// <summary>Unknown.</summary>
    Unknown = 0,

    /// <summary>Convenience-grade biometrics (Android Class 2 / BIOMETRIC_WEAK).</summary>
    Weak,

    /// <summary>Strong biometrics suitable for unlocking keys (Android Class 3, Face ID, Touch ID, Windows Hello).</summary>
    Strong,
}

/// <summary>Biometric hardware and enrollment state.</summary>
public sealed record BiometricInfo
{
    /// <summary>Biometric hardware is present.</summary>
    public required bool IsHardwarePresent { get; init; }

    /// <summary>At least one biometric (or device credential, when that is the only option) is enrolled.</summary>
    public required bool IsEnrolled { get; init; }

    /// <summary>The verification methods that can be used.</summary>
    public BiometricKinds AvailableKinds { get; init; }

    /// <summary>The strength of the strongest available biometric.</summary>
    public BiometricStrength Strength { get; init; }
}

/// <summary>A request to verify the user.</summary>
public sealed record BiometricRequest
{
    /// <summary>Why the app asks for verification. Shown by the system prompt and required on iOS.</summary>
    public required string Reason { get; init; }

    /// <summary>The prompt title (Android, Windows).</summary>
    public string? Title { get; init; }

    /// <summary>The label of the cancel button where configurable.</summary>
    public string? CancelLabel { get; init; }

    /// <summary>The label of the fallback button (iOS); empty hides it.</summary>
    public string? FallbackLabel { get; init; }

    /// <summary>Allow the user to authenticate with the device PIN/pattern/password instead of a biometric.</summary>
    public bool AllowDeviceCredential { get; init; }

    /// <summary>Require <see cref="BiometricStrength.Strong"/> biometrics (Android); weaker ones are rejected.</summary>
    public bool RequireStrongBiometric { get; init; }
}

/// <summary>The result of a successful verification.</summary>
/// <param name="Method">How the user verified, when the platform reports it.</param>
public sealed record BiometricAuthentication(BiometricKinds Method);

/// <summary>Optional features reported by <see cref="IBiometricService"/>.</summary>
public static class BiometricFeatures
{
    /// <summary>Fingerprint is available.</summary>
    public const string Fingerprint = "fingerprint";

    /// <summary>Face recognition is available.</summary>
    public const string Face = "face";

    /// <summary>Iris recognition is available.</summary>
    public const string Iris = "iris";

    /// <summary>Device credential fallback is available.</summary>
    public const string DeviceCredential = "device-credential";
}

/// <summary>Verifies the user with biometrics or the device credential.</summary>
/// <remarks>
/// Local verification gates <em>local</em> functionality (unlocking the UI, revealing a stored secret). It does not prove
/// anything to a server: for server-side trust use passkeys/WebAuthn with server verification. Browsers cannot perform
/// local-only biometric checks, so the browser adapter reports this capability as unsupported.
/// <para>
/// Failure mapping: <see cref="NativeResultStatus.Cancelled"/> when the user dismisses the prompt;
/// <see cref="NativeResultStatus.Unavailable"/> with <see cref="NativeErrorCodes.BiometricNotEnrolled"/>,
/// <see cref="NativeErrorCodes.BiometricLockedOut"/> or <see cref="NativeErrorCodes.HardwareUnavailable"/>;
/// <see cref="NativeResultStatus.Failed"/> with <see cref="NativeErrorCodes.BiometricAuthenticationFailed"/> or
/// <see cref="NativeErrorCodes.BiometricFallbackRequested"/>.
/// </para>
/// </remarks>
public interface IBiometricService : INativeCapability
{
    /// <summary>Reports hardware and enrollment state without prompting.</summary>
    ValueTask<NativeResult<BiometricInfo>> GetInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>Prompts the user to verify.</summary>
    ValueTask<NativeResult<BiometricAuthentication>> AuthenticateAsync(BiometricRequest request, CancellationToken cancellationToken = default);
}
