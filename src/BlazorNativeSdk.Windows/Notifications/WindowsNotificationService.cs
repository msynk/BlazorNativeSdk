using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Windows.UI.Notifications;
using XmlDocument = Windows.Data.Xml.Dom.XmlDocument;

namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>Maps toast settings to permission states (pure, unit-tested).</summary>
internal static class NotificationSettingMapper
{
    public static PermissionStatus ToPermissionStatus(NotificationSetting setting) => setting switch
    {
        NotificationSetting.Enabled => PermissionStatus.Granted,
        NotificationSetting.DisabledForApplication or NotificationSetting.DisabledForUser => PermissionStatus.Denied,
        NotificationSetting.DisabledByGroupPolicy or NotificationSetting.DisabledByManifest => PermissionStatus.Restricted,
        _ => PermissionStatus.Unknown,
    };
}

/// <summary>
/// <see cref="INotificationService"/> over Windows toast notifications (<see cref="ToastNotificationManager"/>).
/// </summary>
/// <remarks>
/// <para>
/// Unpackaged apps need an Application User Model ID: the service registers it under
/// <c>HKCU\Software\Classes\AppUserModelId</c> (display name and optional icon) and sets it on the process; MSIX-packaged
/// apps use their package identity and skip both steps.
/// </para>
/// <para>
/// Activation: when <see cref="WindowsNativeOptions.NotificationActivationScheme"/> is set, toasts use protocol activation
/// with signed launch URIs, so a click launches the app (or reaches the running instance through single-instance forwarding)
/// and is turned back into a <see cref="ActivationKind.Notification"/> activation. Toasts shown by the running process also
/// deliver clicks in-process through <c>ToastNotification.Activated</c>, which is the only path for typed replies and the
/// only path at all when no scheme is configured. Windows does not repeat scheduled toasts, ignores badges for unpackaged
/// apps and has no channels (channel creation is a successful no-op).
/// </para>
/// </remarks>
internal sealed partial class WindowsNotificationService : INotificationService, IDisposable, IAsyncDisposable
{
    private const string ToastGroup = "bns";
    private const int MaxTagLength = 64;
    private const int MaxTrackedToasts = 64;

    private readonly NativeSdkOptions _sdkOptions;
    private readonly WindowsNativeOptions _options;
    private readonly IActivationPublisher _publisher;
    private readonly ToastActivationKeyStore _keys;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ToastNotification> _tracked = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _trackOrder = new();
    private NotifierState? _state;
    private int _disposed;

    public WindowsNotificationService(
        IOptions<NativeSdkOptions> sdkOptions,
        IOptions<WindowsNativeOptions> options,
        IActivationPublisher publisher,
        ToastActivationKeyStore keys,
        ILogger<WindowsNotificationService> logger,
        TimeProvider? timeProvider = null)
    {
        _sdkOptions = sdkOptions.Value;
        _options = options.Value;
        _publisher = publisher;
        _keys = keys;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public CapabilityId Capability => NativeCapabilities.Notifications;

    /// <summary>The AUMID toasts are posted under.</summary>
    internal string AppUserModelIdValue => AppUserModelId.Normalize(_options.AppUserModelId ?? _sdkOptions.AppId);

    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var state = TryGetState(out var error);
        if (state is null)
        {
            return ValueTask.FromResult(CapabilityStatus.FromError(error!, WindowsAdapter.Name));
        }

        var setting = ReadSetting(state);
        string[] features = [NotificationFeatures.Scheduled, NotificationFeatures.Actions, NotificationFeatures.TextInput, NotificationFeatures.ActivationRouting];
        return ValueTask.FromResult(NotificationSettingMapper.ToPermissionStatus(setting ?? NotificationSetting.Enabled) switch
        {
            PermissionStatus.Granted => CapabilityStatus.Available(Capability, WindowsAdapter.Name, features),
            PermissionStatus.Restricted => CapabilityStatus.PermissionDenied(Capability, $"Notifications are blocked ({setting}).", WindowsAdapter.Name) with { ReasonCode = NativeErrorCodes.PermissionRestricted },
            _ => CapabilityStatus.PermissionDenied(Capability, $"Notifications are turned off in Windows Settings ({setting}).", WindowsAdapter.Name),
        });
    }

    /// <summary>The permission state from the toast notifier's setting (also used by the permission service).</summary>
    internal NativeResult<PermissionStatus> GetPermissionStatus()
    {
        var state = TryGetState(out var error);
        if (state is null)
        {
            return error!;
        }

        var setting = ReadSetting(state);
        return setting is null ? PermissionStatus.Unknown : NotificationSettingMapper.ToPermissionStatus(setting.Value);
    }

    public ValueTask<NativeResult<PermissionStatus>> RequestPermissionAsync(CancellationToken cancellationToken = default)
    {
        // Windows has no runtime prompt for toasts: the user controls them in Settings > System > Notifications.
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(GetPermissionStatus());
    }

    public ValueTask<NativeResult> ShowAsync(LocalNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Run(() =>
        {
            var state = GetStateOrThrow();
            if (EnsureAllowed(state) is { } denied)
            {
                return denied;
            }

            var toast = new ToastNotification(CreateDocument(notification))
            {
                Tag = TagFor(notification.Id),
                Group = ToastGroup,
            };

            if (notification.Importance == NotificationImportance.Low)
            {
                toast.SuppressPopup = true;
            }
            else if (notification.Importance == NotificationImportance.High)
            {
                toast.Priority = ToastNotificationPriority.High;
            }

            Track(toast);
            try
            {
                state.Notifier.Show(toast);
            }
            catch
            {
                Untrack(toast.Tag, toast);
                throw;
            }

            return NativeResult.Ok;
        }));
    }

    public ValueTask<NativeResult> ScheduleAsync(LocalNotification notification, NotificationSchedule schedule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(schedule);
        cancellationToken.ThrowIfCancellationRequested();

        if (schedule.Repeat != NotificationRepeat.None)
        {
            return ValueTask.FromResult<NativeResult>(NativeError.Unsupported(
                Capability,
                "Windows scheduled toasts cannot repeat. Schedule each occurrence separately (for example the next several days)."));
        }

        if (schedule.DeliverAt <= _time.GetUtcNow().AddSeconds(1))
        {
            return ShowAsync(notification, cancellationToken);
        }

        return ValueTask.FromResult(Run(() =>
        {
            var state = GetStateOrThrow();
            if (EnsureAllowed(state) is { } denied)
            {
                return denied;
            }

            var scheduled = new ScheduledToastNotification(CreateDocument(notification), schedule.DeliverAt)
            {
                Tag = TagFor(notification.Id),
                Group = ToastGroup,
                SuppressPopup = notification.Importance == NotificationImportance.Low,
            };

            RemoveScheduled(state, scheduled.Tag);
            state.Notifier.AddToSchedule(scheduled);
            return NativeResult.Ok;
        }));
    }

    public ValueTask<NativeResult> CancelAsync(string notificationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(notificationId);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Run(() =>
        {
            var state = GetStateOrThrow();
            var tag = TagFor(notificationId);
            RemoveScheduled(state, tag);
            IgnoreNotFound(() =>
            {
                if (state.Packaged)
                {
                    ToastNotificationManager.History.Remove(tag, ToastGroup);
                }
                else
                {
                    ToastNotificationManager.History.Remove(tag, ToastGroup, state.AppUserModelId);
                }
            });
            Untrack(tag, null);
            return NativeResult.Ok;
        }));
    }

    public ValueTask<NativeResult> CancelAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Run(() =>
        {
            var state = GetStateOrThrow();
            foreach (var scheduled in state.Notifier.GetScheduledToastNotifications())
            {
                state.Notifier.RemoveFromSchedule(scheduled);
            }

            IgnoreNotFound(() =>
            {
                if (state.Packaged)
                {
                    ToastNotificationManager.History.Clear();
                }
                else
                {
                    ToastNotificationManager.History.Clear(state.AppUserModelId);
                }
            });

            lock (_gate)
            {
                foreach (var toast in _tracked.Values)
                {
                    Detach(toast);
                }

                _tracked.Clear();
                _trackOrder.Clear();
            }

            return NativeResult.Ok;
        }));
    }

    public ValueTask<NativeResult> CreateChannelAsync(NotificationChannel channel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return ValueTask.FromResult(NativeResult.Ok);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var toast in _tracked.Values)
            {
                Detach(toast);
            }

            _tracked.Clear();
            _trackOrder.Clear();
        }

        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>The toast tag for a notification id (ids longer than the 64-character limit are hashed).</summary>
    internal static string TagFor(string notificationId) =>
        notificationId.Length <= MaxTagLength
            ? notificationId
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(notificationId)));

    private XmlDocument CreateDocument(LocalNotification notification)
    {
        var scheme = string.IsNullOrWhiteSpace(_options.NotificationActivationScheme) ? null : _options.NotificationActivationScheme;
        var activation = new ToastActivationOptions(
            scheme,
            scheme is null ? null : _keys.Key.ToArray(),
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22546));
        var document = new XmlDocument();
        document.LoadXml(ToastXmlBuilder.Build(notification, activation));
        return document;
    }

    private NativeResult Run(Func<NativeResult> operation)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        try
        {
            return operation();
        }
        catch (Exception ex)
        {
            LogOperationFailed(_logger, ex);
            return NativeError.FromException(Capability, ex);
        }
    }

    private NativeError? EnsureAllowed(NotifierState state)
    {
        var setting = ReadSetting(state);
        if (setting is null or NotificationSetting.Enabled)
        {
            return null;
        }

        var status = NotificationSettingMapper.ToPermissionStatus(setting.Value);
        return NativeError.PermissionDenied(Capability, NativePermissions.Notifications, status, $"Toast notifications are disabled ({setting}).");
    }

    private NotificationSetting? ReadSetting(NotifierState state)
    {
        try
        {
            return state.Notifier.Setting;
        }
        catch (Exception ex)
        {
            // Unregistered AUMIDs throw "element not found"; the toast itself reports the real outcome.
            LogSettingUnavailable(_logger, ex);
            return null;
        }
    }

    private NotifierState GetStateOrThrow() => TryGetState(out var error) ?? throw error!.ToException();

    private NotifierState? TryGetState(out NativeError? error)
    {
        error = null;
        lock (_gate)
        {
            if (_state is not null)
            {
                return _state;
            }

            try
            {
                var packaged = Interop.PackageInfo.IsPackaged;
                var aumid = AppUserModelIdValue;
                if (!packaged)
                {
                    if (_options.RegisterAppUserModelId)
                    {
                        AppUserModelId.Register(aumid, _sdkOptions.DisplayName, _options.NotificationIconPath);
                    }

                    if (_options.SetProcessAppUserModelId)
                    {
                        AppUserModelId.ApplyToProcess(aumid);
                    }
                }

                var notifier = packaged ? ToastNotificationManager.CreateToastNotifier() : ToastNotificationManager.CreateToastNotifier(aumid);
                _state = new NotifierState(notifier, aumid, packaged);
                return _state;
            }
            catch (Exception ex)
            {
                LogInitializationFailed(_logger, ex);
                error = NativeError.Unavailable(Capability, NativeErrorCodes.PlatformError, "Toast notifications could not be initialized: " + ex.Message) with { Exception = ex };
                return null;
            }
        }
    }

    private static void RemoveScheduled(NotifierState state, string tag)
    {
        foreach (var scheduled in state.Notifier.GetScheduledToastNotifications())
        {
            if (scheduled.Tag == tag && scheduled.Group == ToastGroup)
            {
                state.Notifier.RemoveFromSchedule(scheduled);
            }
        }
    }

    private static void IgnoreNotFound(Action action)
    {
        const int ERROR_NOT_FOUND = unchecked((int)0x80070490);
        try
        {
            action();
        }
        catch (COMException ex) when (ex.HResult == ERROR_NOT_FOUND)
        {
        }
    }

    // ToastNotification objects must stay referenced for their events to fire; keep the most recent ones.
    private void Track(ToastNotification toast)
    {
        toast.Activated += OnActivated;
        toast.Dismissed += OnDismissed;
        toast.Failed += OnFailed;
        lock (_gate)
        {
            if (_tracked.Remove(toast.Tag, out var replaced))
            {
                Detach(replaced);
                _ = _trackOrder.Remove(toast.Tag);
            }

            _tracked[toast.Tag] = toast;
            _ = _trackOrder.AddLast(toast.Tag);
            while (_trackOrder.Count > MaxTrackedToasts && _trackOrder.First is { } oldest)
            {
                _trackOrder.RemoveFirst();
                if (_tracked.Remove(oldest.Value, out var evicted))
                {
                    Detach(evicted);
                }
            }
        }
    }

    private void Untrack(string tag, ToastNotification? expected)
    {
        lock (_gate)
        {
            if (_tracked.TryGetValue(tag, out var current) && (expected is null || ReferenceEquals(current, expected)))
            {
                _ = _tracked.Remove(tag);
                _ = _trackOrder.Remove(tag);
                Detach(current);
            }
        }
    }

    private void Detach(ToastNotification toast)
    {
        try
        {
            toast.Activated -= OnActivated;
            toast.Dismissed -= OnDismissed;
            toast.Failed -= OnFailed;
        }
        catch (Exception ex)
        {
            LogOperationFailed(_logger, ex);
        }
    }

    private void OnActivated(ToastNotification sender, object args)
    {
        try
        {
            var arguments = (args as ToastActivatedEventArgs)?.Arguments;
            if (!ToastLaunchArguments.TryParseArguments(arguments, out var payload) &&
                !(Uri.TryCreate(arguments, UriKind.Absolute, out var uri) &&
                  ToastLaunchArguments.TryParseProtocolUri(uri, _options.NotificationActivationScheme, _keys.Key, out payload)))
            {
                return;
            }

            var activation = new AppActivation
            {
                Kind = ActivationKind.Notification,
                NotificationId = payload.NotificationId,
                ActionId = payload.ActionId,
                Route = payload.Route,
                Data = payload.Data,
                UserText = ReadUserText(args as ToastActivatedEventArgs),
                Source = WindowsAdapter.Name,
            };

            _ = PublishAsync(activation);
        }
        catch (Exception ex)
        {
            LogActivationFailed(_logger, ex);
        }
    }

    private async Task PublishAsync(AppActivation activation)
    {
        try
        {
            await _publisher.PublishAsync(activation).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogActivationFailed(_logger, ex);
        }
    }

    private static string? ReadUserText(ToastActivatedEventArgs? args)
    {
        if (args is null || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
        {
            return null;
        }

        var input = args.UserInput;
        if (input is null)
        {
            return null;
        }

        foreach (var pair in input.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            if (pair.Key.StartsWith(ToastXmlBuilder.ReplyInputIdPrefix, StringComparison.Ordinal) && pair.Value is string { Length: > 0 } text)
            {
                return text;
            }
        }

        return null;
    }

    private void OnDismissed(ToastNotification sender, ToastDismissedEventArgs args)
    {
        // Timed-out toasts move to the notification center and can still be clicked; keep tracking those.
        if (args.Reason != ToastDismissalReason.TimedOut)
        {
            Untrack(sender.Tag, sender);
        }
    }

    private void OnFailed(ToastNotification sender, ToastFailedEventArgs args)
    {
        LogToastFailed(_logger, args.ErrorCode);
        Untrack(sender.Tag, sender);
    }

    [LoggerMessage(EventId = 3901, Level = LogLevel.Warning, Message = "A toast notification operation failed.")]
    private static partial void LogOperationFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3902, Level = LogLevel.Error, Message = "Toast notifications could not be initialized.")]
    private static partial void LogInitializationFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3903, Level = LogLevel.Debug, Message = "The toast notification setting could not be read.")]
    private static partial void LogSettingUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3904, Level = LogLevel.Error, Message = "A toast activation could not be published.")]
    private static partial void LogActivationFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3905, Level = LogLevel.Warning, Message = "Windows failed to show a toast notification.")]
    private static partial void LogToastFailed(ILogger logger, Exception? exception);

    private sealed record NotifierState(ToastNotifier Notifier, string AppUserModelId, bool Packaged);
}
