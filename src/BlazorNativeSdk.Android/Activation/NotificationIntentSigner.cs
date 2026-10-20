using System.Security.Cryptography;
using System.Text;
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
        intent.PutExtra(TokenExtra, ComputeToken(GetKey(), notificationId, actionId, route, data));

    /// <summary>Verifies the token extra of a notification intent.</summary>
    public static bool Verify(Intent intent, string? notificationId, string? actionId, string? route, string? data)
    {
        var token = intent.GetStringExtra(TokenExtra);
        return notificationId is not null && token is not null && VerifyToken(GetKey(), token, notificationId, actionId, route, data);
    }

    /// <summary>Computes the token. Pure.</summary>
    public static string ComputeToken(byte[] key, string notificationId, string? actionId, string? route, string? data)
    {
        var payload = Encoding.UTF8.GetBytes(string.Join('\u001F', Field(notificationId), Field(actionId), Field(route), Field(data)));
        return Convert.ToBase64String(HMACSHA256.HashData(key, payload));
    }

    /// <summary>Verifies a token in constant time. Pure.</summary>
    public static bool VerifyToken(byte[] key, string token, string notificationId, string? actionId, string? route, string? data)
    {
        var expected = Encoding.ASCII.GetBytes(ComputeToken(key, notificationId, actionId, route, data));
        var actual = Encoding.ASCII.GetBytes(token);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    // Length-prefixed so field boundaries cannot be shifted; null and empty are distinct.
    private static string Field(string? value) => value is null ? "-" : value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value;

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
            var stored = preferences.GetString(KeyName, null);
            if (stored is not null)
            {
                try
                {
                    var decoded = Convert.FromBase64String(stored);
                    if (decoded.Length == 32)
                    {
                        return s_key = decoded;
                    }
                }
                catch (FormatException)
                {
                    // Regenerate below; previously posted notifications then fail verification and are ignored.
                }
            }

            var key = RandomNumberGenerator.GetBytes(32);
            using var editor = preferences.Edit();
            editor?.PutString(KeyName, Convert.ToBase64String(key))?.Commit();
            return s_key = key;
        }
    }
}
