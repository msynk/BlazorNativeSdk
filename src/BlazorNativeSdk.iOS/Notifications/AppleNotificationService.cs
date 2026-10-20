using System.Globalization;
using BlazorNativeSdk.Devices;
using Microsoft.Extensions.Logging;
using UserNotifications;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary><see cref="INotificationService"/> backed by <c>UNUserNotificationCenter</c>.</summary>
/// <remarks>
/// <para>
/// <see cref="ShowAsync"/> and <see cref="ScheduleAsync"/> do not prompt: call <see cref="RequestPermissionAsync"/> first. iOS drops
/// notifications silently without authorization, so the adapter returns <see cref="NativeResultStatus.PermissionDenied"/>
/// instead. Scheduled notifications use a calendar trigger in the user's local time: one-time schedules fire at the local date
/// and time of <see cref="NotificationSchedule.DeliverAt"/>, daily and weekly schedules at the same local time of day (and
/// weekday). <see cref="NotificationSchedule.Exact"/> is ignored; iOS delivers on time.
/// </para>
/// <para>
/// Actions are registered as notification categories keyed by a hash of the action set, merged with categories registered by
/// the app. <see cref="NotificationImportance.High"/> maps to the time-sensitive interruption level, which requires the
/// <c>com.apple.developer.usernotifications.time-sensitive</c> entitlement (otherwise iOS delivers it as active);
/// <see cref="NotificationImportance.Low"/> maps to passive. Channels do not exist on Apple platforms, so
/// <see cref="CreateChannelAsync"/> is a successful no-op. iOS keeps at most 64 pending scheduled notifications per app.
/// </para>
/// </remarks>
public sealed partial class AppleNotificationService : INotificationService
{
    private readonly SemaphoreSlim _categoryLock = new(1, 1);
    private readonly ILogger<AppleNotificationService> _logger;

    /// <summary>Creates the service.</summary>
    public AppleNotificationService(ILogger<AppleNotificationService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.Notifications;

    private static UNUserNotificationCenter Center => UNUserNotificationCenter.Current;

    /// <inheritdoc />
    public async ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var settings = await Center.GetNotificationSettingsAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        var features = new List<string>
        {
            NotificationFeatures.Scheduled,
            NotificationFeatures.Repeating,
            NotificationFeatures.Actions,
            NotificationFeatures.TextInput,
            NotificationFeatures.Badges,
        };
        if (Center.Delegate is NativeSdkNotificationCenterDelegate)
        {
            features.Add(NotificationFeatures.ActivationRouting);
        }

        var adapter = AppleHost.AdapterName;
        return AppleStatusMapping.FromNotifications(settings.AuthorizationStatus) switch
        {
            PermissionStatus.NotDetermined => CapabilityStatus.RequiresPermission(Capability, adapter, features),
            PermissionStatus.Denied => CapabilityStatus.PermissionDenied(Capability, "The user turned notifications off for this app in Settings.", adapter),
            PermissionStatus.Limited => CapabilityStatus.Available(Capability, adapter, features) with
            {
                Reason = "Provisional authorization: notifications are delivered quietly to Notification Center.",
            },
            _ => CapabilityStatus.Available(Capability, adapter, features),
        };
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<PermissionStatus>> RequestPermissionAsync(CancellationToken cancellationToken = default) =>
        NativeOperation.RunAsync<PermissionStatus>(Capability, async token =>
            await ApplePermissionService.RequestNotificationsAsync(token).ConfigureAwait(false), cancellationToken);

    /// <inheritdoc />
    public ValueTask<NativeResult> ShowAsync(LocalNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return NativeOperation.RunAsync(Capability, token => PostAsync(notification, schedule: null, token), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<NativeResult> ScheduleAsync(LocalNotification notification, NotificationSchedule schedule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(schedule);
        return NativeOperation.RunAsync(Capability, token => PostAsync(notification, schedule, token), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<NativeResult> CancelAsync(string notificationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(notificationId);
        Center.RemovePendingNotificationRequests([notificationId]);
        Center.RemoveDeliveredNotifications([notificationId]);
        return ValueTask.FromResult(NativeResult.Ok);
    }

    /// <inheritdoc />
    public ValueTask<NativeResult> CancelAllAsync(CancellationToken cancellationToken = default)
    {
        Center.RemoveAllPendingNotificationRequests();
        Center.RemoveAllDeliveredNotifications();
        return ValueTask.FromResult(NativeResult.Ok);
    }

    /// <inheritdoc />
    /// <remarks>Notification channels are an Android concept; this is a successful no-op.</remarks>
    public ValueTask<NativeResult> CreateChannelAsync(NotificationChannel channel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return ValueTask.FromResult(NativeResult.Ok);
    }

    private async ValueTask<NativeResult> PostAsync(LocalNotification notification, NotificationSchedule? schedule, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(notification.Id) || string.IsNullOrWhiteSpace(notification.Title))
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "LocalNotification.Id and Title are required.");
        }

        var settings = await Center.GetNotificationSettingsAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        var status = AppleStatusMapping.FromNotifications(settings.AuthorizationStatus);
        if (status is PermissionStatus.NotDetermined or PermissionStatus.Denied)
        {
            return NativeError.PermissionDenied(Capability, NativePermissions.Notifications, status,
                status == PermissionStatus.NotDetermined
                    ? "Notification permission has not been requested yet. Call RequestPermissionAsync first."
                    : "The user turned notifications off for this app. Direct the user to the app's page in Settings.");
        }

        using var content = new UNMutableNotificationContent
        {
            Title = notification.Title,
            Body = notification.Body ?? string.Empty,
            UserInfo = ToNSDictionary(AppleNotificationMapping.EncodeUserInfo(notification)),
            InterruptionLevel = notification.Importance switch
            {
                NotificationImportance.Low => UNNotificationInterruptionLevel.Passive2,
                NotificationImportance.High => UNNotificationInterruptionLevel.TimeSensitive2,
                _ => UNNotificationInterruptionLevel.Active2,
            },
        };
        if (!string.IsNullOrEmpty(notification.Subtitle))
        {
            content.Subtitle = notification.Subtitle;
        }

        if (!string.IsNullOrEmpty(notification.Group))
        {
            content.ThreadIdentifier = notification.Group;
        }

        if (notification.BadgeNumber is { } badge)
        {
            content.Badge = NSNumber.FromInt32(Math.Max(0, badge));
        }

        if (!notification.Silent)
        {
            content.Sound = UNNotificationSound.Default;
        }

        if (AppleNotificationMapping.GetCategoryId(notification.Actions) is { } categoryId)
        {
            await EnsureCategoryAsync(categoryId, notification.Actions, cancellationToken).ConfigureAwait(false);
            content.CategoryIdentifier = categoryId;
        }

        using var trigger = CreateTrigger(schedule);
        using var request = UNNotificationRequest.FromIdentifier(notification.Id, content, trigger);
        try
        {
            await Center.AddNotificationRequestAsync(request).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (NSErrorException ex)
        {
            LogPostFailed(_logger, (long)ex.Error.Code);
            return NativeError.Failed(Capability, NativeErrorCodes.PlatformError, ex.Error.LocalizedDescription, ex) with
            {
                Details = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["domain"] = ex.Error.Domain,
                    ["code"] = ((long)ex.Error.Code).ToString(CultureInfo.InvariantCulture),
                },
            };
        }

        LogPosted(_logger, schedule is null ? "immediate" : schedule.Repeat.ToString(), notification.Actions.Count);
        return NativeResult.Ok;
    }

    private static UNCalendarNotificationTrigger? CreateTrigger(NotificationSchedule? schedule)
    {
        if (schedule is null || AppleNotificationMapping.IsImmediate(schedule, DateTimeOffset.Now))
        {
            return null;
        }

        var components = AppleNotificationMapping.GetCalendarComponents(schedule, TimeZoneInfo.Local);
        using var dateComponents = new NSDateComponents
        {
            Hour = components.Hour,
            Minute = components.Minute,
            Second = components.Second,
        };
        if (components.Year is { } year && components.Month is { } month && components.Day is { } day)
        {
            dateComponents.Year = year;
            dateComponents.Month = month;
            dateComponents.Day = day;
        }

        if (components.Weekday is { } weekday)
        {
            dateComponents.Weekday = weekday;
        }

        return UNCalendarNotificationTrigger.CreateTrigger(dateComponents, components.Repeats);
    }

    private async Task EnsureCategoryAsync(string categoryId, IReadOnlyList<NotificationAction> actions, CancellationToken cancellationToken)
    {
        await _categoryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = (await Center.GetNotificationCategoriesAsync().WaitAsync(cancellationToken).ConfigureAwait(false))?.ToArray() ?? [];
            if (existing.Any(c => c.Identifier == categoryId))
            {
                return;
            }

            var nativeActions = actions.Select(ToNativeAction).ToArray();
            var category = UNNotificationCategory.FromIdentifier(categoryId, nativeActions, [], UNNotificationCategoryOptions.None);
            UNNotificationCategory[] categories = [.. existing, category];
            using var set = new NSSet<UNNotificationCategory>(categories);
            Center.SetNotificationCategories(set);

            // setNotificationCategories applies asynchronously; reading the categories back waits until it took effect so the
            // actions are present on a notification posted right after.
            _ = await Center.GetNotificationCategoriesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            LogCategoryRegistered(_logger, categoryId, actions.Count);
        }
        finally
        {
            _categoryLock.Release();
        }
    }

    private static UNNotificationAction ToNativeAction(NotificationAction action)
    {
        var options = UNNotificationActionOptions.None;
        if (action.OpensApp)
        {
            options |= UNNotificationActionOptions.Foreground;
        }

        if (action.IsDestructive)
        {
            options |= UNNotificationActionOptions.Destructive;
        }

        return action.AcceptsText
            ? UNTextInputNotificationAction.FromIdentifier(action.Id, action.Title, options, action.Title, action.TextPlaceholder ?? string.Empty)
            : UNNotificationAction.FromIdentifier(action.Id, action.Title, options);
    }

    private static NSDictionary ToNSDictionary(Dictionary<string, string> values)
    {
        var keys = values.Keys.Select(k => (NSObject)new NSString(k)).ToArray();
        var objects = values.Values.Select(v => (NSObject)new NSString(v)).ToArray();
        return NSDictionary.FromObjectsAndKeys(objects, keys);
    }

    [LoggerMessage(EventId = 3701, Level = LogLevel.Debug, Message = "Posted a notification ({Trigger}, {ActionCount} action(s)).")]
    private static partial void LogPosted(ILogger logger, string trigger, int actionCount);

    [LoggerMessage(EventId = 3702, Level = LogLevel.Warning, Message = "Posting a notification failed with code {Code}.")]
    private static partial void LogPostFailed(ILogger logger, long code);

    [LoggerMessage(EventId = 3703, Level = LogLevel.Debug, Message = "Registered notification category {Category} with {ActionCount} action(s).")]
    private static partial void LogCategoryRegistered(ILogger logger, string category, int actionCount);
}
