using System.Globalization;

namespace BlazorNativeSdk.ApplePlatform.Tests;

public sealed class BiometricErrorMapperTests
{
    [Theory]
    [InlineData(-2, NativeResultStatus.Cancelled, NativeErrorCodes.Cancelled)]                            // LAErrorUserCancel
    [InlineData(-4, NativeResultStatus.Cancelled, NativeErrorCodes.Cancelled)]                            // LAErrorSystemCancel
    [InlineData(-9, NativeResultStatus.Cancelled, NativeErrorCodes.Cancelled)]                            // LAErrorAppCancel
    [InlineData(-3, NativeResultStatus.Failed, NativeErrorCodes.BiometricFallbackRequested)]              // LAErrorUserFallback
    [InlineData(-7, NativeResultStatus.Unavailable, NativeErrorCodes.BiometricNotEnrolled)]               // LAErrorBiometryNotEnrolled
    [InlineData(-5, NativeResultStatus.Unavailable, NativeErrorCodes.BiometricNotEnrolled)]               // LAErrorPasscodeNotSet
    [InlineData(-8, NativeResultStatus.Unavailable, NativeErrorCodes.BiometricLockedOut)]                 // LAErrorBiometryLockout
    [InlineData(-6, NativeResultStatus.Unavailable, NativeErrorCodes.HardwareUnavailable)]                // LAErrorBiometryNotAvailable
    [InlineData(-11, NativeResultStatus.Unavailable, NativeErrorCodes.HardwareUnavailable)]               // LAErrorCompanionNotAvailable
    [InlineData(-12, NativeResultStatus.Unavailable, NativeErrorCodes.HardwareUnavailable)]               // LAErrorBiometryNotPaired
    [InlineData(-13, NativeResultStatus.Unavailable, NativeErrorCodes.HardwareUnavailable)]               // LAErrorBiometryDisconnected
    [InlineData(-1, NativeResultStatus.Failed, NativeErrorCodes.BiometricAuthenticationFailed)]           // LAErrorAuthenticationFailed
    [InlineData(-1004, NativeResultStatus.Unavailable, NativeErrorCodes.HostNotReady)]                    // LAErrorNotInteractive
    [InlineData(-10, NativeResultStatus.Failed, NativeErrorCodes.PlatformError)]                          // LAErrorInvalidContext
    [InlineData(-14, NativeResultStatus.Failed, NativeErrorCodes.PlatformError)]                          // LAErrorInvalidDimensions
    [InlineData(0, NativeResultStatus.Failed, NativeErrorCodes.PlatformError)]
    public void Maps_LAError_codes(long code, NativeResultStatus status, string errorCode)
    {
        var error = AppleBiometricErrorMapper.Map(code, isFaceIdUsageDenied: false);

        Assert.Equal(status, error.Status);
        Assert.Equal(errorCode, error.Code);
        Assert.Equal(NativeCapabilities.Biometrics, error.Capability);
        Assert.Equal(AppleBiometricErrorMapper.ErrorDomain, error.Details!["domain"]);
        Assert.Equal(code.ToString(CultureInfo.InvariantCulture), error.Details["code"]);
    }

    [Fact]
    public void Only_the_user_cancel_is_reported_as_a_user_cancel()
    {
        Assert.Equal("The user cancelled authentication.", AppleBiometricErrorMapper.Map(-2, false).Message);
        Assert.NotEqual("The user cancelled authentication.", AppleBiometricErrorMapper.Map(-4, false).Message);
        Assert.NotEqual("The user cancelled authentication.", AppleBiometricErrorMapper.Map(-9, false).Message);
    }

    [Fact]
    public void A_refused_Face_ID_is_a_denied_biometrics_permission()
    {
        var error = AppleBiometricErrorMapper.Map(-6, isFaceIdUsageDenied: true);

        Assert.Equal(NativeResultStatus.PermissionDenied, error.Status);
        Assert.Equal(NativeErrorCodes.PermissionDenied, error.Code);
        Assert.Equal(NativePermissions.Biometrics, error.Permission);
        Assert.Equal(PermissionStatus.Denied, error.PermissionStatus);
    }

    [Theory]
    [InlineData(-7)]
    [InlineData(-8)]
    [InlineData(-12)]
    public void A_refused_Face_ID_only_affects_BiometryNotAvailable(long code) =>
        Assert.Equal(AppleBiometricErrorMapper.Map(code, false).Code, AppleBiometricErrorMapper.Map(code, true).Code);

    [Fact]
    public void The_platform_description_is_kept_as_a_detail()
    {
        var error = AppleBiometricErrorMapper.Map(-8, false, "Biometry is locked out.");

        Assert.Equal("Biometry is locked out.", error.Details!["description"]);
        Assert.False(AppleBiometricErrorMapper.Map(-8, false, string.Empty).Details!.ContainsKey("description"));
        Assert.False(AppleBiometricErrorMapper.Map(-8, false).Details!.ContainsKey("description"));
    }

    [Theory]
    [InlineData("Something odd", "Something odd")]
    [InlineData(null, "LocalAuthentication failed with code -10.")]
    public void Unmapped_codes_use_the_platform_description(string? description, string expected) =>
        Assert.Equal(expected, AppleBiometricErrorMapper.Map(-10, false, description).Message);

    [Fact]
    public void Errors_in_the_LocalAuthentication_domain_are_mapped_by_code()
    {
        var error = AppleBiometricErrorMapper.Map(AppleBiometricErrorMapper.ErrorDomain, -8, false, "Locked");

        Assert.Equal(NativeErrorCodes.BiometricLockedOut, error.Code);
        Assert.Equal("Locked", error.Details!["description"]);
    }

    [Fact]
    public void Errors_from_other_domains_are_platform_errors()
    {
        var error = AppleBiometricErrorMapper.Map("NSOSStatusErrorDomain", -2, true, "Interaction not allowed");

        Assert.Equal(NativeResultStatus.Failed, error.Status);
        Assert.Equal(NativeErrorCodes.PlatformError, error.Code);
        Assert.Equal("Interaction not allowed", error.Message);
        Assert.Equal("NSOSStatusErrorDomain", error.Details!["domain"]);
        Assert.Equal("-2", error.Details["code"]);
    }
}
