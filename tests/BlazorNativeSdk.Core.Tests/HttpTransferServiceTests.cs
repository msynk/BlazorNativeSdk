using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;
using BlazorNativeSdk.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace BlazorNativeSdk.Tests;

public sealed class HttpTransferServiceTests : IAsyncDisposable
{
    private static readonly byte[] Payload = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bns-transfer-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new();
    private readonly FakeServer _server = new();
    private readonly ServiceProvider _provider;

    public HttpTransferServiceTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddBlazorNativeSdk(sdk =>
        {
            sdk.AddFakes();
            sdk.AddCapability<IAppStorage>(NativeCapabilities.AppStorage, _ => new TempAppStorage(_root));
            sdk.AddHttpTransfers(o =>
            {
                o.HttpClientFactory = _ => new HttpClient(_server);
                o.BufferSize = 16 * 1024;
            });
        });
        _provider = services.BuildServiceProvider();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpTransferService Transfers => _provider.GetRequiredService<HttpTransferService>();

    [Fact]
    public async Task Downloads_to_a_relative_path_inside_app_data()
    {
        await Transfers.StartAsync(Ct);

        var queued = await Transfers.EnqueueDownloadAsync(new DownloadRequest { Id = "d1", Uri = new Uri("https://files.test/a.bin"), DestinationPath = "downloads/a.bin" }, Ct);
        Assert.True(queued.IsSuccess);

        var done = await WaitAsync("d1");

        Assert.Equal(TransferStatus.Completed, done.Status);
        Assert.Equal(Payload.Length, done.TotalBytes);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(Path.Combine(_root, "downloads", "a.bin"), Ct));
        Assert.False(File.Exists(Path.Combine(_root, "downloads", "a.bin.part")));
    }

    [Fact]
    public async Task Interrupted_downloads_resume_with_a_validated_range_request()
    {
        _server.FailAfterBytes = 100_000;
        await Transfers.StartAsync(Ct);

        await Transfers.EnqueueDownloadAsync(new DownloadRequest { Id = "d2", Uri = new Uri("https://files.test/b.bin"), DestinationPath = "b.bin" }, Ct);
        var done = await WaitAsync("d2", advanceClock: true);

        Assert.Equal(TransferStatus.Completed, done.Status);
        Assert.Equal(1, done.Retries);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(Path.Combine(_root, "b.bin"), Ct));
        var resumed = _server.Requests.Last();
        Assert.Equal("bytes=100000-", resumed.Range);
        Assert.Equal(FakeServer.ETag, resumed.IfRange);
    }

    [Fact]
    public async Task Retry_after_is_honored_for_transient_server_errors()
    {
        _server.ServiceUnavailableResponses = 1;
        await Transfers.StartAsync(Ct);

        await Transfers.EnqueueDownloadAsync(new DownloadRequest { Id = "d3", Uri = new Uri("https://files.test/c.bin"), DestinationPath = "c.bin" }, Ct);
        var done = await WaitAsync("d3");

        Assert.Equal(TransferStatus.Completed, done.Status);
        Assert.Equal(1, done.Retries);
    }

    [Fact]
    public async Task Client_errors_fail_permanently()
    {
        _server.NotFound = true;
        await Transfers.StartAsync(Ct);

        await Transfers.EnqueueDownloadAsync(new DownloadRequest { Id = "d4", Uri = new Uri("https://files.test/missing"), DestinationPath = "m.bin" }, Ct);
        var result = await Transfers.WaitForCompletionAsync("d4", Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(NativeResultStatus.Failed, result.Status);
        var info = (await Transfers.GetTransfersAsync(Ct)).Single();
        Assert.Equal(404, info.HttpStatusCode);
        Assert.Equal(0, info.Retries);
    }

    [Fact]
    public async Task Waits_for_connectivity_before_transferring()
    {
        var connectivity = _provider.GetRequiredService<FakeConnectivityService>();
        connectivity.GoOffline();
        await Transfers.StartAsync(Ct);

        await Transfers.EnqueueDownloadAsync(new DownloadRequest { Id = "d5", Uri = new Uri("https://files.test/d.bin"), DestinationPath = "d.bin" }, Ct);
        await PollAsync(async () => (await Transfers.GetTransfersAsync(Ct)).Single().Status == TransferStatus.WaitingForNetwork);
        Assert.Empty(_server.Requests);

        connectivity.GoOnline();
        Assert.Equal(TransferStatus.Completed, (await WaitAsync("d5")).Status);
    }

    [Fact]
    public async Task Uploads_files_as_multipart_form_data()
    {
        var source = Path.Combine(_root, "upload.txt");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(source, "hello upload", Ct);
        await Transfers.StartAsync(Ct);

        await Transfers.EnqueueUploadAsync(new UploadRequest { Id = "u1", Uri = new Uri("https://files.test/upload"), SourcePath = source, MultipartFieldName = "file", Method = "put" }, Ct);
        var done = await WaitAsync("u1");

        Assert.Equal(TransferStatus.Completed, done.Status);
        var request = _server.Requests.Single();
        Assert.Equal("PUT", request.Method);
        Assert.StartsWith("multipart/form-data", request.ContentType, StringComparison.Ordinal);
        Assert.Contains("hello upload", request.Body, StringComparison.Ordinal);
        Assert.Contains("filename=upload.txt", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_paths_escaping_app_data_and_non_http_urls()
    {
        var escape = await Transfers.EnqueueDownloadAsync(new DownloadRequest { Uri = new Uri("https://files.test/a"), DestinationPath = "../outside.bin" }, Ct);
        var ftp = await Transfers.EnqueueDownloadAsync(new DownloadRequest { Uri = new Uri("ftp://files.test/a"), DestinationPath = "a.bin" }, Ct);
        var missing = await Transfers.EnqueueUploadAsync(new UploadRequest { Uri = new Uri("https://files.test/u"), SourcePath = Path.Combine(_root, "nope.txt") }, Ct);

        Assert.Equal(NativeErrorCodes.InvalidArgument, escape.Error!.Code);
        Assert.Equal(NativeErrorCodes.InvalidArgument, ftp.Error!.Code);
        Assert.Equal(NativeErrorCodes.NotFound, missing.Error!.Code);
    }

    [Fact]
    public async Task Cancel_marks_the_transfer_cancelled_and_completes_waiters()
    {
        var connectivity = _provider.GetRequiredService<FakeConnectivityService>();
        connectivity.GoOffline();
        await Transfers.StartAsync(Ct);
        await Transfers.EnqueueDownloadAsync(new DownloadRequest { Id = "c1", Uri = new Uri("https://files.test/e.bin"), DestinationPath = "e.bin" }, Ct);

        var waiter = Transfers.WaitForCompletionAsync("c1", Ct).AsTask();
        Assert.True((await Transfers.CancelAsync("c1", Ct)).Value);

        Assert.Equal(NativeResultStatus.Cancelled, (await waiter.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Status);
    }

    private async Task<TransferInfo> WaitAsync(string id, bool advanceClock = false)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var info = (await Transfers.GetTransfersAsync(Ct)).Single(t => t.Id == id);
            if (info.IsFinished)
            {
                return info;
            }

            if (advanceClock)
            {
                _time.Advance(TimeSpan.FromSeconds(1));
            }

            await Task.Delay(20, Ct);
        }

        throw new TimeoutException($"Transfer {id} did not finish.");
    }

    private static async Task PollAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException();
            }

            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class TempAppStorage(string root) : FakeCapability(NativeCapabilities.AppStorage), IAppStorage
    {
        public NativeResult<string> GetDirectory(AppDirectory directory)
        {
            Directory.CreateDirectory(root);
            return root;
        }
    }

    private sealed record RecordedRequest(string Method, string? Range, string? IfRange, string? ContentType, string Body);

    private sealed class FakeServer : HttpMessageHandler
    {
        public const string ETag = "\"v1\"";
        private readonly ConcurrentQueue<RecordedRequest> _requests = new();
        private int _failAfter = -1;

        public int FailAfterBytes
        {
            set => _failAfter = value;
        }

        public int ServiceUnavailableResponses { get; set; }

        public bool NotFound { get; set; }

        public List<RecordedRequest> Requests => [.. _requests];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            _requests.Enqueue(new RecordedRequest(
                request.Method.Method,
                request.Headers.Range?.ToString(),
                request.Headers.IfRange?.ToString(),
                request.Content?.Headers.ContentType?.ToString(),
                body));

            if (NotFound)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (ServiceUnavailableResponses > 0)
            {
                ServiceUnavailableResponses--;
                var busy = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                busy.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return busy;
            }

            if (request.Method != HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.Created);
            }

            var start = request.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0;
            var validRange = request.Headers.IfRange?.EntityTag?.Tag == ETag;
            if (!validRange)
            {
                start = 0;
            }

            var slice = Payload.AsMemory((int)start).ToArray();
            Stream content = new MemoryStream(slice);
            var failAfter = Interlocked.Exchange(ref _failAfter, -1);
            if (failAfter >= 0)
            {
                content = new FailingStream(content, failAfter);
            }

            var response = new HttpResponseMessage(start > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new StreamContent(content),
            };
            response.Headers.ETag = new EntityTagHeaderValue(ETag);
            response.Content.Headers.ContentLength = slice.Length;
            if (start > 0)
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, Payload.Length - 1, Payload.Length);
            }

            return response;
        }
    }

    private sealed class FailingStream(Stream inner, int failAfter) : Stream
    {
        private int _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= failAfter)
            {
                throw new IOException("Connection reset.");
            }

            var n = inner.Read(buffer, offset, Math.Min(count, failAfter - _read));
            _read += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
