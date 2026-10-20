using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlazorNativeSdk.Devices;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>A notification payload decoded from <c>userInfo</c>.</summary>
/// <param name="Id">The <see cref="LocalNotification.Id"/>.</param>
/// <param name="Route">The <see cref="LocalNotification.Route"/>.</param>
/// <param name="Data">The <see cref="LocalNotification.Data"/>.</param>
internal sealed record DecodedNotification(string Id, string? Route, IReadOnlyDictionary<string, string> Data);

/// <summary>Local calendar components for a <c>UNCalendarNotificationTrigger</c>.</summary>
/// <param name="Year">The year, for one-time delivery.</param>
/// <param name="Month">The month, for one-time delivery.</param>
/// <param name="Day">The day of the month, for one-time delivery.</param>
/// <param name="Weekday">The weekday (1 = Sunday ... 7 = Saturday), for weekly delivery.</param>
/// <param name="Hour">The hour.</param>
/// <param name="Minute">The minute.</param>
/// <param name="Second">The second.</param>
/// <param name="Repeats">Whether the trigger repeats.</param>
internal readonly record struct NotificationCalendarComponents(int? Year, int? Month, int? Day, int? Weekday, int Hour, int Minute, int Second, bool Repeats);

/// <summary>Pure helpers that translate <see cref="LocalNotification"/>s to and from UserNotifications payloads.</summary>
internal static class AppleNotificationMapping
{
    /// <summary>userInfo key that carries <see cref="LocalNotification.Id"/>.</summary>
    public const string IdKey = "bns.id";

    /// <summary>userInfo key that carries <see cref="LocalNotification.Route"/>.</summary>
    public const string RouteKey = "bns.route";

    /// <summary>userInfo key that carries <see cref="LocalNotification.Data"/> as a JSON object.</summary>
    public const string DataKey = "bns.data";

    /// <summary>Prefix of the notification categories the adapter registers for action sets.</summary>
    public const string CategoryPrefix = "bns.actions.";

    /// <summary>Notifications scheduled less than this far in the future are delivered immediately.</summary>
    public static readonly TimeSpan ImmediateThreshold = TimeSpan.FromSeconds(1);

    /// <summary>Encodes the SDK fields of a notification as string userInfo entries.</summary>
    public static Dictionary<string, string> EncodeUserInfo(LocalNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var userInfo = new Dictionary<string, string>(StringComparer.Ordinal) { [IdKey] = notification.Id };
        if (!string.IsNullOrEmpty(notification.Route))
        {
            userInfo[RouteKey] = notification.Route;
        }

        if (notification.Data.Count > 0)
        {
            userInfo[DataKey] = JsonSerializer.Serialize(new Dictionary<string, string>(notification.Data, StringComparer.Ordinal), AppleJsonContext.Default.DictionaryStringString);
        }

        return userInfo;
    }

    /// <summary>
    /// Decodes userInfo. Notifications that were not posted by the SDK (for example push notifications) keep their request
    /// identifier as the id and expose their top-level string values (except <c>aps</c>) as data.
    /// </summary>
    public static DecodedNotification Decode(IReadOnlyDictionary<string, string> userInfo, string requestIdentifier)
    {
        ArgumentNullException.ThrowIfNull(userInfo);
        var id = userInfo.TryGetValue(IdKey, out var storedId) && !string.IsNullOrEmpty(storedId) ? storedId : requestIdentifier;
        var route = userInfo.TryGetValue(RouteKey, out var storedRoute) && !string.IsNullOrEmpty(storedRoute) ? storedRoute : null;

        Dictionary<string, string> data;
        if (userInfo.TryGetValue(DataKey, out var json))
        {
            data = TryParseData(json) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        else if (userInfo.ContainsKey(IdKey))
        {
            data = new Dictionary<string, string>(StringComparer.Ordinal);
        }
        else
        {
            data = userInfo
                .Where(pair => pair.Key is not "aps" && !pair.Key.StartsWith("bns.", StringComparison.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }

        return new DecodedNotification(id, route, data);
    }

    /// <summary>
    /// A stable category identifier for an action set: notifications with identical actions share one registered category.
    /// Returns <see langword="null"/> when there are no actions.
    /// </summary>
    public static string? GetCategoryId(IReadOnlyList<NotificationAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        if (actions.Count == 0)
        {
            return null;
        }

        var canonical = new StringBuilder();
        foreach (var action in actions)
        {
            canonical.Append(action.Id).Append('\u001f')
                .Append(action.Title).Append('\u001f')
                .Append(action.OpensApp ? '1' : '0')
                .Append(action.IsDestructive ? '1' : '0')
                .Append(action.AcceptsText ? '1' : '0').Append('\u001f')
                .Append(action.TextPlaceholder).Append('\u001e');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return CategoryPrefix + Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    /// <summary>Returns whether a one-time schedule should be delivered immediately (now or in the past).</summary>
    public static bool IsImmediate(NotificationSchedule schedule, DateTimeOffset now) =>
        schedule.Repeat == NotificationRepeat.None && schedule.DeliverAt - now < ImmediateThreshold;

    /// <summary>
    /// Local wall-clock components for a schedule. One-time schedules use the full local date and time; daily schedules match
    /// the time of day; weekly schedules match the weekday and time of day, so repeats follow the user's local time.
    /// </summary>
    public static NotificationCalendarComponents GetCalendarComponents(NotificationSchedule schedule, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(timeZone);
        var local = TimeZoneInfo.ConvertTime(schedule.DeliverAt, timeZone);
        return schedule.Repeat switch
        {
            NotificationRepeat.Daily => new(null, null, null, null, local.Hour, local.Minute, local.Second, true),
            NotificationRepeat.Weekly => new(null, null, null, (int)local.DayOfWeek + 1, local.Hour, local.Minute, local.Second, true),
            _ => new(local.Year, local.Month, local.Day, null, local.Hour, local.Minute, local.Second, false),
        };
    }

    private static Dictionary<string, string>? TryParseData(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, AppleJsonContext.Default.DictionaryStringString);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
