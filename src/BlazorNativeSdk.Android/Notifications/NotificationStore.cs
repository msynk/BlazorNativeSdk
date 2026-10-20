using System.Text.Json;
using Android.Content;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>
/// Persists notification settings and scheduled notifications in private <c>SharedPreferences</c>, so broadcast receivers
/// can post and re-arm them after process death or a reboot without the app's service provider. Writes use
/// <c>apply()</c>: Android flushes pending writes before a receiver, service or activity finishes.
/// </summary>
internal static class NotificationStore
{
    private const string PreferencesName = "blazornativesdk.notifications";
    private const string SettingsKey = "settings";
    private const string ScheduledPrefix = "scheduled:";
    private static readonly Lock s_gate = new();

    public static void SaveSettings(Context context, NotificationSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, AndroidJsonContext.Default.NotificationSettings);
        lock (s_gate)
        {
            using var editor = Preferences(context).Edit();
            editor?.PutString(SettingsKey, json)?.Apply();
        }
    }

    public static NotificationSettings LoadSettings(Context context)
    {
        lock (s_gate)
        {
            return AndroidJsonContext.TryDeserialize(Preferences(context).GetString(SettingsKey, null), AndroidJsonContext.Default.NotificationSettings)
                ?? new NotificationSettings();
        }
    }

    public static void SaveScheduled(Context context, ScheduledNotificationRecord record)
    {
        var json = JsonSerializer.Serialize(record, AndroidJsonContext.Default.ScheduledNotificationRecord);
        lock (s_gate)
        {
            using var editor = Preferences(context).Edit();
            editor?.PutString(ScheduledPrefix + record.Notification.Id, json)?.Apply();
        }
    }

    public static ScheduledNotificationRecord? GetScheduled(Context context, string notificationId)
    {
        lock (s_gate)
        {
            return AndroidJsonContext.TryDeserialize(
                Preferences(context).GetString(ScheduledPrefix + notificationId, null),
                AndroidJsonContext.Default.ScheduledNotificationRecord);
        }
    }

    public static bool RemoveScheduled(Context context, string notificationId)
    {
        lock (s_gate)
        {
            var preferences = Preferences(context);
            if (!preferences.Contains(ScheduledPrefix + notificationId))
            {
                return false;
            }

            using var editor = preferences.Edit();
            editor?.Remove(ScheduledPrefix + notificationId)?.Apply();
            return true;
        }
    }

    public static List<ScheduledNotificationRecord> GetAllScheduled(Context context)
    {
        lock (s_gate)
        {
            var result = new List<ScheduledNotificationRecord>();
            var all = Preferences(context).All;
            if (all is null)
            {
                return result;
            }

            foreach (var (key, value) in all)
            {
                if (key.StartsWith(ScheduledPrefix, StringComparison.Ordinal) &&
                    AndroidJsonContext.TryDeserialize(value?.ToString(), AndroidJsonContext.Default.ScheduledNotificationRecord) is { } record)
                {
                    result.Add(record);
                }
            }

            return result;
        }
    }

    public static void RemoveAllScheduled(Context context)
    {
        lock (s_gate)
        {
            var preferences = Preferences(context);
            var keys = preferences.All?.Keys.Where(k => k.StartsWith(ScheduledPrefix, StringComparison.Ordinal)).ToList() ?? [];
            using var editor = preferences.Edit();
            if (editor is null)
            {
                return;
            }

            foreach (var key in keys)
            {
                editor.Remove(key);
            }

            editor.Apply();
        }
    }

    // SharedPreferences instances are process-wide singletons; their peers are intentionally not disposed.
    private static ISharedPreferences Preferences(Context context) =>
        context.GetSharedPreferences(PreferencesName, FileCreationMode.Private)
        ?? throw new InvalidOperationException("SharedPreferences are not available.");
}
