using BlazorNativeSdk.Lifecycle;
using Network;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>Maps <c>NWPath</c> observations to <see cref="ConnectivitySnapshot"/>s.</summary>
internal static class AppleConnectivityMapping
{
    /// <summary>Builds a snapshot from the properties of an <c>NWPath</c>.</summary>
    /// <param name="status">The path status.</param>
    /// <param name="wifi">The path uses Wi-Fi.</param>
    /// <param name="cellular">The path uses cellular.</param>
    /// <param name="wired">The path uses wired Ethernet.</param>
    /// <param name="other">The path uses another interface (for example a VPN or a tethered device).</param>
    /// <param name="isExpensive">The path is expensive (cellular or a personal hotspot).</param>
    /// <param name="isConstrained">The path is constrained (Low Data Mode).</param>
    /// <param name="observedAt">The observation time.</param>
    public static ConnectivitySnapshot ToSnapshot(NWPathStatus status, bool wifi, bool cellular, bool wired, bool other, bool isExpensive, bool isConstrained, DateTimeOffset observedAt)
    {
        var access = status switch
        {
            NWPathStatus.Satisfied => NetworkAccess.Internet,
            NWPathStatus.Unsatisfied => NetworkAccess.None,

            // "requiresConnection": usable once a connection is established (for example an on-demand VPN or cellular that is
            // still coming up).
            NWPathStatus.Satisfiable => NetworkAccess.ConstrainedInternet,
            _ => NetworkAccess.Unknown,
        };

        var types = new List<ConnectionType>(3);
        if (wifi)
        {
            types.Add(ConnectionType.WiFi);
        }

        if (cellular)
        {
            types.Add(ConnectionType.Cellular);
        }

        if (wired)
        {
            types.Add(ConnectionType.Ethernet);
        }

        if (other && types.Count == 0)
        {
            types.Add(ConnectionType.Other);
        }

        var connected = access is NetworkAccess.Internet or NetworkAccess.ConstrainedInternet;
        return new ConnectivitySnapshot
        {
            Access = access,
            ConnectionTypes = access == NetworkAccess.None ? [] : types,
            IsMetered = connected ? isExpensive : null,
            IsDataSaverEnabled = connected ? isConstrained : null,
            ObservedAt = observedAt,
        };
    }

    /// <summary>Returns whether two snapshots describe the same connectivity (ignoring the observation time).</summary>
    public static bool IsEquivalent(ConnectivitySnapshot left, ConnectivitySnapshot right) =>
        left.Access == right.Access &&
        left.IsMetered == right.IsMetered &&
        left.IsDataSaverEnabled == right.IsDataSaverEnabled &&
        left.ConnectionTypes.SequenceEqual(right.ConnectionTypes);
}
