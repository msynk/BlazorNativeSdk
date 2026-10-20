using System.ComponentModel;
using Android.Content;
using Microsoft.Extensions.Logging;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>
/// Receives notification action buttons that do not open the app (<see cref="Devices.NotificationAction.OpensApp"/> =
/// <see langword="false"/>), including inline text replies, and publishes them as activations. Infrastructure: declared
/// automatically in the app manifest (not exported).
/// </summary>
/// <remarks>
/// The activation is published with <see cref="Lifecycle.AppActivation.IsBackground"/> set, so it is dispatched as soon as the
/// SDK has started, without waiting for any UI, and is never navigated to. Handle it with an
/// <see cref="Lifecycle.IActivationHandler"/> registered through <c>AddActivationHandler&lt;T&gt;()</c>: such handlers exist
/// even when Android starts the process only for this broadcast. Keep the work short (a broadcast receiver has about
/// 10 seconds); schedule longer work with <see cref="Lifecycle.IBackgroundWorkScheduler"/>.
/// </remarks>
[BroadcastReceiver(Name = "blazornativesdk.androidplatform.NotificationActionReceiver", Exported = false, Enabled = true)]
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class NotificationActionReceiver : BroadcastReceiver
{
    /// <inheritdoc />
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (intent is null)
        {
            return;
        }

        try
        {
            // Publishes the activation and removes the notification (inline replies otherwise keep a progress spinner).
            AndroidActivation.HandleIntentCore(intent, isColdStart: false, isBackground: true);
        }
        catch (Exception ex)
        {
            NotificationReceiverLog.Failed(AndroidServices.CreateLogger(typeof(NotificationActionReceiver).FullName!), ex);
        }
    }
}

/// <summary>
/// Posts scheduled notifications when their alarm fires and re-arms repeating ones. Infrastructure: declared automatically
/// in the app manifest (not exported).
/// </summary>
[BroadcastReceiver(Name = "blazornativesdk.androidplatform.ScheduledNotificationReceiver", Exported = false, Enabled = true)]
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ScheduledNotificationReceiver : BroadcastReceiver
{
    /// <inheritdoc />
    public override void OnReceive(Context? context, Intent? intent)
    {
        var id = intent?.GetStringExtra(NotificationExtras.ScheduledId);
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        var logger = AndroidServices.CreateLogger(typeof(ScheduledNotificationReceiver).FullName!);
        try
        {
            ScheduledNotifications.Deliver(context ?? AndroidContext.App, id, DateTimeOffset.UtcNow, logger);
        }
        catch (Exception ex)
        {
            NotificationReceiverLog.Failed(logger, ex);
        }
    }
}

/// <summary>
/// Re-arms scheduled notifications after a reboot, an app update, a clock or time-zone change, or when the exact-alarm
/// permission is granted. Requires <c>RECEIVE_BOOT_COMPLETED</c> in the app manifest. Infrastructure (not exported; the
/// system can still deliver its protected broadcasts).
/// </summary>
[BroadcastReceiver(Name = "blazornativesdk.androidplatform.BootCompletedReceiver", Exported = false, Enabled = true)]
[Android.App.IntentFilter([
    Intent.ActionBootCompleted,
    Intent.ActionMyPackageReplaced,
    Intent.ActionTimeChanged,
    Intent.ActionTimezoneChanged,
    "android.app.action.SCHEDULE_EXACT_ALARM_PERMISSION_STATE_CHANGED",
])]
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class BootCompletedReceiver : BroadcastReceiver
{
    /// <inheritdoc />
    public override void OnReceive(Context? context, Intent? intent)
    {
        var logger = AndroidServices.CreateLogger(typeof(BootCompletedReceiver).FullName!);
        try
        {
            ScheduledNotifications.RearmAll(context ?? AndroidContext.App, DateTimeOffset.UtcNow, logger);
        }
        catch (Exception ex)
        {
            NotificationReceiverLog.Failed(logger, ex);
        }
    }
}

/// <summary>Delivery and re-arming of persisted scheduled notifications.</summary>
internal static partial class ScheduledNotifications
{
    // Serializes delivery and re-arming in this process so a concurrent re-arm cannot resurrect a delivered one-shot.
    private static readonly Lock s_gate = new();

    /// <summary>Posts a due scheduled notification, then re-arms it (repeats) or forgets it (one-shot).</summary>
    public static void Deliver(Context context, string notificationId, DateTimeOffset now, ILogger logger)
    {
        lock (s_gate)
        {
            DeliverCore(context, notificationId, now, logger);
        }
    }

    /// <summary>Re-arms every persisted scheduled notification.</summary>
    /// <returns>The number of alarms armed.</returns>
    public static int RearmAll(Context context, DateTimeOffset now, ILogger logger)
    {
        lock (s_gate)
        {
            return RearmAllCore(context, now, logger);
        }
    }

    private static void DeliverCore(Context context, string notificationId, DateTimeOffset now, ILogger logger)
    {
        var record = NotificationStore.GetScheduled(context, notificationId);
        if (record is null)
        {
            // Cancelled after the alarm was armed.
            return;
        }

        var result = AndroidNotificationPoster.Post(context, record.Notification, NotificationStore.LoadSettings(context));
        if (!result.IsSuccess)
        {
            LogDeliveryFailed(logger, result.Error.Code);
        }

        var next = NotificationScheduleCalculator.GetNextDelivery(record.Repeat, record.AnchorLocal, now, TimeZoneInfo.Local);
        if (next is null)
        {
            NotificationStore.RemoveScheduled(context, notificationId);
            return;
        }

        record.NextFireUnixMs = next.Value.ToUnixTimeMilliseconds();
        NotificationStore.SaveScheduled(context, record);
        NotificationAlarms.Arm(context, record);
    }

    private static int RearmAllCore(Context context, DateTimeOffset now, ILogger logger)
    {
        var count = 0;
        foreach (var record in NotificationStore.GetAllScheduled(context))
        {
            try
            {
                record.NextFireUnixMs = NotificationScheduleCalculator.GetRearmTime(record, now, TimeZoneInfo.Local).ToUnixTimeMilliseconds();
                NotificationStore.SaveScheduled(context, record);
                NotificationAlarms.Arm(context, record);
                count++;
            }
            catch (Exception ex) when (ex is Java.Lang.Exception or InvalidOperationException)
            {
                NotificationReceiverLog.Failed(logger, ex);
            }
        }

        LogRearmed(logger, count);
        return count;
    }

    [LoggerMessage(EventId = 3901, Level = LogLevel.Warning, Message = "A scheduled notification could not be posted ({Code}).")]
    private static partial void LogDeliveryFailed(ILogger logger, string code);

    [LoggerMessage(EventId = 3902, Level = LogLevel.Debug, Message = "Re-armed {Count} scheduled notification(s).")]
    private static partial void LogRearmed(ILogger logger, int count);
}

internal static partial class NotificationReceiverLog
{
    [LoggerMessage(EventId = 3903, Level = LogLevel.Error, Message = "A BlazorNativeSdk notification receiver failed.")]
    public static partial void Failed(ILogger logger, Exception exception);
}
