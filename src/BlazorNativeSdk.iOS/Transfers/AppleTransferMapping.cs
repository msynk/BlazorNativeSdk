using System.Globalization;
using System.Text;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>How a finished <c>NSURLSessionTask</c> is handled.</summary>
internal enum AppleTransferOutcome
{
    /// <summary>The transfer completed.</summary>
    Completed,

    /// <summary>A transient failure: create a new task after a backoff delay.</summary>
    Retry,

    /// <summary>A permanent failure.</summary>
    Failed,
}

/// <summary>
/// Pure decision logic of the background <c>URLSession</c> transfer adapter: error classification, backoff, path resolution
/// and multipart framing. It uses no Apple types so it can be tested without a device.
/// </summary>
internal static class AppleTransferMapping
{
    /// <summary>The longest backoff between retries (same as the Core HTTP transfer queue).</summary>
    public static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(5);

    // NSURLErrorDomain codes (NSURLError.h).
    private const long Cancelled = -999;
    private const long BackgroundSessionWasDisconnected = -997;
    private const long TimedOut = -1001;
    private const long CannotFindHost = -1003;
    private const long CannotConnectToHost = -1004;
    private const long NetworkConnectionLost = -1005;
    private const long DnsLookupFailed = -1006;
    private const long ResourceUnavailable = -1008;
    private const long NotConnectedToInternet = -1009;
    private const long BadServerResponse = -1011;
    private const long InternationalRoamingOff = -1018;
    private const long CallIsActive = -1019;
    private const long DataNotAllowed = -1020;
    private const long CannotLoadFromNetwork = -2000;

    /// <summary>The <c>NSURLErrorDomain</c> error domain.</summary>
    public const string UrlErrorDomain = "NSURLErrorDomain";

    /// <summary>Decides what to do with a task that finished with an HTTP response and no transport error.</summary>
    public static AppleTransferOutcome ClassifyResponse(int statusCode, int retries, int maxRetries) => statusCode switch
    {
        >= 200 and < 300 => AppleTransferOutcome.Completed,
        _ when IsTransientStatus(statusCode) && retries < maxRetries => AppleTransferOutcome.Retry,
        _ => AppleTransferOutcome.Failed,
    };

    /// <summary>
    /// Decides what to do with a task that failed with an <c>NSError</c>. A cancellation the app did not request (the user
    /// force-quit the app, or the system cancelled background transfers) is retried like a lost connection.
    /// </summary>
    public static AppleTransferOutcome ClassifyError(string? domain, long code, int retries, int maxRetries) =>
        IsTransientError(domain, code) && retries < maxRetries ? AppleTransferOutcome.Retry : AppleTransferOutcome.Failed;

    /// <summary>Whether an <c>NSError</c> describes a condition that may pass (connectivity, timeouts, cancellation by the system).</summary>
    public static bool IsTransientError(string? domain, long code) =>
        string.Equals(domain, UrlErrorDomain, StringComparison.Ordinal) && code is Cancelled or BackgroundSessionWasDisconnected or TimedOut
            or CannotFindHost or CannotConnectToHost or NetworkConnectionLost or DnsLookupFailed or ResourceUnavailable
            or NotConnectedToInternet or BadServerResponse or InternationalRoamingOff or CallIsActive or DataNotAllowed
            or CannotLoadFromNetwork;

    /// <summary>Whether an <c>NSError</c> is <c>NSURLErrorCancelled</c>.</summary>
    public static bool IsCancellation(string? domain, long code) =>
        string.Equals(domain, UrlErrorDomain, StringComparison.Ordinal) && code == Cancelled;

    /// <summary>HTTP status codes that are retried (same set as the Core HTTP transfer queue).</summary>
    public static bool IsTransientStatus(int statusCode) => statusCode is 408 or 429 or 500 or 502 or 503 or 504;

    /// <summary>The delay before retry number <paramref name="retries"/> + 1: <c>Retry-After</c> when present, else 2^(n+1) seconds up to five minutes.</summary>
    public static TimeSpan GetBackoff(int retries, TimeSpan? retryAfter)
    {
        if (retryAfter is { } wait)
        {
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return TimeSpan.FromSeconds(Math.Min(Math.Pow(2, Math.Max(0, retries) + 1), MaximumBackoff.TotalSeconds));
    }

    /// <summary>Parses a <c>Retry-After</c> header: delta seconds or an HTTP date relative to <paramref name="now"/>.</summary>
    public static TimeSpan? ParseRetryAfter(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.FromSeconds(Math.Min(seconds, (long)TimeSpan.FromDays(1).TotalSeconds));
        }

        if (DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            var wait = date - now;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>
    /// Resolves a transfer path: rooted paths are used as given, relative paths are resolved against <paramref name="root"/>
    /// and must stay inside it.
    /// </summary>
    /// <returns>The full path, or <see langword="null"/> when a relative path escapes the root.</returns>
    public static string? ResolvePath(string path, string root)
    {
        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        var full = Path.GetFullPath(Path.Combine(root, path));
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return full.StartsWith(rootWithSeparator, StringComparison.Ordinal) ? full : null;
    }

    /// <summary>The bytes written before the file content of a <c>multipart/form-data</c> body with one file part.</summary>
    public static byte[] MultipartPreamble(string boundary, string fieldName, string fileName, string contentType) =>
        Encoding.UTF8.GetBytes(
            $"--{boundary}\r\n" +
            $"Content-Disposition: form-data; name=\"{QuoteSafe(fieldName)}\"; filename=\"{QuoteSafe(fileName)}\"\r\n" +
            $"Content-Type: {HeaderSafe(contentType)}\r\n\r\n");

    /// <summary>The bytes written after the file content of a <c>multipart/form-data</c> body.</summary>
    public static byte[] MultipartEpilogue(string boundary) => Encoding.UTF8.GetBytes($"\r\n--{boundary}--\r\n");

    /// <summary>The <c>Content-Type</c> header of a multipart body.</summary>
    public static string MultipartContentType(string boundary) => $"multipart/form-data; boundary={boundary}";

    // Quoted-string values in Content-Disposition: quotes and line breaks would let a file name inject headers or parts.
    private static string QuoteSafe(string value) => HeaderSafe(value).Replace("\"", "%22", StringComparison.Ordinal);

    private static string HeaderSafe(string value) => value.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);
}
