using System.Security.Cryptography;
using Android.Content;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>
/// Authenticates notification intents. The launcher activity is exported, so any app could start it with forged
/// <c>bns.notification.*</c> extras and make the app navigate to an arbitrary route; notification routes bypass the
/// deep-link allow-list because they normally come from the app itself. Every notification intent therefore carries an
/// HMAC-SHA256 token over its activation fields, keyed with a random per-install secret kept in private preferences.
/// </summary>
internal static class NotificationIntentSigner
{
    public const string TokenExtra = "bns.notification.token";
    private const string PreferencesName = "blazornativesdk.activation";
    private const string KeyName = "signing-key";
    private static readonly Lock s_gate = new();
    private static byte[]? s_key;

    /// <summary>Adds the token extra to a notification intent.</summary>
    public static void Sign(Intent intent, string notificationId, string? actionId, string? route, string? data) =>
        intent.PutExtra(TokenExtra, NotificationIntentToken.Compute(GetKey(), notificationId, actionId, route, data));

    /// <summary>Verifies the token extra of a notification intent.</summary>
    public static bool Verify(Intent intent, string? notificationId, string? actionId, string? route, string? data)
    {
        var token = intent.GetStringExtra(TokenExtra);
        return notificationId is not null && token is not null && NotificationIntentToken.Verify(GetKey(), token, notificationId, actionId, route, data);
    }

    private static byte[] GetKey()
    {
        lock (s_gate)
        {
            if (s_key is not null)
            {
                return s_key;
            }

            var preferences = AndroidContext.App.GetSharedPreferences(PreferencesName, FileCreationMode.Private)
                ?? throw new InvalidOperationException("SharedPreferences are not available.");
            if (NotificationIntentToken.TryDecodeKey(preferences.GetString(KeyName, null)) is { } stored)
            {
                return s_key = stored;
            }

            // Missing or corrupt: regenerate; previously posted notifications then fail verification and are ignored.
            var key = RandomNumberGenerator.GetBytes(NotificationIntentToken.KeyLength);
            using var editor = preferences.Edit();
            editor?.PutString(KeyName, Convert.ToBase64String(key))?.Commit();
            return s_key = key;
        }
    }
}
