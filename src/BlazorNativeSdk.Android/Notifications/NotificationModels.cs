using System.Text.Json;
using System.Text.Json.Serialization;
using BlazorNativeSdk.Devices;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>A notification persisted for scheduled delivery (so it can be posted after process death or reboot).</summary>
internal sealed class StoredNotification
{
    public required string Id { get; set; }

    public required string Title { get; set; }

    public string? Body { get; set; }

    public string? Subtitle { get; set; }

    public string? Route { get; set; }

    public Dictionary<string, string> Data { get; set; } = [];

    public List<StoredNotificationAction> Actions { get; set; } = [];

    public string? ChannelId { get; set; }

    public string? Group { get; set; }

    public NotificationImportance Importance { get; set; } = NotificationImportance.Default;

    public int? BadgeNumber { get; set; }

    public bool Silent { get; set; }

    public static StoredNotification From(LocalNotification notification) => new()
    {
        Id = notification.Id,
        Title = notification.Title,
        Body = notification.Body,
        Subtitle = notification.Subtitle,
        Route = notification.Route,
        Data = new Dictionary<string, string>(notification.Data, StringComparer.Ordinal),
        Actions = [.. notification.Actions.Select(StoredNotificationAction.From)],
        ChannelId = notification.ChannelId,
        Group = notification.Group,
        Importance = notification.Importance,
        BadgeNumber = notification.BadgeNumber,
        Silent = notification.Silent,
    };
}

/// <summary>A persisted notification action.</summary>
internal sealed class StoredNotificationAction
{
    public required string Id { get; set; }

    public required string Title { get; set; }

    public bool OpensApp { get; set; } = true;

    public bool AcceptsText { get; set; }

    public string? TextPlaceholder { get; set; }

    public bool IsDestructive { get; set; }

    public static StoredNotificationAction From(NotificationAction action) => new()
    {
        Id = action.Id,
        Title = action.Title,
        OpensApp = action.OpensApp,
        AcceptsText = action.AcceptsText,
        TextPlaceholder = action.TextPlaceholder,
        IsDestructive = action.IsDestructive,
    };
}

/// <summary>A scheduled notification and its alarm state.</summary>
internal sealed class ScheduledNotificationRecord
{
    public required StoredNotification Notification { get; set; }

    /// <summary>The next delivery time in Unix milliseconds (UTC).</summary>
    public long NextFireUnixMs { get; set; }

    public NotificationRepeat Repeat { get; set; }

    public bool Exact { get; set; }

    /// <summary>The local wall-clock time of the first delivery; repeats keep this local time across DST and time-zone changes.</summary>
    public DateTime AnchorLocal { get; set; }
}

/// <summary>Notification settings persisted for receivers that run before the service provider exists.</summary>
internal sealed class NotificationSettings
{
    public string? SmallIcon { get; set; }

    public string DefaultChannelId { get; set; } = "bns.default";

    public string DefaultChannelName { get; set; } = "Notifications";

    public string? DefaultChannelDescription { get; set; }

    public NotificationImportance DefaultChannelImportance { get; set; } = NotificationImportance.Default;

    public static NotificationSettings From(AndroidNativeOptions options) => new()
    {
        SmallIcon = options.NotificationSmallIcon,
        DefaultChannelId = options.DefaultChannelId,
        DefaultChannelName = options.DefaultChannelName,
        DefaultChannelDescription = options.DefaultChannelDescription,
        DefaultChannelImportance = options.DefaultChannelImportance,
    };
}

/// <summary>Source-generated JSON metadata for the Android adapter.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(ScheduledNotificationRecord))]
[JsonSerializable(typeof(NotificationSettings))]
internal sealed partial class AndroidJsonContext : JsonSerializerContext
{
    /// <summary>Deserializes JSON, returning <see langword="null"/> for missing or corrupt documents.</summary>
    public static T? TryDeserialize<T>(string? json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
