using BlazorNativeSdk.Lifecycle;
using CoreFoundation;
using Microsoft.Extensions.Logging;
using Network;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary><see cref="IConnectivityService"/> backed by Network.framework's <c>NWPathMonitor</c>.</summary>
/// <remarks>
/// <see cref="ConnectivitySnapshot.IsMetered"/> reflects <c>NWPath.isExpensive</c> (cellular, personal hotspot) and
/// <see cref="ConnectivitySnapshot.IsDataSaverEnabled"/> reflects <c>NWPath.isConstrained</c> (Low Data Mode).
/// <see cref="IConnectivityService.Changed"/> is raised on a background thread. Until the monitor reports its first path (within
/// milliseconds of start-up) the snapshot is <see cref="ConnectivitySnapshot.Unknown"/>.
/// </remarks>
public sealed partial class AppleConnectivityService : IConnectivityService, IDisposable, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly DispatchQueue _queue = new("blazornativesdk.connectivity");
    private readonly NWPathMonitor _monitor = new();
    private readonly ILogger<AppleConnectivityService> _logger;
    private ConnectivitySnapshot _current = ConnectivitySnapshot.Unknown;
    private int _disposed;

    /// <summary>Creates the service and starts monitoring.</summary>
    public AppleConnectivityService(ILogger<AppleConnectivityService> logger)
    {
        _logger = logger;
        _monitor.SetQueue(_queue);
        _monitor.SnapshotHandler = OnPathChanged;
        _monitor.Start();
    }

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.Connectivity;

    /// <inheritdoc />
    public ConnectivitySnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<ConnectivityChangedEventArgs>? Changed;

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Available(Capability, AppleHost.AdapterName, "metered", "data-saver"));

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _monitor.Cancel();
        _monitor.Dispose();
        _queue.Dispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void OnPathChanged(NWPath path)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var snapshot = AppleConnectivityMapping.ToSnapshot(
            path.Status,
            path.UsesInterfaceType(NWInterfaceType.Wifi),
            path.UsesInterfaceType(NWInterfaceType.Cellular),
            path.UsesInterfaceType(NWInterfaceType.Wired),
            path.UsesInterfaceType(NWInterfaceType.Other),
            path.IsExpensive,
            path.IsConstrained,
            DateTimeOffset.UtcNow);

        ConnectivitySnapshot previous;
        lock (_gate)
        {
            previous = _current;
            if (AppleConnectivityMapping.IsEquivalent(previous, snapshot))
            {
                return;
            }

            _current = snapshot;
        }

        LogChanged(_logger, previous.Access, snapshot.Access);
        try
        {
            Changed?.Invoke(this, new ConnectivityChangedEventArgs(previous, snapshot));
        }
        catch (Exception ex)
        {
            LogHandlerFailed(_logger, ex);
        }
    }

    [LoggerMessage(EventId = 3951, Level = LogLevel.Debug, Message = "Connectivity changed from {Previous} to {Current}.")]
    private static partial void LogChanged(ILogger logger, NetworkAccess previous, NetworkAccess current);

    [LoggerMessage(EventId = 3952, Level = LogLevel.Error, Message = "A connectivity change handler failed.")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception);
}
