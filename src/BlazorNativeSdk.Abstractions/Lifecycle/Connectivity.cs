namespace BlazorNativeSdk.Lifecycle;

/// <summary>The level of network access.</summary>
public enum NetworkAccess
{
    /// <summary>Unknown.</summary>
    Unknown,

    /// <summary>No connectivity.</summary>
    None,

    /// <summary>Local network only.</summary>
    Local,

    /// <summary>Limited internet access (for example a captive portal).</summary>
    ConstrainedInternet,

    /// <summary>Internet access.</summary>
    Internet,
}

/// <summary>Network connection types.</summary>
public enum ConnectionType
{
    /// <summary>Unknown.</summary>
    Unknown,

    /// <summary>Wi-Fi.</summary>
    WiFi,

    /// <summary>Cellular.</summary>
    Cellular,

    /// <summary>Wired Ethernet.</summary>
    Ethernet,

    /// <summary>Bluetooth tethering.</summary>
    Bluetooth,

    /// <summary>Other.</summary>
    Other,
}

/// <summary>A connectivity observation.</summary>
public sealed record ConnectivitySnapshot
{
    /// <summary>An unknown connectivity state.</summary>
    public static ConnectivitySnapshot Unknown { get; } = new() { Access = NetworkAccess.Unknown };

    /// <summary>The level of access.</summary>
    public required NetworkAccess Access { get; init; }

    /// <summary>The active connection types.</summary>
    public IReadOnlyList<ConnectionType> ConnectionTypes { get; init; } = [];

    /// <summary>Whether the connection is metered, when known.</summary>
    public bool? IsMetered { get; init; }

    /// <summary>Whether the user asked to reduce data usage (iOS Low Data Mode, Android Data Saver, browser Save-Data), when known.</summary>
    public bool? IsDataSaverEnabled { get; init; }

    /// <summary>When this snapshot was observed.</summary>
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary><see langword="true"/> when internet access is reported.</summary>
    public bool IsOnline => Access == NetworkAccess.Internet;
}

/// <summary>Connectivity change event data.</summary>
public sealed class ConnectivityChangedEventArgs : EventArgs
{
    /// <summary>Creates event data.</summary>
    public ConnectivityChangedEventArgs(ConnectivitySnapshot previous, ConnectivitySnapshot current)
    {
        Previous = previous;
        Current = current;
    }

    /// <summary>The previous snapshot.</summary>
    public ConnectivitySnapshot Previous { get; }

    /// <summary>The current snapshot.</summary>
    public ConnectivitySnapshot Current { get; }

    /// <summary><see langword="true"/> when the app went from offline to online.</summary>
    public bool CameOnline => !Previous.IsOnline && Current.IsOnline;
}

/// <summary>Observes network connectivity.</summary>
/// <remarks>
/// "Online" means the OS believes internet access exists; it does not guarantee that a specific server is reachable.
/// Always handle request failures.
/// </remarks>
public interface IConnectivityService : INativeCapability
{
    /// <summary>The latest snapshot.</summary>
    ConnectivitySnapshot Current { get; }

    /// <summary>Raised when connectivity changes.</summary>
    event EventHandler<ConnectivityChangedEventArgs>? Changed;
}
