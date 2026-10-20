using Android.Content;
using AndroidX.Core.App;
using BlazorNativeSdk.Devices;
using PendingIntent = Android.App.PendingIntent;
using PendingIntentFlags = Android.App.PendingIntentFlags;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>
/// Builds and posts notifications with <see cref="NotificationCompat"/>. Used by the notification service and by the
/// broadcast receivers that deliver scheduled notifications, so it depends only on a <see cref="Context"/>.
/// </summary>
internal static class AndroidNotificationPoster
{
    /// <summary>The integer id paired with the notification tag (the SDK notification id) in <c>notify(tag, id, ...)</c>.</summary>
    public const int NotificationTagId = 0x0B45;

    private const string ActionIntentAction = "blazornativesdk.intent.action.NOTIFICATION_ACTION";
    private const string UriScheme = "bns-notification";
    private const int SemanticActionReply = 1;
    private const int SemanticActionDelete = 4;

    /// <summary>Posts (or replaces) a notification.</summary>
    public static NativeResult Post(Context context, StoredNotification notification, NotificationSettings settings)
    {
        var capability = NativeCapabilities.Notifications;
        using var manager = GetManager(context);
        if (!manager.AreNotificationsEnabled())
        {
            return NativeError.PermissionDenied(capability, NativePermissions.Notifications, PermissionStatus.Denied,
                "Notifications are disabled for this app (POST_NOTIFICATIONS denied or turned off in system settings).");
        }

        var channel = EnsureChannel(manager, notification.ChannelId, settings);
        if (!channel.IsSuccess)
        {
            return channel.Error;
        }

        using var builder = new NotificationCompat.Builder(context, channel.Value);
        builder.SetSmallIcon(ResolveSmallIcon(context, settings.SmallIcon));
        builder.SetContentTitle(notification.Title);
        if (!string.IsNullOrEmpty(notification.Body))
        {
            builder.SetContentText(notification.Body);
            using var style = new NotificationCompat.BigTextStyle();
            builder.SetStyle(style.BigText(notification.Body));
        }

        if (!string.IsNullOrEmpty(notification.Subtitle))
        {
            builder.SetSubText(notification.Subtitle);
        }

        if (!string.IsNullOrEmpty(notification.Group))
        {
            builder.SetGroup(notification.Group);
        }

        if (notification.BadgeNumber is { } badge)
        {
            builder.SetNumber(badge);
        }

        builder.SetPriority(NotificationExtras.ToPriority(notification.Importance));
        builder.SetSilent(notification.Silent);
        builder.SetAutoCancel(true);
        builder.SetShowWhen(true);
        builder.SetWhen(Java.Lang.JavaSystem.CurrentTimeMillis());

        if (CreateActivityIntent(context, notification, actionId: null, mutable: false) is { } contentIntent)
        {
            builder.SetContentIntent(contentIntent);
        }

        foreach (var action in notification.Actions)
        {
            if (CreateAction(context, notification, action) is { } built)
            {
                builder.AddAction(built);
            }
        }

        try
        {
            using var platformNotification = builder.Build();
            manager.Notify(notification.Id, NotificationTagId, platformNotification);
            return NativeResult.Ok;
        }
        catch (Java.Lang.SecurityException ex)
        {
            return NativeError.PermissionDenied(capability, NativePermissions.Notifications, PermissionStatus.Denied, ex.Message) with { Exception = ex };
        }
    }

    /// <summary>Gets the notification manager.</summary>
    public static NotificationManagerCompat GetManager(Context context) =>
        NotificationManagerCompat.From(context) ?? throw new InvalidOperationException("NotificationManagerCompat is not available.");

    /// <summary>Removes a posted notification.</summary>
    public static void Cancel(Context context, string notificationId)
    {
        using var manager = GetManager(context);
        manager.Cancel(notificationId, NotificationTagId);
    }

    /// <summary>Creates or updates a channel (Android 8.0+; no-op below).</summary>
    public static void CreateChannel(NotificationManagerCompat manager, string id, string name, string? description, NotificationImportance importance)
    {
        using var builder = new NotificationChannelCompat.Builder(id, NotificationExtras.ToChannelImportance(importance));
        builder.SetName(name);
        if (description is not null)
        {
            builder.SetDescription(description);
        }

        using var channel = builder.Build();
        manager.CreateNotificationChannel(channel);
    }

    private static NativeResult<string> EnsureChannel(NotificationManagerCompat manager, string? requested, NotificationSettings settings)
    {
        var id = string.IsNullOrWhiteSpace(requested) ? settings.DefaultChannelId : requested;
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            return id;
        }

        using var existing = manager.GetNotificationChannelCompat(id);
        if (existing is null)
        {
            if (!string.Equals(id, settings.DefaultChannelId, StringComparison.Ordinal))
            {
                return NativeError.Failed(NativeCapabilities.Notifications, NativeErrorCodes.NotFound,
                    $"Notification channel '{id}' does not exist. Create it with CreateChannelAsync before posting to it.");
            }

            CreateChannel(manager, id, settings.DefaultChannelName, settings.DefaultChannelDescription, settings.DefaultChannelImportance);
            return id;
        }

        if (existing.Importance == NotificationManagerCompat.ImportanceNone)
        {
            return NativeError.PermissionDenied(NativeCapabilities.Notifications, NativePermissions.Notifications, PermissionStatus.Denied,
                $"The user turned off the notification channel '{id}' in system settings.");
        }

        return id;
    }

    private static NotificationCompat.Action? CreateAction(Context context, StoredNotification notification, StoredNotificationAction action)
    {
        var mutable = action.AcceptsText;
        var pendingIntent = action.OpensApp
            ? CreateActivityIntent(context, notification, action.Id, mutable)
            : CreateBroadcastIntent(context, notification, action.Id, mutable);
        if (pendingIntent is null)
        {
            return null;
        }

        using var builder = new NotificationCompat.Action.Builder(0, action.Title, pendingIntent);
        builder.SetShowsUserInterface(action.OpensApp);
        if (action.AcceptsText)
        {
            using var inputBuilder = new RemoteInput.Builder(NotificationExtras.ReplyKey);
            inputBuilder.SetLabel(action.TextPlaceholder ?? action.Title);
            using var remoteInput = inputBuilder.Build();
            builder.AddRemoteInput(remoteInput);
            builder.SetAllowGeneratedReplies(true);
            builder.SetSemanticAction(SemanticActionReply);
        }
        else if (action.IsDestructive)
        {
            builder.SetSemanticAction(SemanticActionDelete);
        }

        return builder.Build();
    }

    /// <summary>A PendingIntent that opens the app's launch activity with the notification extras.</summary>
    private static PendingIntent? CreateActivityIntent(Context context, StoredNotification notification, string? actionId, bool mutable)
    {
        using var intent = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName ?? string.Empty);
        if (intent is null)
        {
            return null;
        }

        // Reuse the running activity (onNewIntent) instead of stacking a new one.
        intent.AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        PutExtras(intent, notification, actionId);
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            // Makes each (notification, action) PendingIntent distinct even if request codes collide.
            intent.SetIdentifier(UriScheme + ":" + notification.Id + "#" + actionId);
        }

        return PendingIntent.GetActivity(context, StableHash.Compute("activity", notification.Id, actionId), intent, Flags(mutable));
    }

    /// <summary>A PendingIntent that delivers an action to <see cref="NotificationActionReceiver"/> without opening the app.</summary>
    private static PendingIntent? CreateBroadcastIntent(Context context, StoredNotification notification, string actionId, bool mutable)
    {
        using var intent = new Intent(context, typeof(NotificationActionReceiver));
        intent.SetAction(ActionIntentAction);
        using var data = Android.Net.Uri.FromParts(UriScheme, notification.Id, actionId);
        intent.SetData(data);
        PutExtras(intent, notification, actionId);
        return PendingIntent.GetBroadcast(context, StableHash.Compute("broadcast", notification.Id, actionId), intent, Flags(mutable));
    }

    private static void PutExtras(Intent intent, StoredNotification notification, string? actionId)
    {
        intent.PutExtra(NotificationExtras.NotificationId, notification.Id);
        if (actionId is not null)
        {
            intent.PutExtra(NotificationExtras.ActionId, actionId);
        }

        if (!string.IsNullOrEmpty(notification.Route))
        {
            intent.PutExtra(NotificationExtras.Route, notification.Route);
        }

        var data = NotificationExtras.EncodeData(notification.Data);
        if (data is not null)
        {
            intent.PutExtra(NotificationExtras.Data, data);
        }

        var route = string.IsNullOrEmpty(notification.Route) ? null : notification.Route;
        NotificationIntentSigner.Sign(intent, notification.Id, actionId, route, data);
    }

    /// <summary>
    /// Immutable PendingIntents everywhere, except for RemoteInput actions, which the system must fill in: those are
    /// explicitly mutable on Android 12+ (and implicitly mutable below).
    /// </summary>
    internal static PendingIntentFlags Flags(bool mutable)
    {
        var flags = PendingIntentFlags.UpdateCurrent;
        if (!mutable)
        {
            return flags | PendingIntentFlags.Immutable;
        }

        return OperatingSystem.IsAndroidVersionAtLeast(31) ? flags | PendingIntentFlags.Mutable : flags;
    }

    private static int ResolveSmallIcon(Context context, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name) && context.Resources is { } resources)
        {
            foreach (var type in (string[])["drawable", "mipmap"])
            {
#pragma warning disable CA1422 // GetIdentifier is discouraged but is the only way to resolve a resource configured by name.
                var id = resources.GetIdentifier(name, type, context.PackageName);
#pragma warning restore CA1422
                if (id != 0)
                {
                    return id;
                }
            }
        }

        var appIcon = context.ApplicationInfo?.Icon ?? 0;
        return appIcon != 0 ? appIcon : Android.Resource.Drawable.IcDialogInfo;
    }
}
