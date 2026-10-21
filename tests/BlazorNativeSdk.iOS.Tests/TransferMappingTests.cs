using System.Text;

namespace BlazorNativeSdk.ApplePlatform.Tests;

public sealed class TransferMappingTests
{
    private const string UrlDomain = AppleTransferMapping.UrlErrorDomain;

    [Theory]
    [InlineData(200, nameof(AppleTransferOutcome.Completed))]
    [InlineData(204, nameof(AppleTransferOutcome.Completed))]
    [InlineData(408, nameof(AppleTransferOutcome.Retry))]
    [InlineData(429, nameof(AppleTransferOutcome.Retry))]
    [InlineData(500, nameof(AppleTransferOutcome.Retry))]
    [InlineData(502, nameof(AppleTransferOutcome.Retry))]
    [InlineData(503, nameof(AppleTransferOutcome.Retry))]
    [InlineData(504, nameof(AppleTransferOutcome.Retry))]
    [InlineData(301, nameof(AppleTransferOutcome.Failed))]
    [InlineData(400, nameof(AppleTransferOutcome.Failed))]
    [InlineData(401, nameof(AppleTransferOutcome.Failed))]
    [InlineData(404, nameof(AppleTransferOutcome.Failed))]
    [InlineData(501, nameof(AppleTransferOutcome.Failed))]
    public void Responses_complete_retry_or_fail_by_status(int status, string expected) =>
        Assert.Equal(expected, AppleTransferMapping.ClassifyResponse(status, retries: 0, maxRetries: 8).ToString());

    [Fact]
    public void Transient_responses_fail_once_retries_are_exhausted()
    {
        Assert.Equal(AppleTransferOutcome.Retry, AppleTransferMapping.ClassifyResponse(503, retries: 7, maxRetries: 8));
        Assert.Equal(AppleTransferOutcome.Failed, AppleTransferMapping.ClassifyResponse(503, retries: 8, maxRetries: 8));
        Assert.Equal(AppleTransferOutcome.Failed, AppleTransferMapping.ClassifyResponse(503, retries: 0, maxRetries: 0));
    }

    [Theory]
    [InlineData(-999)]  // Cancelled (by the system: force-quit, background updates disabled)
    [InlineData(-997)]  // BackgroundSessionWasDisconnected
    [InlineData(-1001)] // TimedOut
    [InlineData(-1003)] // CannotFindHost
    [InlineData(-1004)] // CannotConnectToHost
    [InlineData(-1005)] // NetworkConnectionLost
    [InlineData(-1006)] // DNSLookupFailed
    [InlineData(-1008)] // ResourceUnavailable
    [InlineData(-1009)] // NotConnectedToInternet
    [InlineData(-1011)] // BadServerResponse
    [InlineData(-1018)] // InternationalRoamingOff
    [InlineData(-1019)] // CallIsActive
    [InlineData(-1020)] // DataNotAllowed
    [InlineData(-2000)] // CannotLoadFromNetwork
    public void Connectivity_and_system_cancellation_errors_are_retried(long code)
    {
        Assert.True(AppleTransferMapping.IsTransientError(UrlDomain, code));
        Assert.Equal(AppleTransferOutcome.Retry, AppleTransferMapping.ClassifyError(UrlDomain, code, retries: 0, maxRetries: 8));
        Assert.Equal(AppleTransferOutcome.Failed, AppleTransferMapping.ClassifyError(UrlDomain, code, retries: 8, maxRetries: 8));
    }

    [Theory]
    [InlineData(UrlDomain, -1000)]  // BadURL
    [InlineData(UrlDomain, -1002)]  // UnsupportedURL
    [InlineData(UrlDomain, -1007)]  // HTTPTooManyRedirects
    [InlineData(UrlDomain, -1012)]  // UserCancelledAuthentication
    [InlineData(UrlDomain, -1100)]  // FileDoesNotExist
    [InlineData(UrlDomain, -1200)]  // SecureConnectionFailed
    [InlineData(UrlDomain, -1202)]  // ServerCertificateUntrusted
    [InlineData(UrlDomain, -3003)]  // CannotWriteToFile
    [InlineData("NSCocoaErrorDomain", -1009)]
    [InlineData(null, -1009)]
    public void Other_errors_fail_permanently(string? domain, long code)
    {
        Assert.False(AppleTransferMapping.IsTransientError(domain, code));
        Assert.Equal(AppleTransferOutcome.Failed, AppleTransferMapping.ClassifyError(domain, code, retries: 0, maxRetries: 8));
    }

    [Fact]
    public void Only_NSURLErrorCancelled_in_the_url_domain_is_a_cancellation()
    {
        Assert.True(AppleTransferMapping.IsCancellation(UrlDomain, -999));
        Assert.False(AppleTransferMapping.IsCancellation(UrlDomain, -1001));
        Assert.False(AppleTransferMapping.IsCancellation("NSCocoaErrorDomain", -999));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 4)]
    [InlineData(2, 8)]
    [InlineData(7, 256)]
    [InlineData(8, 300)]
    [InlineData(30, 300)]
    [InlineData(-3, 2)]
    public void Backoff_doubles_up_to_five_minutes(int retries, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), AppleTransferMapping.GetBackoff(retries, retryAfter: null));

    [Fact]
    public void Retry_after_overrides_the_backoff_and_is_never_negative()
    {
        Assert.Equal(TimeSpan.FromSeconds(90), AppleTransferMapping.GetBackoff(5, TimeSpan.FromSeconds(90)));
        Assert.Equal(TimeSpan.Zero, AppleTransferMapping.GetBackoff(5, TimeSpan.FromSeconds(-5)));
    }

    [Fact]
    public void Retry_after_accepts_seconds_and_http_dates()
    {
        var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromSeconds(120), AppleTransferMapping.ParseRetryAfter(" 120 ", now));
        Assert.Equal(TimeSpan.FromMinutes(5), AppleTransferMapping.ParseRetryAfter("Wed, 21 Oct 2026 12:05:00 GMT", now));
        Assert.Equal(TimeSpan.Zero, AppleTransferMapping.ParseRetryAfter("Wed, 21 Oct 2026 11:00:00 GMT", now));
        Assert.Equal(TimeSpan.FromDays(1), AppleTransferMapping.ParseRetryAfter("999999999", now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("-5")]
    [InlineData("1.5")]
    public void Invalid_retry_after_values_are_ignored(string? value) =>
        Assert.Null(AppleTransferMapping.ParseRetryAfter(value, DateTimeOffset.UnixEpoch));

    [Fact]
    public void Relative_paths_resolve_inside_the_root_and_cannot_escape_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "bns-root");

        Assert.Equal(Path.Combine(root, "downloads", "a.bin"), AppleTransferMapping.ResolvePath(Path.Combine("downloads", "a.bin"), root));
        Assert.Equal(Path.Combine(root, "a.bin"), AppleTransferMapping.ResolvePath(Path.Combine("x", "..", "a.bin"), root));
        Assert.Null(AppleTransferMapping.ResolvePath(Path.Combine("..", "outside.bin"), root));
        Assert.Null(AppleTransferMapping.ResolvePath(Path.Combine("..", "bns-root-sibling", "a.bin"), root));
    }

    [Fact]
    public void Rooted_paths_are_used_as_given()
    {
        var elsewhere = Path.Combine(Path.GetTempPath(), "elsewhere", "file.bin");

        Assert.Equal(Path.GetFullPath(elsewhere), AppleTransferMapping.ResolvePath(elsewhere, Path.Combine(Path.GetTempPath(), "bns-root")));
    }

    [Fact]
    public void Multipart_body_frames_one_file_part()
    {
        var body = Encoding.UTF8.GetString(
            [.. AppleTransferMapping.MultipartPreamble("b1", "file", "photo.jpg", "image/jpeg"), .. "DATA"u8, .. AppleTransferMapping.MultipartEpilogue("b1")]);

        Assert.Equal(
            "--b1\r\nContent-Disposition: form-data; name=\"file\"; filename=\"photo.jpg\"\r\nContent-Type: image/jpeg\r\n\r\nDATA\r\n--b1--\r\n",
            body);
        Assert.Equal("multipart/form-data; boundary=b1", AppleTransferMapping.MultipartContentType("b1"));
    }

    [Fact]
    public void Multipart_names_cannot_inject_headers_or_parts()
    {
        var preamble = Encoding.UTF8.GetString(AppleTransferMapping.MultipartPreamble(
            "b1", "f\"ield", "evil\".txt\r\nContent-Type: text/html", "text/plain\r\nX-Injected: 1"));

        Assert.Contains("name=\"f%22ield\"", preamble, StringComparison.Ordinal);
        Assert.Contains("filename=\"evil%22.txtContent-Type: text/html\"", preamble, StringComparison.Ordinal);
        Assert.Contains("Content-Type: text/plainX-Injected: 1\r\n\r\n", preamble, StringComparison.Ordinal);
        // Boundary, Content-Disposition and Content-Type lines, then the blank line: no injected line breaks.
        Assert.Equal(5, preamble.Split("\r\n").Length);
    }
}
