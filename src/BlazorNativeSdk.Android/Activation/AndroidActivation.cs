using Android.Content;
using AndroidX.Core.App;
using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.Logging;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>
/// Turns Android intents (notification taps and actions, <c>ACTION_VIEW</c> deep links and App Links) into
/// <see cref="AppActivation"/>s published through <see cref="IActivationPublisher"/>.
/// </summary>
/// <remarks>
/// The MAUI integration calls <see cref="HandleIntent"/> from the activity lifecycle automatically. In a plain .NET for
/// Android app, call it from the launcher activity:
/// <code>
/// protected override void OnCreate(Bundle? savedInstanceState)
/// {
///     base.OnCreate(savedInstanceState);
///     Microsoft.Maui.ApplicationModel.Platform.Init(this, savedInstanceState);
///     if (savedInstanceState is null) AndroidActivation.HandleIntent(Intent, isColdStart: true);
/// }
///
/// protected override void OnNewIntent(Intent? intent)
/// {
///     base.OnNewIntent(intent);
///     Intent = intent;
///     AndroidActivation.HandleIntent(intent, isColdStart: false);
/// }
/// </code>
/// Each intent is handled once: it is marked with an extra, and intents relaunched from the recents screen
/// (<c>FLAG_ACTIVITY_LAUNCHED_FROM_HISTORY</c>) are ignored, so activity recreation does not re-deliver a link.
/// Activations that arrive before the SDK started are buffered and delivered when it starts.
/// </remarks>
public static partial class AndroidActivation
{
    /// <summary>Publishes the activation carried by <paramref name="intent"/>, if any. Safe to call from any thread.</summary>
    /// <param name="intent">The intent that started or reached the activity.</param>
    /// <param name="isColdStart"><see langword="true"/> for the intent that launched the activity (<c>OnCreate</c>).</param>
    public static void HandleIntent(Intent? intent, bool isColdStart) => HandleIntentCore(intent, isColdStart, isBackground: false);

    /// <summary>
    /// Publishes the activation carried by <paramref name="intent"/>. <paramref name="isBackground"/> marks activations delivered
    /// without opening the app (notification actions handled by a broadcast receiver), which are dispatched to handlers
    /// immediately and never navigated to.
    /// </summary>
    internal static void HandleIntentCore(Intent? intent, bool isColdStart, bool isBackground)
    {
        if (intent is null || intent.GetBooleanExtra(NotificationExtras.Consumed, false))
        {
            return;
        }

        intent.PutExtra(NotificationExtras.Consumed, true);
        if ((intent.Flags & ActivityFlags.LaunchedFromHistory) != 0)
        {
            return;
        }

        var activation = Parse(intent, isColdStart);
        if (activation is null)
        {
            if (isColdStart)
            {
                AndroidServices.PublishActivation(new AppActivation { Kind = ActivationKind.Launch, IsColdStart = true, Source = NotificationExtras.Source });
            }

            return;
        }

        if (activation.Kind == ActivationKind.Notification && activation.ActionId is not null && activation.NotificationId is { } id)
        {
            // Unlike a tap on the notification body, action buttons do not auto-cancel the notification.
            AndroidNotificationPoster.Cancel(AndroidContext.App, id);
        }

        AndroidServices.PublishActivation(isBackground ? activation with { IsBackground = true } : activation);
    }

    /// <summary>Parses the activation in an intent without publishing it.</summary>
    internal static AppActivation? Parse(Intent intent, bool isColdStart)
    {
        if (intent.HasExtra(NotificationExtras.NotificationId))
        {
            var id = intent.GetStringExtra(NotificationExtras.NotificationId);
            var actionId = intent.GetStringExtra(NotificationExtras.ActionId);
            var route = intent.GetStringExtra(NotificationExtras.Route);
            var data = intent.GetStringExtra(NotificationExtras.Data);
            if (!NotificationIntentSigner.Verify(intent, id, actionId, route, data))
            {
                // Forged by another app (the launcher activity is exported) or posted before the signing key changed.
                LogUnverified(AndroidServices.CreateLogger(typeof(AndroidActivation).FullName!));
                return null;
            }

            return NotificationExtras.CreateActivation(id, actionId, route, data, GetReplyText(intent), isColdStart);
        }

        if (intent.Action == Intent.ActionView &&
            intent.DataString is { Length: > 0 } link &&
            Uri.TryCreate(link, UriKind.Absolute, out var uri))
        {
            return new AppActivation { Kind = ActivationKind.DeepLink, Uri = uri, IsColdStart = isColdStart, Source = NotificationExtras.Source };
        }

        return null;
    }

    private static string? GetReplyText(Intent intent)
    {
        using var results = RemoteInput.GetResultsFromIntent(intent);
        return results?.GetCharSequence(NotificationExtras.ReplyKey)?.ToString();
    }

    [LoggerMessage(EventId = 3991, Level = LogLevel.Warning, Message = "Ignored a notification intent whose signature did not verify.")]
    private static partial void LogUnverified(ILogger logger);
}
