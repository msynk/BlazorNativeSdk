using System.Text;
using BlazorNativeSdk.Lifecycle;
using MauiConnectionProfile = Microsoft.Maui.Networking.ConnectionProfile;
using MauiNetworkAccess = Microsoft.Maui.Networking.NetworkAccess;
using NetworkAccess = BlazorNativeSdk.Lifecycle.NetworkAccess;

namespace BlazorNativeSdk.AndroidPlatform.Tests;

public sealed class StableHashTests
{
    [Fact]
    public void No_input_hashes_to_the_masked_offset_basis()
    {
        const int expected = unchecked((int)(2166136261u & 0x7FFFFFFF));

        Assert.Equal(expected, StableHash.Compute());
        Assert.Equal(expected, StableHash.Compute(string.Empty));
        Assert.Equal(expected, StableHash.Compute((string?)null));
    }

    [Theory]
    [InlineData("activity", "n1", null)]
    [InlineData("broadcast", "order-42", "reply")]
    [InlineData("alarm", "ü✓🎉", null)]
    public void Is_FNV_1a_over_UTF_16_code_units_separated_by_a_zero_byte(string kind, string id, string? action) =>
        Assert.Equal(Fnv1a(kind, id, action), StableHash.Compute(kind, id, action));

    [Fact]
    public void Is_stable_across_processes()
    {
        // PendingIntent request codes outlive the process that created them; this value must never change.
        Assert.Equal(Fnv1a("alarm", "n1"), StableHash.Compute("alarm", "n1"));
        Assert.Equal(StableHash.Compute("alarm", "n1"), StableHash.Compute("alarm", "n1"));
    }

    [Fact]
    public void Part_boundaries_matter()
    {
        Assert.NotEqual(StableHash.Compute("ab", "c"), StableHash.Compute("a", "bc"));
        Assert.NotEqual(StableHash.Compute("activity", "n1"), StableHash.Compute("broadcast", "n1"));
    }

    [Fact]
    public void Is_never_negative()
    {
        for (var i = 0; i < 2000; i++)
        {
            Assert.InRange(StableHash.Compute("activity", "notification-" + i, i % 2 == 0 ? null : "a" + i), 0, int.MaxValue);
        }
    }

    // Independent reference implementation: FNV-1a 32-bit over the UTF-16LE bytes of each part, with one zero byte between parts.
    private static int Fnv1a(params string?[] parts)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                bytes.Add(0);
            }

            bytes.AddRange(Encoding.Unicode.GetBytes(parts[i] ?? string.Empty));
        }

        var hash = 2166136261u;
        foreach (var b in bytes)
        {
            hash = unchecked((hash ^ b) * 16777619u);
        }

        return (int)(hash & 0x7FFFFFFF);
    }
}

public sealed class ConnectivityMapperTests
{
    [Theory]
    [InlineData(MauiNetworkAccess.Unknown, NetworkAccess.Unknown)]
    [InlineData(MauiNetworkAccess.None, NetworkAccess.None)]
    [InlineData(MauiNetworkAccess.Local, NetworkAccess.Local)]
    [InlineData(MauiNetworkAccess.ConstrainedInternet, NetworkAccess.ConstrainedInternet)]
    [InlineData(MauiNetworkAccess.Internet, NetworkAccess.Internet)]
    public void Maps_network_access(MauiNetworkAccess access, NetworkAccess expected) =>
        Assert.Equal(expected, AndroidConnectivityMapper.MapAccess(access));

    [Theory]
    [InlineData(MauiConnectionProfile.WiFi, ConnectionType.WiFi)]
    [InlineData(MauiConnectionProfile.Cellular, ConnectionType.Cellular)]
    [InlineData(MauiConnectionProfile.Ethernet, ConnectionType.Ethernet)]
    [InlineData(MauiConnectionProfile.Bluetooth, ConnectionType.Bluetooth)]
    [InlineData(MauiConnectionProfile.Unknown, ConnectionType.Other)]
    public void Maps_connection_profiles(MauiConnectionProfile profile, ConnectionType expected) =>
        Assert.Equal(expected, AndroidConnectivityMapper.MapProfile(profile));

    [Theory]
    [InlineData(3, true)]     // RESTRICT_BACKGROUND_STATUS_ENABLED
    [InlineData(2, false)]    // RESTRICT_BACKGROUND_STATUS_WHITELISTED: Data Saver on, but this app is exempt
    [InlineData(1, false)]    // RESTRICT_BACKGROUND_STATUS_DISABLED
    [InlineData(0, null)]
    [InlineData(4, null)]
    public void Data_saver_is_reported_only_when_it_restricts_the_app(int status, bool? expected) =>
        Assert.Equal(expected, AndroidConnectivityMapper.MapDataSaver(status));

    [Fact]
    public void Changes_ignore_the_observation_time()
    {
        var snapshot = new ConnectivitySnapshot
        {
            Access = NetworkAccess.Internet,
            ConnectionTypes = [ConnectionType.WiFi],
            IsMetered = false,
            IsDataSaverEnabled = false,
            ObservedAt = DateTimeOffset.UnixEpoch,
        };

        Assert.False(AndroidConnectivityMapper.HasChanged(snapshot, snapshot with { ObservedAt = DateTimeOffset.UnixEpoch.AddHours(1), ConnectionTypes = [ConnectionType.WiFi] }));
        Assert.True(AndroidConnectivityMapper.HasChanged(snapshot, snapshot with { Access = NetworkAccess.ConstrainedInternet }));
        Assert.True(AndroidConnectivityMapper.HasChanged(snapshot, snapshot with { IsMetered = true }));
        Assert.True(AndroidConnectivityMapper.HasChanged(snapshot, snapshot with { IsMetered = null }));
        Assert.True(AndroidConnectivityMapper.HasChanged(snapshot, snapshot with { IsDataSaverEnabled = true }));
        Assert.True(AndroidConnectivityMapper.HasChanged(snapshot, snapshot with { ConnectionTypes = [ConnectionType.WiFi, ConnectionType.Cellular] }));
        Assert.True(AndroidConnectivityMapper.HasChanged(snapshot, snapshot with { ConnectionTypes = [ConnectionType.Cellular] }));
    }
}

public sealed class LifecycleMapperTests
{
    [Theory]
    [InlineData(0, 0, AppLifecycleState.Background)]
    [InlineData(1, 0, AppLifecycleState.Inactive)]
    [InlineData(2, 1, AppLifecycleState.Active)]
    [InlineData(0, 1, AppLifecycleState.Active)]
    public void Maps_started_and_resumed_activity_counts(int started, int resumed, AppLifecycleState expected) =>
        Assert.Equal(expected, AndroidLifecycleMapper.MapCounts(started, resumed));

    [Theory]
    [InlineData(5, false)]     // TRIM_MEMORY_RUNNING_MODERATE
    [InlineData(10, true)]     // TRIM_MEMORY_RUNNING_LOW
    [InlineData(15, true)]     // TRIM_MEMORY_RUNNING_CRITICAL
    [InlineData(20, false)]    // TRIM_MEMORY_UI_HIDDEN: only means the UI went to the background
    [InlineData(40, true)]     // TRIM_MEMORY_BACKGROUND
    [InlineData(60, true)]     // TRIM_MEMORY_MODERATE
    [InlineData(80, true)]     // TRIM_MEMORY_COMPLETE
    public void Reports_memory_pressure_for_trim_levels(int level, bool expected) =>
        Assert.Equal(expected, AndroidLifecycleMapper.IsMemoryPressure(level));
}
