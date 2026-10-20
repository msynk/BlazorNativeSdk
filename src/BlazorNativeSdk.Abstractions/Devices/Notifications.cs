namespace BlazorNativeSdk.Devices;

/// <summary>Notification importance (maps to Android channel importance and iOS interruption level).</summary>
public enum NotificationImportance
{
    /// <summary>No sound or visual interruption.</summary>
    Low,

    /// <summary>Default importance.</summary>
    Default,

    /// <summary>Makes a sound and may appear as a heads-up notification.</summary>
    High,
}

/// <summary>A button shown on a notification.</summary>
public sealed record NotificationAction
{
    /// <summary>Creates an action.</summary>
    public NotificationAction(string id, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Id = id;
        Title = title;
    }

    /// <summary>An identifier reported back in <see cref="Lifecycle.AppActivation.ActionId"/>.</summary>
    public string Id { get; }

    /// <summary>The button label.</summary>
    public string Title { get; }

    /// <summary>Bring the app to the foreground when the action is chosen.</summary>
    public bool OpensApp { get; init; } = true;

    /// <summary>Render the action as destructive where supported (iOS).</summary>
    public bool IsDestructive { get; init; }

    /// <summary>Let the user type a reply, reported in <see cref="Lifecycle.AppActivation.UserText"/>.</summary>
    public bool AcceptsText { get; init; }

    /// <summary>The placeholder for text input actions.</summary>
    public string? TextPlaceholder { get; init; }
}

/// <summary>A local notification.</summary>
public sealed record LocalNotification
{
    /// <summary>A stable identifier used to update or cancel the notification.</summary>
    public required string Id { get; init; }

    /// <summary>The title.</summary>
    public required string Title { get; init; }

    /// <summary>The body text.</summary>
    public string? Body { get; init; }

    /// <summary>A subtitle (iOS) or sub-text (Android).</summary>
    public string? Subtitle { get; init; }

    /// <summary>
    /// The app route to open when the user taps the notification, such as <c>/orders/42</c>.
    /// Routed through <see cref="Lifecycle.IAppActivationService"/> so cold-start taps are delivered once the UI is ready.
    /// </summary>
    public string? Route { get; init; }

    /// <summary>Custom data delivered back on activation.</summary>
    public IReadOnlyDictionary<string, string> Data { get; init; } = new Dictionary<string, string>();

    /// <summary>Action buttons. Check the <see cref="NotificationFeatures.Actions"/> feature.</summary>
    public IReadOnlyList<NotificationAction> Actions { get; init; } = [];

    /// <summary>The Android channel id. Ignored elsewhere.</summary>
    public string? ChannelId { get; init; }

    /// <summary>Groups related notifications (Android group key, iOS thread identifier).</summary>
    public string? Group { get; init; }

    /// <summary>The importance.</summary>
    public NotificationImportance Importance { get; init; } = NotificationImportance.Default;

    /// <summary>The app icon badge number to set where supported.</summary>
    public int? BadgeNumber { get; init; }

    /// <summary>Deliver without sound.</summary>
    public bool Silent { get; init; }
}

/// <summary>How a scheduled notification repeats.</summary>
public enum NotificationRepeat
{
    /// <summary>Deliver once.</summary>
    None,

    /// <summary>Every day at the same local time.</summary>
    Daily,

    /// <summary>Every week at the same local day and time.</summary>
    Weekly,
}

/// <summary>When a notification is delivered.</summary>
public sealed record NotificationSchedule
{
    /// <summary>The first delivery time.</summary>
    public required DateTimeOffset DeliverAt { get; init; }

    /// <summary>The repeat interval.</summary>
    public NotificationRepeat Repeat { get; init; }

    /// <summary>
    /// Request exact delivery. Android requires the exact-alarm permission for this and otherwise delivers within an
    /// inexact window; other platforms ignore the flag.
    /// </summary>
    public bool Exact { get; init; }
}

/// <summary>An Android notification channel. Other platforms ignore channels.</summary>
public sealed record NotificationChannel
{
    /// <summary>The channel id.</summary>
    public required string Id { get; init; }

    /// <summary>The user-visible name.</summary>
    public required string Name { get; init; }

    /// <summary>The user-visible description.</summary>
    public string? Description { get; init; }

    /// <summary>The importance.</summary>
    public NotificationImportance Importance { get; init; } = NotificationImportance.Default;
}

/// <summary>Optional features reported by <see cref="INotificationService"/>.</summary>
public static class NotificationFeatures
{
    /// <summary>Scheduling notifications for later delivery while the app is not running.</summary>
    public const string Scheduled = "scheduled";

    /// <summary>Repeating schedules.</summary>
    public const string Repeating = "repeating";

    /// <summary>Action buttons.</summary>
    public const string Actions = "actions";

    /// <summary>Text-input actions.</summary>
    public const string TextInput = "text-input";

    /// <summary>Channels (Android).</summary>
    public const string Channels = "channels";

    /// <summary>App icon badges.</summary>
    public const string Badges = "badges";

    /// <summary>Notification taps are routed to the app (<see cref="Lifecycle.IAppActivationService"/>).</summary>
    public const string ActivationRouting = "activation-routing";
}

/// <summary>Shows and schedules local notifications.</summary>
/// <remarks>
/// Remote (push) notifications require a server and a platform push service and are out of scope for this
/// interface; push adapters publish taps through the same activation pipeline so routing is uniform.
/// Configuration-only operations that have no meaning on a platform (such as channels outside Android) succeed as
/// no-ops; user-visible operations that cannot be performed return <see cref="NativeResultStatus.Unsupported"/>.
/// </remarks>
public interface INotificationService : INativeCapability
{
    /// <summary>Requests permission to post notifications.</summary>
    ValueTask<NativeResult<PermissionStatus>> RequestPermissionAsync(CancellationToken cancellationToken = default);

    /// <summary>Shows a notification immediately, replacing an existing one with the same id.</summary>
    ValueTask<NativeResult> ShowAsync(LocalNotification notification, CancellationToken cancellationToken = default);

    /// <summary>Schedules a notification. Check the <see cref="NotificationFeatures.Scheduled"/> feature.</summary>
    ValueTask<NativeResult> ScheduleAsync(LocalNotification notification, NotificationSchedule schedule, CancellationToken cancellationToken = default);

    /// <summary>Cancels a pending or delivered notification.</summary>
    ValueTask<NativeResult> CancelAsync(string notificationId, CancellationToken cancellationToken = default);

    /// <summary>Cancels all pending and delivered notifications posted by the app.</summary>
    ValueTask<NativeResult> CancelAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates or updates an Android notification channel. A successful no-op on other platforms.</summary>
    ValueTask<NativeResult> CreateChannelAsync(NotificationChannel channel, CancellationToken cancellationToken = default);
}
