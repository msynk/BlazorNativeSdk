using Android.App;
using Android.Content;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>Arms and disarms the <see cref="AlarmManager"/> alarms that deliver scheduled notifications.</summary>
internal static class NotificationAlarms
{
    private const string AlarmIntentAction = "blazornativesdk.intent.action.NOTIFICATION_ALARM";
    private const string UriScheme = "bns-alarm";

    /// <summary>
    /// Arms the alarm for <paramref name="record"/>. Exact delivery uses <c>setExactAndAllowWhileIdle</c> when requested and
    /// permitted; otherwise <c>setAndAllowWhileIdle</c> lets the OS batch delivery (up to about an hour in Doze).
    /// </summary>
    /// <returns><see langword="true"/> when an exact alarm was set.</returns>
    public static bool Arm(Context context, ScheduledNotificationRecord record)
    {
        if (context.GetSystemService(Context.AlarmService) is not AlarmManager alarms)
        {
            throw new InvalidOperationException("AlarmManager is not available.");
        }

        using var pendingIntent = CreatePendingIntent(context, record.Notification.Id, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)
            ?? throw new InvalidOperationException("Could not create the alarm PendingIntent.");
        if (record.Exact && AndroidPermissionService.CanScheduleExactAlarms())
        {
            try
            {
                alarms.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, record.NextFireUnixMs, pendingIntent);
                return true;
            }
            catch (Java.Lang.SecurityException)
            {
                // The exact-alarm grant was revoked between the check and the call; fall back to an inexact alarm.
            }
        }

        alarms.SetAndAllowWhileIdle(AlarmType.RtcWakeup, record.NextFireUnixMs, pendingIntent);
        return false;
    }

    /// <summary>Cancels the alarm for <paramref name="notificationId"/>, when one is armed.</summary>
    public static void Disarm(Context context, string notificationId)
    {
        using var pendingIntent = CreatePendingIntent(context, notificationId, PendingIntentFlags.NoCreate | PendingIntentFlags.Immutable);
        if (pendingIntent is null)
        {
            return;
        }

        if (context.GetSystemService(Context.AlarmService) is AlarmManager alarms)
        {
            alarms.Cancel(pendingIntent);
        }

        pendingIntent.Cancel();
    }

    private static PendingIntent? CreatePendingIntent(Context context, string notificationId, PendingIntentFlags flags)
    {
        using var intent = new Intent(context, typeof(ScheduledNotificationReceiver));
        intent.SetAction(AlarmIntentAction);
        using var data = Android.Net.Uri.FromParts(UriScheme, notificationId, null);
        intent.SetData(data);
        intent.PutExtra(NotificationExtras.ScheduledId, notificationId);
        return PendingIntent.GetBroadcast(context, StableHash.Compute("alarm", notificationId), intent, flags);
    }
}
