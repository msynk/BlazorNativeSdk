using BlazorNativeSdk.Devices;
using LocalAuthentication;
using Microsoft.Extensions.Logging;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>
/// <see cref="IBiometricService"/> backed by LocalAuthentication (Face ID, Touch ID, Optic ID and the device passcode).
/// </summary>
/// <remarks>
/// <para>
/// Face ID requires <c>NSFaceIDUsageDescription</c> in Info.plist; without it iOS terminates the app, so the adapter refuses
/// with <see cref="NativeErrorCodes.MissingDeclaration"/> instead. A user who denied the app Face ID is reported as
/// <see cref="NativeResultStatus.PermissionDenied"/>.
/// </para>
/// <para>
/// With <see cref="BiometricRequest.AllowDeviceCredential"/> the system may accept the passcode instead of a biometric; iOS does
/// not report which one was used, so <see cref="BiometricAuthentication.Method"/> is <see cref="BiometricKinds.None"/> in that case.
/// <see cref="BiometricRequest.Title"/> and <see cref="BiometricRequest.RequireStrongBiometric"/> have no effect: iOS shows its own
/// title and every Apple biometric is strong.
/// </para>
/// </remarks>
public sealed partial class AppleBiometricService : IBiometricService
{
    private readonly ILogger<AppleBiometricService> _logger;

    /// <summary>Creates the service.</summary>
    public AppleBiometricService(ILogger<AppleBiometricService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.Biometrics;

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var probe = AppleBiometricProbe.Run();
        var adapter = AppleHost.AdapterName;
        var features = GetFeatures(probe);

        if (probe.HasFaceId && AppleInfoPlist.RequireUsageDescription(Capability, AppleInfoPlist.FaceIdUsage, "Face ID") is { } missing)
        {
            return ValueTask.FromResult(CapabilityStatus.FromError(missing, adapter));
        }

        if (probe.IsFaceIdUsageDenied)
        {
            return ValueTask.FromResult(CapabilityStatus.PermissionDenied(Capability, "The user denied this app the use of Face ID.", adapter).WithFeatures(features));
        }

        if (probe.BiometricsUsable)
        {
            return ValueTask.FromResult(CapabilityStatus.Available(Capability, adapter, features));
        }

        if (probe.DeviceOwnerUsable)
        {
            // Biometrics are not usable (not enrolled, locked out or absent) but the passcode is: requests with
            // AllowDeviceCredential still succeed.
            var biometricError = probe.BiometricError;
            return ValueTask.FromResult(CapabilityStatus.Available(Capability, adapter, features) with
            {
                ReasonCode = biometricError?.Code,
                Reason = biometricError?.Message,
            });
        }

        var error = probe.DeviceOwnerError ?? probe.BiometricError
            ?? NativeError.Unavailable(Capability, NativeErrorCodes.HardwareUnavailable, "Neither biometrics nor a device passcode can be used.");
        return ValueTask.FromResult(CapabilityStatus.FromError(error, adapter));
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<BiometricInfo>> GetInfoAsync(CancellationToken cancellationToken = default) =>
        NativeOperation.RunAsync<BiometricInfo>(Capability, _ =>
        {
            var probe = AppleBiometricProbe.Run();
            var kinds = BiometricKinds.None;
            if (probe.BiometricsUsable)
            {
                kinds |= probe.BiometricKind;
            }

            if (probe.DeviceOwnerUsable)
            {
                kinds |= BiometricKinds.DeviceCredential;
            }

            var biometricEnrolled = probe.BiometricsUsable || probe.BiometricErrorCode == (long)LAStatus.BiometryLockout;
            var info = new BiometricInfo
            {
                IsHardwarePresent = probe.IsHardwarePresent,
                IsEnrolled = biometricEnrolled || (!probe.IsHardwarePresent && probe.DeviceOwnerUsable),
                AvailableKinds = kinds,
                Strength = probe.IsHardwarePresent ? BiometricStrength.Strong : BiometricStrength.Unknown,
            };
            return ValueTask.FromResult(NativeResult.Success(info));
        }, cancellationToken);

    /// <inheritdoc />
    public ValueTask<NativeResult<BiometricAuthentication>> AuthenticateAsync(BiometricRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return NativeOperation.RunAsync<BiometricAuthentication>(Capability, async token =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason))
            {
                return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "BiometricRequest.Reason is required on iOS: it is shown in the system prompt.");
            }

            using var context = new LAContext();
            if (request.FallbackLabel is { } fallback)
            {
                context.LocalizedFallbackTitle = fallback;
            }

            if (!string.IsNullOrEmpty(request.CancelLabel))
            {
                context.LocalizedCancelTitle = request.CancelLabel;
            }

            // Evaluating the biometric policy first populates BiometryType, which tells whether Face ID (and therefore its
            // usage description) is involved.
            var biometricsUsable = context.CanEvaluatePolicy(LAPolicy.DeviceOwnerAuthenticationWithBiometrics, out var biometricError);
            var biometryType = context.BiometryType;
            var faceIdDenied = !biometricsUsable && biometryType == LABiometryType.FaceId && biometricError?.Code == (nint)(long)LAStatus.BiometryNotAvailable;
            if (biometryType == LABiometryType.FaceId && AppleInfoPlist.RequireUsageDescription(Capability, AppleInfoPlist.FaceIdUsage, "Face ID") is { } missing)
            {
                return missing;
            }

            var policy = request.AllowDeviceCredential ? LAPolicy.DeviceOwnerAuthentication : LAPolicy.DeviceOwnerAuthenticationWithBiometrics;
            if (!context.CanEvaluatePolicy(policy, out var policyError))
            {
                return policyError is null
                    ? NativeError.Unavailable(Capability, NativeErrorCodes.HardwareUnavailable, "The authentication policy cannot be evaluated on this device.")
                    : AppleBiometricErrorMapper.Map(policyError.Domain, (long)policyError.Code, faceIdDenied, policyError.LocalizedDescription);
            }

            Tuple<bool, NSError?> outcome;
            using (token.Register(static state => ((LAContext)state!).Invalidate(), context))
            {
                outcome = await context.EvaluatePolicyAsync(policy, request.Reason).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            if (outcome.Item1)
            {
                var method = policy == LAPolicy.DeviceOwnerAuthenticationWithBiometrics ? AppleBiometricProbe.ToKind(biometryType) : BiometricKinds.None;
                return new BiometricAuthentication(method);
            }

            var error = outcome.Item2 is { } nsError
                ? AppleBiometricErrorMapper.Map(nsError.Domain, (long)nsError.Code, faceIdDenied, nsError.LocalizedDescription)
                : NativeError.Failed(Capability, NativeErrorCodes.PlatformError, "Authentication failed without an error.");
            if (error.Status == NativeResultStatus.Failed)
            {
                LogAuthenticationFailed(_logger, error.Code);
            }

            return error;
        }, cancellationToken);
    }

    private static List<string> GetFeatures(AppleBiometricProbe probe)
    {
        var features = new List<string>();
        if (probe.IsHardwarePresent)
        {
            features.Add(probe.BiometricKind switch
            {
                BiometricKinds.Face => BiometricFeatures.Face,
                BiometricKinds.Iris => BiometricFeatures.Iris,
                _ => BiometricFeatures.Fingerprint,
            });
        }

        if (probe.DeviceOwnerUsable)
        {
            features.Add(BiometricFeatures.DeviceCredential);
        }

        return features;
    }

    [LoggerMessage(EventId = 3301, Level = LogLevel.Debug, Message = "Biometric authentication failed with {Code}.")]
    private static partial void LogAuthenticationFailed(ILogger logger, string code);
}

/// <summary>A snapshot of what LocalAuthentication can evaluate, taken without prompting.</summary>
internal readonly record struct AppleBiometricProbe(
    LABiometryType BiometryType,
    bool BiometricsUsable,
    long? BiometricErrorCode,
    string? BiometricErrorDescription,
    bool DeviceOwnerUsable,
    long? DeviceOwnerErrorCode,
    string? DeviceOwnerErrorDescription)
{
    private const long OpticId = 4;

    public bool HasFaceId => BiometryType == LABiometryType.FaceId;

    public bool IsHardwarePresent => BiometryType != LABiometryType.None;

    /// <summary>Face ID hardware exists but LocalAuthentication reports it as unavailable: the user denied the app.</summary>
    public bool IsFaceIdUsageDenied => HasFaceId && !BiometricsUsable && BiometricErrorCode == (long)LAStatus.BiometryNotAvailable;

    public BiometricKinds BiometricKind => ToKind(BiometryType);

    public NativeError? BiometricError => BiometricErrorCode is { } code
        ? AppleBiometricErrorMapper.Map(code, IsFaceIdUsageDenied, BiometricErrorDescription)
        : null;

    public NativeError? DeviceOwnerError => DeviceOwnerErrorCode is { } code
        ? AppleBiometricErrorMapper.Map(code, IsFaceIdUsageDenied, DeviceOwnerErrorDescription)
        : null;

    public static BiometricKinds ToKind(LABiometryType type) => type switch
    {
        LABiometryType.FaceId => BiometricKinds.Face,
        LABiometryType.TouchId => BiometricKinds.Fingerprint,
        _ when (long)type == OpticId => BiometricKinds.Iris,
        LABiometryType.None => BiometricKinds.None,
        _ => BiometricKinds.Other,
    };

    public static AppleBiometricProbe Run()
    {
        using var context = new LAContext();
        var biometrics = context.CanEvaluatePolicy(LAPolicy.DeviceOwnerAuthenticationWithBiometrics, out var biometricError);
        var type = context.BiometryType;
        var owner = context.CanEvaluatePolicy(LAPolicy.DeviceOwnerAuthentication, out var ownerError);
        return new AppleBiometricProbe(
            type,
            biometrics,
            biometrics ? null : (long?)(biometricError?.Code ?? (nint)(long)LAStatus.BiometryNotAvailable),
            biometricError?.LocalizedDescription,
            owner,
            owner ? null : (long?)(ownerError?.Code ?? (nint)(long)LAStatus.PasscodeNotSet),
            ownerError?.LocalizedDescription);
    }
}
