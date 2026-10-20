using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace BlazorNativeSdk.Lifecycle;

/// <summary>
/// <see cref="IConnectivityService"/> for desktop hosts based on <see cref="NetworkChange"/> and <see cref="NetworkInterface"/>.
/// It detects whether a non-loopback interface with a gateway is up; it cannot detect captive portals or metered networks.
/// </summary>
[UnsupportedOSPlatform("browser")]
public sealed partial class NetworkInformationConnectivityService : IConnectivityService, IDisposable
{
    private readonly ILogger<NetworkInformationConnectivityService> _logger;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private ConnectivitySnapshot _current;
    private bool _subscribed;

    /// <summary>Creates the service.</summary>
    public NetworkInformationConnectivityService(ILogger<NetworkInformationConnectivityService> logger, TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _current = Probe();
    }

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.Connectivity;

    /// <inheritdoc />
    public ConnectivitySnapshot Current
    {
        get
        {
            EnsureSubscribed();
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<ConnectivityChangedEventArgs>? Changed
    {
        add
        {
            EnsureSubscribed();
            _changed += value;
        }
        remove => _changed -= value;
    }

    private event EventHandler<ConnectivityChangedEventArgs>? _changed;

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Available(Capability, "network-information", "change-notification"));

    /// <inheritdoc />
    public void Dispose()
    {
        if (_subscribed)
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        }
    }

    /// <summary>Re-reads the network state and raises <see cref="Changed"/> when it differs.</summary>
    public void Refresh()
    {
        var next = Probe();
        ConnectivityChangedEventArgs? args = null;
        lock (_gate)
        {
            if (next.Access != _current.Access || !next.ConnectionTypes.SequenceEqual(_current.ConnectionTypes))
            {
                args = new ConnectivityChangedEventArgs(_current, next);
            }

            _current = next;
        }

        if (args is not null)
        {
            LogChanged(_logger, args.Previous.Access, args.Current.Access);
            _changed?.Invoke(this, args);
        }
    }

    private void EnsureSubscribed()
    {
        if (_subscribed)
        {
            return;
        }

        lock (_gate)
        {
            if (_subscribed)
            {
                return;
            }

            try
            {
                NetworkChange.NetworkAddressChanged += OnNetworkChanged;
                NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
            }
            catch (PlatformNotSupportedException)
            {
            }

            _subscribed = true;
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => Refresh();

    private ConnectivitySnapshot Probe()
    {
        try
        {
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                return new ConnectivitySnapshot { Access = NetworkAccess.None, ObservedAt = _time.GetUtcNow() };
            }

            var types = new SortedSet<ConnectionType>();
            var hasGateway = false;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                types.Add(nic.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => ConnectionType.WiFi,
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => ConnectionType.Ethernet,
                    NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 or NetworkInterfaceType.Ppp => ConnectionType.Cellular,
                    _ => ConnectionType.Other,
                });

                try
                {
                    hasGateway |= nic.GetIPProperties().GatewayAddresses.Count > 0;
                }
                catch (NetworkInformationException)
                {
                }
                catch (PlatformNotSupportedException)
                {
                    hasGateway = true;
                }
            }

            return new ConnectivitySnapshot
            {
                Access = types.Count == 0 ? NetworkAccess.None : hasGateway ? NetworkAccess.Internet : NetworkAccess.Local,
                ConnectionTypes = [.. types],
                ObservedAt = _time.GetUtcNow(),
            };
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            return ConnectivitySnapshot.Unknown;
        }
    }

    [LoggerMessage(EventId = 1301, Level = LogLevel.Information, Message = "Connectivity changed from {Previous} to {Current}.")]
    private static partial void LogChanged(ILogger logger, NetworkAccess previous, NetworkAccess current);
}
