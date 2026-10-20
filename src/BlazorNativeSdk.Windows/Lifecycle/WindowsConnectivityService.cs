using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.Logging;
using Windows.Networking.Connectivity;

namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>Maps WinRT network information to <see cref="ConnectivitySnapshot"/> values (pure, unit-tested).</summary>
internal static class ConnectivityMapper
{
    private const uint IanaEthernet = 6;
    private const uint IanaWifi = 71;
    private const uint IanaMobileBroadband = 243;
    private const uint IanaMobileBroadbandCdma = 244;

    public static NetworkAccess MapAccess(NetworkConnectivityLevel level) => level switch
    {
        NetworkConnectivityLevel.None => NetworkAccess.None,
        NetworkConnectivityLevel.LocalAccess => NetworkAccess.Local,
        NetworkConnectivityLevel.ConstrainedInternetAccess => NetworkAccess.ConstrainedInternet,
        NetworkConnectivityLevel.InternetAccess => NetworkAccess.Internet,
        _ => NetworkAccess.Unknown,
    };

    /// <summary>Fixed and variable (pay-per-use) plans are metered; unrestricted ones are not; unknown stays unknown.</summary>
    public static bool? IsMetered(NetworkCostType costType) => costType switch
    {
        NetworkCostType.Unrestricted => false,
        NetworkCostType.Fixed or NetworkCostType.Variable => true,
        _ => null,
    };

    public static ConnectionType MapConnectionType(bool isWlan, bool isWwan, uint? ianaInterfaceType) =>
        isWlan ? ConnectionType.WiFi
        : isWwan ? ConnectionType.Cellular
        : ianaInterfaceType switch
        {
            IanaEthernet => ConnectionType.Ethernet,
            IanaWifi => ConnectionType.WiFi,
            IanaMobileBroadband or IanaMobileBroadbandCdma => ConnectionType.Cellular,
            null => ConnectionType.Unknown,
            _ => ConnectionType.Other,
        };
}

/// <summary>
/// <see cref="IConnectivityService"/> over <see cref="NetworkInformation"/>: the internet connection profile's connectivity
/// level, cost (metered) and background data restriction (data saver), refreshed on <c>NetworkStatusChanged</c>.
/// Unlike the Core fallback it detects captive portals (constrained internet) and metered connections.
/// </summary>
internal sealed partial class WindowsConnectivityService : IConnectivityService, IDisposable, IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly NetworkStatusChangedEventHandler _statusChanged;
    private ConnectivitySnapshot? _current;
    private EventHandler<ConnectivityChangedEventArgs>? _changed;
    private bool _subscribed;
    private int _disposed;

    public WindowsConnectivityService(ILogger<WindowsConnectivityService> logger, TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _statusChanged = OnNetworkStatusChanged;
    }

    public CapabilityId Capability => NativeCapabilities.Connectivity;

    public ConnectivitySnapshot Current
    {
        get
        {
            EnsureSubscribed();
            lock (_gate)
            {
                if (_current is not null)
                {
                    return _current;
                }
            }

            var snapshot = Probe();
            lock (_gate)
            {
                return _current ??= snapshot;
            }
        }
    }

    public event EventHandler<ConnectivityChangedEventArgs>? Changed
    {
        add
        {
            EnsureSubscribed();
            lock (_gate)
            {
                _changed += value;
            }
        }

        remove
        {
            lock (_gate)
            {
                _changed -= value;
            }
        }
    }

    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Available(Capability, WindowsAdapter.Name, "change-notification", "metered", "data-saver", "captive-portal"));

    /// <summary>Re-reads the network state and raises <see cref="Changed"/> when it differs.</summary>
    public void Refresh()
    {
        var next = Probe();
        ConnectivityChangedEventArgs? args = null;
        EventHandler<ConnectivityChangedEventArgs>? handler;
        lock (_gate)
        {
            var previous = _current ?? ConnectivitySnapshot.Unknown;
            if (previous.Access != next.Access ||
                previous.IsMetered != next.IsMetered ||
                previous.IsDataSaverEnabled != next.IsDataSaverEnabled ||
                !previous.ConnectionTypes.SequenceEqual(next.ConnectionTypes))
            {
                args = new ConnectivityChangedEventArgs(previous, next);
            }

            _current = next;
            handler = _changed;
        }

        if (args is null)
        {
            return;
        }

        LogChanged(_logger, args.Previous.Access, args.Current.Access);
        try
        {
            handler?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            LogHandlerFailed(_logger, ex);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_subscribed)
            {
                try
                {
                    NetworkInformation.NetworkStatusChanged -= _statusChanged;
                }
                catch (Exception ex)
                {
                    LogSubscriptionFailed(_logger, ex);
                }

                _subscribed = false;
            }

            _changed = null;
        }

        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void EnsureSubscribed()
    {
        if (Volatile.Read(ref _disposed) != 0)
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
                NetworkInformation.NetworkStatusChanged += _statusChanged;
                _subscribed = true;
            }
            catch (Exception ex)
            {
                LogSubscriptionFailed(_logger, ex);
            }
        }
    }

    private void OnNetworkStatusChanged(object sender)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            Refresh();
        }
    }

    private ConnectivitySnapshot Probe()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null)
            {
                return new ConnectivitySnapshot { Access = NetworkAccess.None, ObservedAt = _time.GetUtcNow() };
            }

            var cost = profile.GetConnectionCost();
            uint? ianaType = null;
            try
            {
                ianaType = profile.NetworkAdapter?.IanaInterfaceType;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                // Some virtual adapters do not expose adapter details.
            }

            return new ConnectivitySnapshot
            {
                Access = ConnectivityMapper.MapAccess(profile.GetNetworkConnectivityLevel()),
                ConnectionTypes = [ConnectivityMapper.MapConnectionType(profile.IsWlanConnectionProfile, profile.IsWwanConnectionProfile, ianaType)],
                IsMetered = cost is null ? null : ConnectivityMapper.IsMetered(cost.NetworkCostType),
                IsDataSaverEnabled = cost?.BackgroundDataUsageRestricted,
                ObservedAt = _time.GetUtcNow(),
            };
        }
        catch (Exception ex)
        {
            LogProbeFailed(_logger, ex);
            return ConnectivitySnapshot.Unknown with { ObservedAt = _time.GetUtcNow() };
        }
    }

    [LoggerMessage(EventId = 3801, Level = LogLevel.Information, Message = "Connectivity changed from {Previous} to {Current}.")]
    private static partial void LogChanged(ILogger logger, NetworkAccess previous, NetworkAccess current);

    [LoggerMessage(EventId = 3802, Level = LogLevel.Warning, Message = "Reading the network state failed.")]
    private static partial void LogProbeFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3803, Level = LogLevel.Warning, Message = "Subscribing to network status changes failed.")]
    private static partial void LogSubscriptionFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3804, Level = LogLevel.Error, Message = "A connectivity Changed handler failed.")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception);
}
