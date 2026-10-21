using BlazorNativeSdk.Lifecycle;

namespace BlazorNativeSdk.ApplePlatform.Tests;

/// <summary>Permission states from docs/adapters/ios.md: photos (Limited = selected photos), notifications (provisional = Limited).</summary>
public sealed class AuthorizationMappingTests
{
    [Theory]
    [InlineData(0, PermissionStatus.NotDetermined)]   // AVAuthorizationStatusNotDetermined
    [InlineData(1, PermissionStatus.Restricted)]      // AVAuthorizationStatusRestricted
    [InlineData(2, PermissionStatus.Denied)]          // AVAuthorizationStatusDenied
    [InlineData(3, PermissionStatus.Granted)]         // AVAuthorizationStatusAuthorized
    [InlineData(4, PermissionStatus.Unknown)]
    [InlineData(-1, PermissionStatus.Unknown)]
    public void Maps_capture_authorization(long status, PermissionStatus expected) =>
        Assert.Equal(expected, AppleAuthorizationMapping.FromCapture(status));

    [Theory]
    [InlineData(0, PermissionStatus.NotDetermined)]   // PHAuthorizationStatusNotDetermined
    [InlineData(1, PermissionStatus.Restricted)]      // PHAuthorizationStatusRestricted
    [InlineData(2, PermissionStatus.Denied)]          // PHAuthorizationStatusDenied
    [InlineData(3, PermissionStatus.Granted)]         // PHAuthorizationStatusAuthorized
    [InlineData(4, PermissionStatus.Limited)]         // PHAuthorizationStatusLimited: selected photos only
    [InlineData(5, PermissionStatus.Unknown)]
    public void Maps_photo_library_authorization(long status, PermissionStatus expected) =>
        Assert.Equal(expected, AppleAuthorizationMapping.FromPhotos(status));

    [Theory]
    [InlineData(0, PermissionStatus.NotDetermined)]   // UNAuthorizationStatusNotDetermined
    [InlineData(1, PermissionStatus.Denied)]          // UNAuthorizationStatusDenied
    [InlineData(2, PermissionStatus.Granted)]         // UNAuthorizationStatusAuthorized
    [InlineData(3, PermissionStatus.Limited)]         // UNAuthorizationStatusProvisional
    [InlineData(4, PermissionStatus.Limited)]         // UNAuthorizationStatusEphemeral
    [InlineData(5, PermissionStatus.Unknown)]
    public void Maps_notification_authorization(long status, PermissionStatus expected) =>
        Assert.Equal(expected, AppleAuthorizationMapping.FromNotifications(status));

    [Theory]
    [InlineData(PermissionStatus.Granted, CapabilityAvailability.Available)]
    [InlineData(PermissionStatus.Limited, CapabilityAvailability.Available)]
    [InlineData(PermissionStatus.NotRequired, CapabilityAvailability.Available)]
    [InlineData(PermissionStatus.NotDetermined, CapabilityAvailability.RequiresPermission)]
    [InlineData(PermissionStatus.Denied, CapabilityAvailability.PermissionDenied)]
    [InlineData(PermissionStatus.Restricted, CapabilityAvailability.PermissionDenied)]
    [InlineData(PermissionStatus.Unknown, CapabilityAvailability.Unknown)]
    public void Maps_permission_states_to_availability(PermissionStatus status, CapabilityAvailability expected) =>
        Assert.Equal(expected, AppleAuthorizationMapping.ToAvailability(status));
}

/// <summary>Connectivity from docs/adapters/ios.md: <c>NWPathMonitor</c>; metered = expensive, data saver = Low Data Mode.</summary>
public sealed class ConnectivityMappingTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, NetworkAccess.Internet)]               // nw_path_status_satisfied
    [InlineData(2, NetworkAccess.None)]                   // nw_path_status_unsatisfied
    [InlineData(3, NetworkAccess.ConstrainedInternet)]    // nw_path_status_satisfiable
    [InlineData(0, NetworkAccess.Unknown)]                // nw_path_status_invalid
    [InlineData(9, NetworkAccess.Unknown)]
    public void Maps_path_status(long status, NetworkAccess expected) =>
        Assert.Equal(expected, Snapshot(status, wifi: true).Access);

    [Fact]
    public void Lists_interfaces_in_a_stable_order()
    {
        var snapshot = Snapshot(1, wifi: true, cellular: true, wired: true, other: true);

        Assert.Equal([ConnectionType.WiFi, ConnectionType.Cellular, ConnectionType.Ethernet], snapshot.ConnectionTypes);
    }

    [Fact]
    public void Other_interfaces_are_reported_only_alone()
    {
        Assert.Equal([ConnectionType.Other], Snapshot(1, other: true).ConnectionTypes);
        Assert.Empty(Snapshot(1).ConnectionTypes);
    }

    [Fact]
    public void An_unsatisfied_path_has_no_connection_types() =>
        Assert.Empty(Snapshot(2, wifi: true).ConnectionTypes);

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Expensive_and_constrained_paths_are_metered_and_data_saving_when_connected(long status)
    {
        var snapshot = Snapshot(status, cellular: true, isExpensive: true, isConstrained: true);

        Assert.True(snapshot.IsMetered);
        Assert.True(snapshot.IsDataSaverEnabled);
        Assert.False(Snapshot(status, wifi: true).IsMetered);
        Assert.False(Snapshot(status, wifi: true).IsDataSaverEnabled);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Metering_is_unknown_without_a_usable_path(long status)
    {
        var snapshot = Snapshot(status, cellular: true, isExpensive: true, isConstrained: true);

        Assert.Null(snapshot.IsMetered);
        Assert.Null(snapshot.IsDataSaverEnabled);
    }

    [Fact]
    public void Keeps_the_observation_time() => Assert.Equal(s_now, Snapshot(1).ObservedAt);

    [Fact]
    public void Equivalence_ignores_the_observation_time()
    {
        var snapshot = Snapshot(1, wifi: true);

        Assert.True(AppleConnectivityMapping.IsEquivalent(snapshot, snapshot with { ObservedAt = s_now.AddMinutes(5) }));
        Assert.False(AppleConnectivityMapping.IsEquivalent(snapshot, Snapshot(1, cellular: true)));
        Assert.False(AppleConnectivityMapping.IsEquivalent(snapshot, Snapshot(3, wifi: true)));
        Assert.False(AppleConnectivityMapping.IsEquivalent(snapshot, Snapshot(1, wifi: true, isExpensive: true)));
        Assert.False(AppleConnectivityMapping.IsEquivalent(snapshot, Snapshot(1, wifi: true, isConstrained: true)));
    }

    private static ConnectivitySnapshot Snapshot(
        long status,
        bool wifi = false,
        bool cellular = false,
        bool wired = false,
        bool other = false,
        bool isExpensive = false,
        bool isConstrained = false) =>
        AppleConnectivityMapping.ToSnapshot(status, wifi, cellular, wired, other, isExpensive, isConstrained, s_now);
}

public sealed class InfoPlistErrorTests
{
    [Fact]
    public void A_missing_usage_description_is_a_missing_declaration_naming_the_key()
    {
        var error = AppleInfoPlistErrors.MissingUsageDescription(NativeCapabilities.Camera, "NSCameraUsageDescription", "the camera");

        Assert.Equal(NativeResultStatus.Unavailable, error.Status);
        Assert.Equal(NativeErrorCodes.MissingDeclaration, error.Code);
        Assert.Equal(NativeCapabilities.Camera, error.Capability);
        Assert.Contains("'NSCameraUsageDescription'", error.Message, StringComparison.Ordinal);
        Assert.Contains("needs the camera", error.Message, StringComparison.Ordinal);
    }
}
