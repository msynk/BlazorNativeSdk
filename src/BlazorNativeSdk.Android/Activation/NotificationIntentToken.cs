using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>
/// The HMAC-SHA256 token that authenticates a notification intent (see <see cref="NotificationIntentSigner"/>). Pure: works on
/// plain values so it can be unit tested without Android.
/// </summary>
internal static class NotificationIntentToken
{
    /// <summary>The length of the per-install signing key in bytes.</summary>
    public const int KeyLength = 32;

    /// <summary>Computes the token over the activation fields of a notification intent.</summary>
    public static string Compute(byte[] key, string notificationId, string? actionId, string? route, string? data)
    {
        var payload = Encoding.UTF8.GetBytes(string.Join('\u001F', Field(notificationId), Field(actionId), Field(route), Field(data)));
        return Convert.ToBase64String(HMACSHA256.HashData(key, payload));
    }

    /// <summary>
    /// Verifies a token in constant time. A missing token or notification id never verifies.
    /// </summary>
    public static bool Verify(byte[] key, string? token, string? notificationId, string? actionId, string? route, string? data)
    {
        if (token is null || notificationId is null)
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Compute(key, notificationId, actionId, route, data));
        var actual = Encoding.ASCII.GetBytes(token);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>
    /// Decodes a stored signing key, or returns <see langword="null"/> when it is missing, corrupt or not
    /// <see cref="KeyLength"/> bytes long (a new key is then generated).
    /// </summary>
    public static byte[]? TryDecodeKey(string? stored)
    {
        if (stored is null)
        {
            return null;
        }

        try
        {
            var decoded = Convert.FromBase64String(stored);
            return decoded.Length == KeyLength ? decoded : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // Length-prefixed so field boundaries cannot be shifted; null and empty are distinct.
    private static string Field(string? value) => value is null ? "-" : value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;
}
