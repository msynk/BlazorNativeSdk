using System.Text.Json;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>
/// Intent extras used to carry notification activations, and their encoding. Pure: works on plain values so it can be
/// unit tested without Android.
/// </summary>
internal static class NotificationExtras
{
    public const string NotificationId = "bns.notification.id";
    public const string Route = "bns.notification.route";
    public const string Data = "bns.notification.data";
    public const string ActionId = "bns.notification.action";
    public const string ReplyKey = "bns.notification.reply";
    public const string ScheduledId = "bns.notification.scheduled-id";
    public const string Consumed = "bns.activation.consumed";
    public const string Source = "android";

    /// <summary>Encodes notification data as JSON (empty data is omitted).</summary>
    public static string? EncodeData(IReadOnlyDictionary<string, string>? data) =>
        data is null || data.Count == 0
            ? null
            : JsonSerializer.Serialize(data as Dictionary<string, string> ?? new Dictionary<string, string>(data), AndroidJsonContext.Default.DictionaryStringString);

    /// <summary>Decodes notification data; corrupt or missing data yields an empty dictionary.</summary>
    public static Dictionary<string, string> DecodeData(string? json) =>
        AndroidJsonContext.TryDeserialize(json, AndroidJsonContext.Default.DictionaryStringString) ?? [];

    /// <summary>
    /// Builds a notification activation from extracted extras, or returns <see langword="null"/> when
    /// <paramref name="notificationId"/> is missing.
    /// </summary>
    public static AppActivation? CreateActivation(
        string? notificationId,
        string? actionId,
        string? route,
        string? dataJson,
        string? userText,
        bool isColdStart) =>
        string.IsNullOrEmpty(notificationId)
            ? null
            : new AppActivation
            {
                Kind = ActivationKind.Notification,
                NotificationId = notificationId,
                ActionId = string.IsNullOrEmpty(actionId) ? null : actionId,
                Route = string.IsNullOrWhiteSpace(route) ? null : route,
                Data = DecodeData(dataJson),
                UserText = userText,
                IsColdStart = isColdStart,
                Source = Source,
            };

    /// <summary>Maps notification importance to <c>NotificationManagerCompat.IMPORTANCE_*</c>.</summary>
    public static int ToChannelImportance(NotificationImportance importance) => importance switch
    {
        NotificationImportance.Low => 2,
        NotificationImportance.High => 4,
        _ => 3,
    };

    /// <summary>Maps notification importance to <c>NotificationCompat.PRIORITY_*</c> (used below Android 8.0).</summary>
    public static int ToPriority(NotificationImportance importance) => importance switch
    {
        NotificationImportance.Low => -1,
        NotificationImportance.High => 1,
        _ => 0,
    };
}

/// <summary>Computes delivery times for scheduled and repeating notifications. Pure.</summary>
internal static class NotificationScheduleCalculator
{
    /// <summary>The local wall-clock time of <paramref name="deliverAt"/> in <paramref name="zone"/>.</summary>
    public static DateTime GetAnchor(DateTimeOffset deliverAt, TimeZoneInfo zone) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(deliverAt, zone).DateTime, DateTimeKind.Unspecified);

    /// <summary>
    /// The first delivery: <see cref="NotificationSchedule.DeliverAt"/> for one-shot notifications (immediately when it is
    /// in the past), or the next occurrence of the anchor's local time for repeats.
    /// </summary>
    public static DateTimeOffset GetFirstDelivery(NotificationSchedule schedule, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (schedule.Repeat == NotificationRepeat.None || schedule.DeliverAt > now)
        {
            return schedule.DeliverAt;
        }

        return GetNextDelivery(schedule.Repeat, GetAnchor(schedule.DeliverAt, zone), now, zone) ?? schedule.DeliverAt;
    }

    /// <summary>The next occurrence strictly after <paramref name="after"/>, or <see langword="null"/> for one-shot schedules.</summary>
    public static DateTimeOffset? GetNextDelivery(NotificationRepeat repeat, DateTime anchorLocal, DateTimeOffset after, TimeZoneInfo zone)
    {
        if (repeat == NotificationRepeat.None)
        {
            return null;
        }

        var step = repeat == NotificationRepeat.Weekly ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1);
        var candidate = DateTime.SpecifyKind(anchorLocal, DateTimeKind.Unspecified);
        var nowLocal = DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(after, zone).DateTime, DateTimeKind.Unspecified);
        if (candidate <= nowLocal)
        {
            var steps = ((nowLocal - candidate).Ticks / step.Ticks) + 1;
            candidate = candidate.AddTicks(steps * step.Ticks);
        }

        var instant = ToInstant(candidate, zone);
        while (instant <= after)
        {
            candidate = candidate.Add(step);
            instant = ToInstant(candidate, zone);
        }

        return instant;
    }

    /// <summary>
    /// When re-arming after a reboot, time change or app restart: missed one-shot notifications are delivered immediately,
    /// missed repeats skip to their next occurrence.
    /// </summary>
    public static DateTimeOffset GetRearmTime(ScheduledNotificationRecord record, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Repeat == NotificationRepeat.None)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(record.NextFireUnixMs);
        }

        // Recompute from the anchor so time-zone and DST changes keep the local time of day.
        return GetNextDelivery(record.Repeat, record.AnchorLocal, now, zone) ?? now;
    }

    /// <summary>Converts a local wall-clock time to an instant, skipping DST gaps and taking the earlier instant in overlaps.</summary>
    public static DateTimeOffset ToInstant(DateTime wallClock, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        var guard = 0;
        while (zone.IsInvalidTime(local) && guard++ < 16)
        {
            local = local.AddMinutes(15);
        }

        if (zone.IsAmbiguousTime(local))
        {
            return new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max());
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
