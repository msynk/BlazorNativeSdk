using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;
using Windows.Networking.Connectivity;
using Windows.Security.Authorization.AppCapabilityAccess;
using Windows.Security.Credentials.UI;
using Windows.UI.Notifications;

namespace BlazorNativeSdk.WindowsPlatform.Tests;

public sealed class ConnectivityMapperTests
{
    [Theory]
    [InlineData(NetworkConnectivityLevel.None, NetworkAccess.None)]
    [InlineData(NetworkConnectivityLevel.LocalAccess, NetworkAccess.Local)]
    [InlineData(NetworkConnectivityLevel.ConstrainedInternetAccess, NetworkAccess.ConstrainedInternet)]
    [InlineData(NetworkConnectivityLevel.InternetAccess, NetworkAccess.Internet)]
    public void Maps_connectivity_levels(NetworkConnectivityLevel level, NetworkAccess expected) =>
        Assert.Equal(expected, ConnectivityMapper.MapAccess(level));

    [Theory]
    [InlineData(NetworkCostType.Unrestricted, false)]
    [InlineData(NetworkCostType.Fixed, true)]
    [InlineData(NetworkCostType.Variable, true)]
    [InlineData(NetworkCostType.Unknown, null)]
    public void Maps_cost_types_to_metered(NetworkCostType cost, bool? expected) =>
        Assert.Equal(expected, ConnectivityMapper.IsMetered(cost));

    [Theory]
    [InlineData(true, false, null, ConnectionType.WiFi)]
    [InlineData(false, true, null, ConnectionType.Cellular)]
    [InlineData(false, false, 6u, ConnectionType.Ethernet)]
    [InlineData(false, false, 71u, ConnectionType.WiFi)]
    [InlineData(false, false, 243u, ConnectionType.Cellular)]
    [InlineData(false, false, 131u, ConnectionType.Other)]
    [InlineData(false, false, null, ConnectionType.Unknown)]
    public void Maps_connection_types(bool wlan, bool wwan, uint? iana, ConnectionType expected) =>
        Assert.Equal(expected, ConnectivityMapper.MapConnectionType(wlan, wwan, iana));

    [Fact]
    public async Task Reads_the_current_network_state()
    {
        using var service = new WindowsConnectivityService(Microsoft.Extensions.Logging.Abstractions.NullLogger<WindowsConnectivityService>.Instance);
        var snapshot = service.Current;
        Assert.NotNull(snapshot);
        Assert.True((await service.GetStatusAsync(TestContext.Current.CancellationToken)).IsAvailable);
        service.Dispose();
    }
}

public sealed class BiometricResultMapperTests
{
    [Fact]
    public void Verified_succeeds()
    {
        var result = BiometricResultMapper.MapVerification(UserConsentVerificationResult.Verified);
        Assert.True(result.IsSuccess);
        Assert.Equal(BiometricKinds.Other, result.Value.Method);
    }

    [Theory]
    [InlineData(UserConsentVerificationResult.Canceled, NativeResultStatus.Cancelled, NativeErrorCodes.Cancelled)]
    [InlineData(UserConsentVerificationResult.RetriesExhausted, NativeResultStatus.Unavailable, NativeErrorCodes.BiometricLockedOut)]
    [InlineData(UserConsentVerificationResult.DeviceNotPresent, NativeResultStatus.Unavailable, NativeErrorCodes.HardwareUnavailable)]
    [InlineData(UserConsentVerificationResult.NotConfiguredForUser, NativeResultStatus.Unavailable, NativeErrorCodes.BiometricNotEnrolled)]
    [InlineData(UserConsentVerificationResult.DisabledByPolicy, NativeResultStatus.Unavailable, NativeErrorCodes.PermissionRestricted)]
    [InlineData(UserConsentVerificationResult.DeviceBusy, NativeResultStatus.Unavailable, NativeErrorCodes.HardwareUnavailable)]
    public void Maps_verification_failures(UserConsentVerificationResult result, NativeResultStatus status, string code)
    {
        var mapped = BiometricResultMapper.MapVerification(result);
        Assert.Equal(status, mapped.Status);
        Assert.Equal(code, mapped.Error!.Code);
        Assert.Equal(NativeCapabilities.Biometrics, mapped.Error.Capability);
    }

    [Fact]
    public void Maps_availability_to_info()
    {
        var available = BiometricResultMapper.MapAvailability(UserConsentVerifierAvailability.Available);
        Assert.True(available.Value!.IsHardwarePresent && available.Value.IsEnrolled);
        Assert.Equal(BiometricStrength.Strong, available.Value.Strength);
        Assert.True(available.Value.AvailableKinds.HasFlag(BiometricKinds.DeviceCredential));

        var missing = BiometricResultMapper.MapAvailability(UserConsentVerifierAvailability.DeviceNotPresent).Value!;
        Assert.False(missing.IsHardwarePresent);
        Assert.False(missing.IsEnrolled);

        var notEnrolled = BiometricResultMapper.MapAvailability(UserConsentVerifierAvailability.NotConfiguredForUser).Value!;
        Assert.True(notEnrolled.IsHardwarePresent);
        Assert.False(notEnrolled.IsEnrolled);

        Assert.Equal(NativeErrorCodes.PermissionRestricted, BiometricResultMapper.MapAvailability(UserConsentVerifierAvailability.DisabledByPolicy).Error!.Code);
        Assert.Equal(NativeResultStatus.Unavailable, BiometricResultMapper.MapAvailability(UserConsentVerifierAvailability.DeviceBusy).Status);
    }

    [Theory]
    [InlineData(UserConsentVerifierAvailability.Available, CapabilityAvailability.Available, null)]
    [InlineData(UserConsentVerifierAvailability.DeviceNotPresent, CapabilityAvailability.Unavailable, NativeErrorCodes.HardwareUnavailable)]
    [InlineData(UserConsentVerifierAvailability.NotConfiguredForUser, CapabilityAvailability.Unavailable, NativeErrorCodes.BiometricNotEnrolled)]
    [InlineData(UserConsentVerifierAvailability.DisabledByPolicy, CapabilityAvailability.Unavailable, NativeErrorCodes.PermissionRestricted)]
    public void Maps_availability_to_status(UserConsentVerifierAvailability availability, CapabilityAvailability expected, string? code)
    {
        var status = BiometricResultMapper.MapStatus(availability);
        Assert.Equal(expected, status.Availability);
        Assert.Equal(code, status.ReasonCode);
    }
}

public sealed class PermissionMapperTests
{
    [Theory]
    [InlineData(AppCapabilityAccessStatus.Allowed, PermissionStatus.Granted)]
    [InlineData(AppCapabilityAccessStatus.DeniedByUser, PermissionStatus.Denied)]
    [InlineData(AppCapabilityAccessStatus.DeniedBySystem, PermissionStatus.Restricted)]
    [InlineData(AppCapabilityAccessStatus.NotDeclaredByApp, PermissionStatus.NotDetermined)]
    [InlineData(AppCapabilityAccessStatus.UserPromptRequired, PermissionStatus.NotDetermined)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.18362.0")]
    public void Maps_capability_access(AppCapabilityAccessStatus status, PermissionStatus expected)
    {

        Assert.Equal(expected, PermissionMapper.Map(status));
    }

    [Theory]
    [InlineData(NotificationSetting.Enabled, PermissionStatus.Granted)]
    [InlineData(NotificationSetting.DisabledForApplication, PermissionStatus.Denied)]
    [InlineData(NotificationSetting.DisabledForUser, PermissionStatus.Denied)]
    [InlineData(NotificationSetting.DisabledByGroupPolicy, PermissionStatus.Restricted)]
    [InlineData(NotificationSetting.DisabledByManifest, PermissionStatus.Restricted)]
    public void Maps_toast_settings(NotificationSetting setting, PermissionStatus expected) =>
        Assert.Equal(expected, NotificationSettingMapper.ToPermissionStatus(setting));

    [Theory]
    [InlineData("camera", "ms-settings:privacy-webcam")]
    [InlineData("microphone", "ms-settings:privacy-microphone")]
    [InlineData("notifications", "ms-settings:notifications")]
    [InlineData(null, "ms-settings:privacy")]
    public void Chooses_the_settings_page(string? permission, string expected) =>
        Assert.Equal(expected, PermissionMapper.SettingsUri(permission).OriginalString);
}

public sealed class FilePickerHelperTests
{
    [Fact]
    public void Collects_distinct_extensions_or_a_wildcard()
    {
        Assert.Equal(["*"], WindowsFilePickerService.CollectExtensions([]));
        var extensions = WindowsFilePickerService.CollectExtensions([FileTypeFilter.Images, FileTypeFilter.FromExtensions("Mixed", ".PNG", "pdf", "*")]);
        Assert.Contains(".png", extensions);
        Assert.Contains(".pdf", extensions);
        Assert.DoesNotContain("*", extensions);
        Assert.Equal(extensions.Count, extensions.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Builds_save_choices_from_filters_or_the_suggested_name()
    {
        using var content = new MemoryStream();
        var fromName = WindowsFilePickerService.BuildSaveChoices(new FileSaveRequest { SuggestedFileName = "report.PDF", Content = content });
        Assert.Equal("PDF file", fromName[0].Description);
        Assert.Equal([".pdf"], fromName[0].Extensions);

        var noExtension = WindowsFilePickerService.BuildSaveChoices(new FileSaveRequest { SuggestedFileName = "README", Content = content });
        Assert.Equal(["."], noExtension[0].Extensions);

        var merged = WindowsFilePickerService.BuildSaveChoices(new FileSaveRequest
        {
            SuggestedFileName = "a.txt",
            Content = content,
            FileTypes = [FileTypeFilter.Text, FileTypeFilter.FromExtensions("Text files", ".md"), FileTypeFilter.Json],
        });
        Assert.Equal(2, merged.Count);
        Assert.Equal([".txt", ".md"], merged[0].Extensions);
    }
}
