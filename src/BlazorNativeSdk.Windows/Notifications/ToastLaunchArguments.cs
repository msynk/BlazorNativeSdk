using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>What a toast click carries back to the app.</summary>
/// <param name="NotificationId">The <see cref="Devices.LocalNotification.Id"/>.</param>
/// <param name="ActionId">The chosen action, or <see langword="null"/> for the toast body.</param>
/// <param name="Route">The app route to open.</param>
/// <param name="Data">Custom data.</param>
internal sealed record ToastActivationPayload(string NotificationId, string? ActionId, string? Route, IReadOnlyDictionary<string, string> Data);

/// <summary>
/// Encodes toast activation payloads as foreground launch arguments (<c>bns-toast?nid=...</c>) or as signed protocol URIs
/// (<c>{scheme}://bns-toast/?nid=...&amp;sig=...</c>) and decodes them again.
/// </summary>
/// <remarks>
/// Protocol URIs can be crafted by any web page or program, so they carry an HMAC-SHA256 signature made with a per-user,
/// DPAPI-protected key (<see cref="ToastActivationKeyStore"/>). Unsigned or tampered URIs are not treated as notification
/// activations. The signature covers a canonical re-encoding of the decoded fields, so URI normalization cannot break it.
/// </remarks>
internal static class ToastLaunchArguments
{
    public const string ArgumentsPrefix = "bns-toast?";
    public const string ProtocolHost = "bns-toast";
    private const string IdKey = "nid";
    private const string ActionKey = "aid";
    private const string RouteKey = "route";
    private const string DataPrefix = "d.";
    private const string SignatureKey = "sig";

    /// <summary>Encodes the payload as foreground activation arguments.</summary>
    public static string ToArguments(ToastActivationPayload payload) => ArgumentsPrefix + EncodeQuery(payload);

    /// <summary>Encodes the payload as a signed protocol activation URI.</summary>
    public static Uri ToProtocolUri(string scheme, ToastActivationPayload payload, ReadOnlySpan<byte> key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        var query = EncodeQuery(payload);
        return new Uri($"{scheme}://{ProtocolHost}/?{query}&{SignatureKey}={Sign(query, key)}");
    }

    /// <summary>Decodes foreground activation arguments.</summary>
    public static bool TryParseArguments(string? arguments, [NotNullWhen(true)] out ToastActivationPayload? payload)
    {
        payload = null;
        return arguments is not null &&
               arguments.StartsWith(ArgumentsPrefix, StringComparison.Ordinal) &&
               TryParseQuery(arguments.AsSpan(ArgumentsPrefix.Length), out payload, out _);
    }

    /// <summary>Decodes and verifies a protocol activation URI produced by <see cref="ToProtocolUri"/>.</summary>
    public static bool TryParseProtocolUri(Uri? uri, string? scheme, ReadOnlySpan<byte> key, [NotNullWhen(true)] out ToastActivationPayload? payload)
    {
        payload = null;
        if (uri is null || !uri.IsAbsoluteUri || string.IsNullOrEmpty(scheme) ||
            !string.Equals(uri.Scheme, scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, ProtocolHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Parse the original text so that Uri normalization cannot alter the encoded values.
        var text = uri.OriginalString;
        var queryStart = text.IndexOf('?', StringComparison.Ordinal);
        if (queryStart < 0)
        {
            return false;
        }

        var query = text.AsSpan(queryStart + 1);
        var fragment = query.IndexOf('#');
        if (fragment >= 0)
        {
            query = query[..fragment];
        }

        if (!TryParseQuery(query, out var parsed, out var signature) || signature is null)
        {
            return false;
        }

        var expected = Sign(EncodeQuery(parsed), key);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature)))
        {
            return false;
        }

        payload = parsed;
        return true;
    }

    /// <summary>Encodes the fields in a canonical order with RFC 3986 escaping.</summary>
    public static string EncodeQuery(ToastActivationPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var builder = new StringBuilder();
        Append(builder, IdKey, payload.NotificationId);
        if (payload.ActionId is not null)
        {
            Append(builder, ActionKey, payload.ActionId);
        }

        if (payload.Route is not null)
        {
            Append(builder, RouteKey, payload.Route);
        }

        foreach (var pair in payload.Data.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            Append(builder, DataPrefix + pair.Key, pair.Value);
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string key, string value)
    {
        if (builder.Length > 0)
        {
            builder.Append('&');
        }

        builder.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
    }

    private static bool TryParseQuery(ReadOnlySpan<char> query, [NotNullWhen(true)] out ToastActivationPayload? payload, out string? signature)
    {
        payload = null;
        signature = null;
        string? id = null, action = null, route = null;
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var range in query.Split('&'))
        {
            var part = query[range];
            if (part.IsEmpty)
            {
                continue;
            }

            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                return false;
            }

            string key, value;
            try
            {
                key = Uri.UnescapeDataString(part[..separator].ToString());
                value = Uri.UnescapeDataString(part[(separator + 1)..].ToString());
            }
            catch (UriFormatException)
            {
                return false;
            }

            switch (key)
            {
                case IdKey:
                    id = value;
                    break;
                case ActionKey:
                    action = value;
                    break;
                case RouteKey:
                    route = value;
                    break;
                case SignatureKey:
                    signature = value;
                    break;
                default:
                    if (key.StartsWith(DataPrefix, StringComparison.Ordinal) && key.Length > DataPrefix.Length)
                    {
                        data[key[DataPrefix.Length..]] = value;
                    }

                    break;
            }
        }

        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        payload = new ToastActivationPayload(id, action, route, data);
        return true;
    }

    private static string Sign(string canonicalQuery, ReadOnlySpan<byte> key)
    {
        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonicalQuery), mac);
        return Base64Url.EncodeToString(mac);
    }
}

/// <summary>The per-user secret that signs protocol activation URIs, stored DPAPI-protected in the app data folder.</summary>
internal sealed class ToastActivationKeyStore
{
    private const int KeySize = 32;
    private const int MaxAttempts = 16;
    private static readonly TimeSpan[] s_readRetryDelays =
        [TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500)];

    private readonly string _path;
    private readonly byte[] _entropy;
    private readonly Lazy<byte[]> _key;

    public ToastActivationKeyStore(string directory, string appId)
    {
        _path = Path.Combine(directory, "toast-activation.key");
        _entropy = SHA256.HashData(Encoding.UTF8.GetBytes("BlazorNativeSdk.ToastActivation/v1\0" + appId));
        // PublicationOnly does not cache exceptions (a transient I/O failure is retried); concurrent creators converge on the
        // key file that won the atomic move.
        _key = new Lazy<byte[]>(LoadOrCreate, LazyThreadSafetyMode.PublicationOnly);
    }

    /// <summary>The signing key (loaded or created on first use).</summary>
    public ReadOnlySpan<byte> Key => _key.Value;

    private byte[] LoadOrCreate()
    {
        var retries = 0;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var state = TryLoad(out var existing);
            if (state == KeyFileState.Loaded)
            {
                return existing!;
            }

            if (state == KeyFileState.Unreadable)
            {
                // Locked by another process (a concurrent writer, a backup or antivirus scanner) or briefly inaccessible:
                // the key may be perfectly valid, and replacing it would invalidate every toast already shown.
                if (retries == s_readRetryDelays.Length)
                {
                    throw new IOException($"The toast activation key '{_path}' exists but cannot be read; it was left in place.");
                }

                Thread.Sleep(s_readRetryDelays[retries++]);
                continue;
            }

            // Missing, or definitively invalid (wrong length, or DPAPI cannot decrypt it: another user or machine, corruption).
            if (TryCreate(overwrite: state == KeyFileState.Invalid) is { } created)
            {
                return created;
            }

            // Another instance created the key meanwhile: load theirs so both sign identically.
        }

        throw new IOException($"The toast activation key '{_path}' could not be loaded or created.");
    }

    private byte[]? TryCreate(bool overwrite)
    {
        var key = RandomNumberGenerator.GetBytes(KeySize);
        var protectedKey = ProtectedData.Protect(key, _entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, protectedKey);
            try
            {
                File.Move(temp, _path, overwrite);
            }
            catch (IOException) when (!overwrite && File.Exists(_path))
            {
                return null;
            }

            // Concurrent replacements of an invalid file converge on whichever key landed on disk.
            return TryLoad(out var stored) == KeyFileState.Loaded ? stored : key;
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private KeyFileState TryLoad(out byte[]? key)
    {
        key = null;
        byte[] data;
        try
        {
            if (!File.Exists(_path))
            {
                return KeyFileState.Missing;
            }

            data = File.ReadAllBytes(_path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return KeyFileState.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return KeyFileState.Unreadable;
        }

        try
        {
            var candidate = ProtectedData.Unprotect(data, _entropy, DataProtectionScope.CurrentUser);
            if (candidate.Length != KeySize)
            {
                return KeyFileState.Invalid;
            }

            key = candidate;
            return KeyFileState.Loaded;
        }
        catch (CryptographicException)
        {
            return KeyFileState.Invalid;
        }
    }

    private enum KeyFileState
    {
        Missing,
        Loaded,
        Unreadable,
        Invalid,
    }
}
