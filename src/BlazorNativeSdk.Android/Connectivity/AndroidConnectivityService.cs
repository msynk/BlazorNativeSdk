using Android.Content;
using Android.Net;
using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;
using MauiConnectivity = Microsoft.Maui.Networking.Connectivity;
using NetworkAccess = BlazorNativeSdk.Lifecycle.NetworkAccess;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>
/// <see cref="IConnectivityService"/> for Android: Microsoft.Maui.Essentials <c>Connectivity</c> for access and
/// transports, plus <see cref="ConnectivityManager"/> for metered networks and Data Saver.
/// </summary>
/// <remarks>
/// Requires the <c>ACCESS_NETWORK_STATE</c> permission (normal, granted at install). Without it the status is
/// <see cref="CapabilityAvailability.Unavailable"/> with <see cref="NativeErrorCodes.MissingDeclaration"/> and
/// <see cref="Current"/> stays <see cref="ConnectivitySnapshot.Unknown"/>. Data Saver changes are observed while the app
/// process runs (<c>ACTION_RESTRICT_BACKGROUND_CHANGED</c>).
/// </remarks>
public sealed partial class AndroidConnectivityService : IConnectivityService, IDisposable, IAsyncDisposable
{
    private const string AdapterName = "android";
    private readonly Lock _gate = new();
    private readonly ILogger<AndroidConnectivityService> _logger;
    private readonly TimeProvider _time;
    private ConnectivitySnapshot? _current;
    private DataSaverReceiver? _dataSaverReceiver;
    private bool _subscribed;
    private int _disposed;

    /// <summary>Creates the service.</summary>
    public AndroidConnectivityService(ILogger<AndroidConnectivityService> logger, TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
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
                return _current ??= Probe();
            }
        }
    }

    /// <inheritdoc />
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

    private event EventHandler<ConnectivityChangedEventArgs>? _changed;

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!IsDeclared())
        {
            return ValueTask.FromResult(CapabilityStatus.FromError(
                AndroidPermissionErrors.MissingDeclaration(Capability, AndroidPermissionCatalog.AccessNetworkState), AdapterName));
        }

        return ValueTask.FromResult(CapabilityStatus.Available(Capability, AdapterName, "change-notification", "metered", "data-saver"));
    }

    /// <summary>Re-reads the network state and raises <see cref="Changed"/> when it differs.</summary>
    public void Refresh()
    {
        var next = Probe();
        ConnectivityChangedEventArgs? args = null;
        EventHandler<ConnectivityChangedEventArgs>? handler;
        lock (_gate)
        {
            var previous = _current ?? ConnectivitySnapshot.Unknown;
            _current = next;
            if (AndroidConnectivityMapper.HasChanged(previous, next))
            {
                args = new ConnectivityChangedEventArgs(previous, next);
            }

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

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        DataSaverReceiver? receiver;
        bool subscribed;
        lock (_gate)
        {
            receiver = _dataSaverReceiver;
            subscribed = _subscribed;
            _dataSaverReceiver = null;
            _subscribed = false;
            _changed = null;
        }

        if (subscribed)
        {
            MauiConnectivity.Current.ConnectivityChanged -= OnConnectivityChanged;
        }

        if (receiver is not null)
        {
            try
            {
                AndroidContext.App.UnregisterReceiver(receiver);
            }
            catch (Java.Lang.IllegalArgumentException)
            {
                // Not registered.
            }

            receiver.Dispose();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void EnsureSubscribed()
    {
        lock (_gate)
        {
            if (_subscribed || Volatile.Read(ref _disposed) != 0 || !IsDeclared())
            {
                return;
            }

            _subscribed = true;
        }

        try
        {
            MauiConnectivity.Current.ConnectivityChanged += OnConnectivityChanged;
            if (OperatingSystem.IsAndroidVersionAtLeast(24))
            {
                var receiver = new DataSaverReceiver(this);
                using var filter = new IntentFilter(ConnectivityManager.ActionRestrictBackgroundChanged);
                AndroidX.Core.Content.ContextCompat.RegisterReceiver(AndroidContext.App, receiver, filter, AndroidX.Core.Content.ContextCompat.ReceiverNotExported);
                lock (_gate)
                {
                    _dataSaverReceiver = receiver;
                }
            }
        }
        catch (Exception ex) when (ex is PermissionException or Java.Lang.Exception)
        {
            LogSubscribeFailed(_logger, ex);
        }
    }

    private void OnConnectivityChanged(object? sender, Microsoft.Maui.Networking.ConnectivityChangedEventArgs e) => Refresh();

    private ConnectivitySnapshot Probe()
    {
        if (!IsDeclared())
        {
            return ConnectivitySnapshot.Unknown;
        }

        try
        {
            var connectivity = MauiConnectivity.Current;
            var access = AndroidConnectivityMapper.MapAccess(connectivity.NetworkAccess);
            var types = connectivity.ConnectionProfiles.Select(AndroidConnectivityMapper.MapProfile).Distinct().Order().ToArray();
            bool? metered = null;
            bool? dataSaver = null;
            if (AndroidContext.App.GetSystemService(Context.ConnectivityService) is ConnectivityManager manager)
            {
                metered = access is NetworkAccess.None or NetworkAccess.Unknown ? null : manager.IsActiveNetworkMetered;
                if (OperatingSystem.IsAndroidVersionAtLeast(24))
                {
                    dataSaver = AndroidConnectivityMapper.MapDataSaver((int)manager.RestrictBackgroundStatus);
                }
            }

            return new ConnectivitySnapshot
            {
                Access = access,
                ConnectionTypes = types,
                IsMetered = metered,
                IsDataSaverEnabled = dataSaver,
                ObservedAt = _time.GetUtcNow(),
            };
        }
        catch (Exception ex) when (ex is PermissionException or Java.Lang.Exception)
        {
            LogSubscribeFailed(_logger, ex);
            return ConnectivitySnapshot.Unknown;
        }
    }

    private static bool IsDeclared() => Microsoft.Maui.ApplicationModel.Permissions.IsDeclaredInManifest(AndroidPermissionCatalog.AccessNetworkState);

    [LoggerMessage(EventId = 3961, Level = LogLevel.Debug, Message = "Connectivity changed from {Previous} to {Current}.")]
    private static partial void LogChanged(ILogger logger, NetworkAccess previous, NetworkAccess current);

    [LoggerMessage(EventId = 3962, Level = LogLevel.Error, Message = "A connectivity Changed handler failed.")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3963, Level = LogLevel.Warning, Message = "Reading or observing connectivity failed.")]
    private static partial void LogSubscribeFailed(ILogger logger, Exception exception);

    private sealed class DataSaverReceiver : BroadcastReceiver
    {
        private readonly WeakReference<AndroidConnectivityService> _owner;

        public DataSaverReceiver(AndroidConnectivityService owner) => _owner = new WeakReference<AndroidConnectivityService>(owner);

        public override void OnReceive(Context? context, Intent? intent)
        {
            if (_owner.TryGetTarget(out var owner))
            {
                owner.Refresh();
            }
        }
    }
}
