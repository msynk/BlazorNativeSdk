using AndroidX.Core.App;
using BlazorNativeSdk.Devices;
using Microsoft.Extensions.Logging;
using MauiPermissions = Microsoft.Maui.ApplicationModel.Permissions;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>
/// <see cref="INotificationService"/> for Android: <c>NotificationCompat</c> notifications with channels, action buttons,
/// inline replies and <see cref="Android.App.AlarmManager"/> scheduling that survives process death and reboots.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Android 13+ requires <c>POST_NOTIFICATIONS</c> (declare it and call <see cref="RequestPermissionAsync"/>).</item>
/// <item>Taps open the app's launch activity and are routed through <see cref="Lifecycle.IAppActivationService"/> with
/// <see cref="LocalNotification.Route"/>, <see cref="LocalNotification.Data"/> and the chosen action. Actions with
/// <see cref="NotificationAction.OpensApp"/> = <see langword="false"/> are delivered by a broadcast receiver without
/// showing the UI. Android shows at most three action buttons.</item>
/// <item>Scheduled notifications are persisted in private preferences and armed with
/// <c>setExactAndAllowWhileIdle</c> when <see cref="NotificationSchedule.Exact"/> is set and the exact-alarm permission is
/// granted (<see cref="NativePermissions.ExactAlarms"/>), otherwise <c>setAndAllowWhileIdle</c>, which the OS may defer
/// (up to about an hour in Doze). Repeats keep the local time of day across DST and time-zone changes. Alarms are re-armed
/// at boot (requires <c>RECEIVE_BOOT_COMPLETED</c>) and when the SDK starts, because force-stopping the app clears them.</item>
/// <item>On Android 8.0+ importance and sound belong to the channel; <see cref="LocalNotification.Importance"/> is applied as
/// the pre-8.0 priority only. Notifications without <see cref="LocalNotification.ChannelId"/> use the default channel from
/// <see cref="AndroidNativeOptions"/>, created on first use.</item>
/// <item>Badges are launcher-specific (<see cref="LocalNotification.BadgeNumber"/> is passed as the notification number),
/// so the <see cref="NotificationFeatures.Badges"/> feature is not reported.</item>
/// </list>
/// </remarks>
public sealed partial class AndroidNotificationService : INotificationService, INativeSdkStartupTask
{
    private const string AdapterName = "android";
    private static readonly string[] s_features =
    [
        NotificationFeatures.Scheduled,
        NotificationFeatures.Repeating,
        NotificationFeatures.Actions,
        NotificationFeatures.TextInput,
        NotificationFeatures.Channels,
        NotificationFeatures.ActivationRouting,
    ];

    private readonly NotificationSettings _settings;
    private readonly AndroidPermissionService _permissions;
    private readonly ILogger<AndroidNotificationService> _logger;
    private readonly TimeProvider _time;

    /// <summary>Creates the service.</summary>
    public AndroidNotificationService(
        AndroidNativeOptions options,
        AndroidPermissionService permissions,
        ILogger<AndroidNotificationService> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _settings = NotificationSettings.From(options);
        _permissions = permissions;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;

        // Receivers that run in a fresh process read these settings without the service provider.
        NotificationStore.SaveSettings(AndroidContext.App, _settings);
    }

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.Notifications;

    /// <inheritdoc />
    public async ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && !MauiPermissions.IsDeclaredInManifest(AndroidPermissionCatalog.PostNotifications))
        {
            return CapabilityStatus.FromError(EssentialsErrors.MissingDeclaration(Capability, AndroidPermissionCatalog.PostNotifications), AdapterName);
        }

        if (AndroidPermissionService.AreNotificationsEnabled())
        {
            return CapabilityStatus.Available(Capability, AdapterName, s_features);
        }

        var permission = await AndroidPermissionService.CheckForAsync(Capability, NativePermissions.Notifications, cancellationToken).ConfigureAwait(false);
        if (permission.IsSuccess && permission.Value is PermissionStatus.NotDetermined)
        {
            return CapabilityStatus.RequiresPermission(Capability, AdapterName, s_features);
        }

        return CapabilityStatus.PermissionDenied(Capability,
            "Notifications are disabled for this app (permission denied or turned off in system settings). Use IPermissionService.OpenAppSettingsAsync.",
            AdapterName);
    }

    /// <inheritdoc />
    /// <remarks>Prompts for <c>POST_NOTIFICATIONS</c> on Android 13+. Below, returns <see cref="PermissionStatus.NotRequired"/>
    /// or <see cref="PermissionStatus.Denied"/> when the user turned notifications off in settings.</remarks>
    public ValueTask<NativeResult<PermissionStatus>> RequestPermissionAsync(CancellationToken cancellationToken = default) =>
        NativeOperation.RunAsync(Capability, ct => _permissions.RequestForAsync(Capability, NativePermissions.Notifications, ct), cancellationToken);

    /// <inheritdoc />
    public ValueTask<NativeResult> ShowAsync(LocalNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return NativeOperation.RunAsync(Capability, ct =>
        {
            ct.ThrowIfCancellationRequested();
            if (Validate(notification) is { } invalid)
            {
                return ValueTask.FromResult<NativeResult>(invalid);
            }

            var result = AndroidNotificationPoster.Post(AndroidContext.App, StoredNotification.From(notification), _settings);
            if (!result.IsSuccess)
            {
                LogPostFailed(_logger, result.Error.Code);
            }

            return ValueTask.FromResult(result);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<NativeResult> ScheduleAsync(LocalNotification notification, NotificationSchedule schedule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(schedule);
        return NativeOperation.RunAsync(Capability, ct =>
        {
            ct.ThrowIfCancellationRequested();
            if (Validate(notification) is { } invalid)
            {
                return ValueTask.FromResult<NativeResult>(invalid);
            }

            if (!AndroidPermissionService.AreNotificationsEnabled())
            {
                return ValueTask.FromResult<NativeResult>(NativeError.PermissionDenied(Capability, NativePermissions.Notifications, PermissionStatus.Denied,
                    "Notifications are disabled for this app; the scheduled notification would never be shown."));
            }

            var context = AndroidContext.App;
            var now = _time.GetUtcNow();
            var zone = TimeZoneInfo.Local;
            var record = new ScheduledNotificationRecord
            {
                Notification = StoredNotification.From(notification),
                Repeat = schedule.Repeat,
                Exact = schedule.Exact,
                AnchorLocal = NotificationScheduleCalculator.GetAnchor(schedule.DeliverAt, zone),
                NextFireUnixMs = NotificationScheduleCalculator.GetFirstDelivery(schedule, now, zone).ToUnixTimeMilliseconds(),
            };

            NotificationAlarms.Disarm(context, notification.Id);
            NotificationStore.SaveScheduled(context, record);
            var exact = NotificationAlarms.Arm(context, record);
            if (schedule.Exact && !exact)
            {
                LogInexact(_logger, notification.Id);
            }

            return ValueTask.FromResult(NativeResult.Ok);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<NativeResult> CancelAsync(string notificationId, CancellationToken cancellationToken = default) =>
        NativeOperation.RunAsync(Capability, ct =>
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(notificationId))
            {
                return ValueTask.FromResult<NativeResult>(NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "The notification id is required."));
            }

            var context = AndroidContext.App;
            NotificationAlarms.Disarm(context, notificationId);
            NotificationStore.RemoveScheduled(context, notificationId);
            AndroidNotificationPoster.Cancel(context, notificationId);
            return ValueTask.FromResult(NativeResult.Ok);
        }, cancellationToken);

    /// <inheritdoc />
    /// <remarks>Removes every notification posted by the app (including ones not posted through this SDK) and all scheduled ones.</remarks>
    public ValueTask<NativeResult> CancelAllAsync(CancellationToken cancellationToken = default) =>
        NativeOperation.RunAsync(Capability, ct =>
        {
            ct.ThrowIfCancellationRequested();
            var context = AndroidContext.App;
            foreach (var record in NotificationStore.GetAllScheduled(context))
            {
                NotificationAlarms.Disarm(context, record.Notification.Id);
            }

            NotificationStore.RemoveAllScheduled(context);
            using var manager = AndroidNotificationPoster.GetManager(context);
            manager.CancelAll();
            return ValueTask.FromResult(NativeResult.Ok);
        }, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Android lets apps change a channel's name and description after creation, but importance and sound are then
    /// controlled by the user; delete and recreate the channel with a new id to change them.
    /// </remarks>
    public ValueTask<NativeResult> CreateChannelAsync(NotificationChannel channel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return NativeOperation.RunAsync(Capability, ct =>
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(channel.Id) || string.IsNullOrWhiteSpace(channel.Name))
            {
                return ValueTask.FromResult<NativeResult>(NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "Channel id and name are required."));
            }

            using var manager = AndroidNotificationPoster.GetManager(AndroidContext.App);
            AndroidNotificationPoster.CreateChannel(manager, channel.Id, channel.Name, channel.Description, channel.Importance);
            return ValueTask.FromResult(NativeResult.Ok);
        }, cancellationToken);
    }

    /// <summary>Re-arms persisted scheduled notifications (alarms are cleared when the app is force-stopped or updated).</summary>
    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        ScheduledNotifications.RearmAll(AndroidContext.App, _time.GetUtcNow(), _logger);
        return ValueTask.CompletedTask;
    }

    private NativeError? Validate(LocalNotification notification)
    {
        if (string.IsNullOrWhiteSpace(notification.Id) || string.IsNullOrWhiteSpace(notification.Title))
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "Notification id and title are required.");
        }

        if (notification.Actions.Count > 3)
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "Android shows at most three notification actions.");
        }

        return notification.Actions.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != notification.Actions.Count
            ? NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "Notification action ids must be unique.")
            : null;
    }

    [LoggerMessage(EventId = 3904, Level = LogLevel.Warning, Message = "Posting a notification failed ({Code}).")]
    private static partial void LogPostFailed(ILogger logger, string code);

    [LoggerMessage(EventId = 3905, Level = LogLevel.Information,
        Message = "Notification {NotificationId} was scheduled with an inexact alarm because the exact-alarm permission is not granted.")]
    private static partial void LogInexact(ILogger logger, string notificationId);
}
