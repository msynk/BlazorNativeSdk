using System.Globalization;
using BlazorNativeSdk.Devices;
using M = BlazorNativeSdk.AndroidPlatform.AndroidBiometricErrorMapper;

namespace BlazorNativeSdk.AndroidPlatform.Tests;

public sealed class BiometricMapperTests
{
    [Theory]
    [InlineData(24, false, false, M.BiometricWeak, false)]
    [InlineData(30, true, false, M.BiometricStrong, false)]
    [InlineData(27, false, true, M.BiometricWeak | M.DeviceCredential, true)]
    [InlineData(27, true, true, M.BiometricStrong | M.DeviceCredential, true)]
    [InlineData(28, true, true, M.BiometricStrong, false)]    // Android 9: strong + credential is not supported, credential dropped
    [InlineData(29, true, true, M.BiometricStrong, false)]    // Android 10: same
    [InlineData(28, false, true, M.BiometricWeak | M.DeviceCredential, true)]
    [InlineData(29, false, true, M.BiometricWeak | M.DeviceCredential, true)]
    [InlineData(30, true, true, M.BiometricStrong | M.DeviceCredential, true)]
    [InlineData(34, true, true, M.BiometricStrong | M.DeviceCredential, true)]
    public void Resolves_authenticators_per_api_level(int apiLevel, bool requireStrong, bool allowCredential, int expected, bool credentialAllowed) =>
        Assert.Equal((expected, credentialAllowed), M.ResolveAuthenticators(apiLevel, requireStrong, allowCredential));

    [Theory]
    [InlineData(M.ErrorNoneEnrolled, NativeResultStatus.Unavailable, NativeErrorCodes.BiometricNotEnrolled)]
    [InlineData(M.ErrorNoHardware, NativeResultStatus.Unavailable, NativeErrorCodes.HardwareUnavailable)]
    [InlineData(M.ErrorUnsupported, NativeResultStatus.Unavailable, NativeErrorCodes.HardwareUnavailable)]
    [InlineData(M.ErrorHwUnavailable, NativeResultStatus.Unavailable, NativeErrorCodes.HardwareUnavailable)]
    [InlineData(M.ErrorSecurityUpdateRequired, NativeResultStatus.Unavailable, NativeErrorCodes.HardwareUnavailable)]
    [InlineData(M.StatusUnknown, NativeResultStatus.Failed, NativeErrorCodes.PlatformError)]
    [InlineData(99, NativeResultStatus.Failed, NativeErrorCodes.PlatformError)]
    public void Maps_canAuthenticate_results(int code, NativeResultStatus status, string errorCode)
    {
        var error = M.MapCanAuthenticate(code);

        Assert.Equal(status, error.Status);
        Assert.Equal(errorCode, error.Code);
        Assert.Equal(NativeCapabilities.Biometrics, error.Capability);
        Assert.Equal("canAuthenticate", error.Details!["android.source"]);
        Assert.Equal(code.ToString(CultureInfo.InvariantCulture), error.Details["android.code"]);
    }

    [Theory]
    [InlineData(M.PromptUserCanceled, NativeResultStatus.Cancelled, NativeErrorCodes.Cancelled)]
    [InlineData(M.PromptNegativeButton, NativeResultStatus.Cancelled, NativeErrorCodes.Cancelled)]
    [InlineData(M.PromptCanceled, NativeResultStatus.Cancelled, NativeErrorCodes.Cancelled)]
    [InlineData(M.PromptLockout, NativeResultStatus.Unavailable, NativeErrorCodes.BiometricLockedOut)]
    [InlineData(M.PromptLockoutPermanent, NativeResultStatus.Unavailable, NativeErrorCodes.BiometricLockedOut)]
    [InlineData(M.PromptNoBiometrics, NativeResultStatus.Unavailable, NativeErrorCodes.BiometricNotEnrolled)]
    [InlineData(M.PromptNoDeviceCredential, NativeResultStatus.Unavailable, NativeErrorCodes.BiometricNotEnrolled)]
    [InlineData(M.PromptHwNotPresent, NativeResultStatus.Unavailable, NativeErrorCodes.HardwareUnavailable)]
    [InlineData(M.PromptHwUnavailable, NativeResultStatus.Unavailable, NativeErrorCodes.HardwareUnavailable)]
    [InlineData(M.PromptSecurityUpdateRequired, NativeResultStatus.Unavailable, NativeErrorCodes.HardwareUnavailable)]
    [InlineData(M.PromptTimeout, NativeResultStatus.Failed, NativeErrorCodes.Timeout)]
    [InlineData(M.PromptUnableToProcess, NativeResultStatus.Failed, NativeErrorCodes.PlatformError)]
    [InlineData(M.PromptNoSpace, NativeResultStatus.Failed, NativeErrorCodes.PlatformError)]
    [InlineData(M.PromptVendor, NativeResultStatus.Failed, NativeErrorCodes.PlatformError)]
    public void Maps_prompt_errors(int code, NativeResultStatus status, string errorCode)
    {
        var error = M.MapPromptError(code, "platform text");

        Assert.Equal(status, error.Status);
        Assert.Equal(errorCode, error.Code);
        Assert.Equal(NativeCapabilities.Biometrics, error.Capability);
        Assert.Equal("biometricPrompt", error.Details!["android.source"]);
        Assert.Equal(code.ToString(CultureInfo.InvariantCulture), error.Details["android.code"]);
    }

    [Theory]
    [InlineData("Vendor says no", "Vendor says no")]
    [InlineData(null, "Biometric authentication failed (error 8).")]
    [InlineData("", "Biometric authentication failed (error 8).")]
    public void Unmapped_prompt_errors_keep_the_platform_message(string? message, string expected) =>
        Assert.Equal(expected, M.MapPromptError(M.PromptVendor, message).Message);

    [Theory]
    [InlineData(M.ResultTypeDeviceCredential, BiometricKinds.Fingerprint, BiometricKinds.DeviceCredential)]
    [InlineData(M.ResultTypeUnknown, BiometricKinds.Fingerprint, BiometricKinds.None)]
    [InlineData(M.ResultTypeBiometric, BiometricKinds.Fingerprint, BiometricKinds.Fingerprint)]
    [InlineData(M.ResultTypeBiometric, BiometricKinds.Face, BiometricKinds.Face)]
    [InlineData(M.ResultTypeBiometric, BiometricKinds.Iris, BiometricKinds.Iris)]
    [InlineData(M.ResultTypeBiometric, BiometricKinds.Fingerprint | BiometricKinds.DeviceCredential, BiometricKinds.Fingerprint)]
    [InlineData(M.ResultTypeBiometric, BiometricKinds.Fingerprint | BiometricKinds.Face, BiometricKinds.Other)]   // ambiguous modality
    [InlineData(M.ResultTypeBiometric, BiometricKinds.None, BiometricKinds.Other)]
    public void Reports_the_modality_only_when_unambiguous(int resultType, BiometricKinds hardware, BiometricKinds expected) =>
        Assert.Equal(expected, M.MapMethod(resultType, hardware));

    [Fact]
    public void Enrolled_strong_biometrics()
    {
        var info = M.CreateInfo(M.Success, M.Success, BiometricKinds.Fingerprint, deviceSecure: true);

        Assert.True(info.IsHardwarePresent);
        Assert.True(info.IsEnrolled);
        Assert.Equal(BiometricKinds.Fingerprint | BiometricKinds.DeviceCredential, info.AvailableKinds);
        Assert.Equal(BiometricStrength.Strong, info.Strength);
    }

    [Fact]
    public void Enrolled_weak_biometrics_of_an_unknown_kind()
    {
        var info = M.CreateInfo(M.Success, M.ErrorNoneEnrolled, BiometricKinds.None, deviceSecure: false);

        Assert.Equal(BiometricKinds.Other, info.AvailableKinds);
        Assert.Equal(BiometricStrength.Weak, info.Strength);
    }

    [Theory]
    [InlineData(M.ErrorNoneEnrolled)]
    [InlineData(M.ErrorHwUnavailable)]
    [InlineData(M.ErrorSecurityUpdateRequired)]
    public void Hardware_present_but_not_usable(int weakResult)
    {
        var info = M.CreateInfo(weakResult, weakResult, BiometricKinds.Face, deviceSecure: true);

        Assert.True(info.IsHardwarePresent);
        Assert.False(info.IsEnrolled);
        Assert.Equal(BiometricKinds.DeviceCredential, info.AvailableKinds);
        Assert.Equal(BiometricStrength.Unknown, info.Strength);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Without_biometric_hardware_the_device_credential_counts_as_enrolled(bool deviceSecure)
    {
        var info = M.CreateInfo(M.ErrorNoHardware, M.ErrorNoHardware, BiometricKinds.None, deviceSecure);

        Assert.False(info.IsHardwarePresent);
        Assert.Equal(deviceSecure, info.IsEnrolled);
        Assert.Equal(deviceSecure ? BiometricKinds.DeviceCredential : BiometricKinds.None, info.AvailableKinds);
    }
}
