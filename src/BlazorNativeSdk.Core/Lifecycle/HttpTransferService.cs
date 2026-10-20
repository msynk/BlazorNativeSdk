using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text.Json;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk.Lifecycle;

/// <summary>Options for <see cref="HttpTransferService"/>.</summary>
public sealed class HttpTransferOptions
{
    /// <summary>The adapter name reported in the policy and status.</summary>
    public string AdapterName { get; set; } = "http";

    /// <summary>The maximum number of transfers that run at the same time. Default: 2.</summary>
    public int MaxConcurrentTransfers { get; set; } = 2;

    /// <summary>
    /// Creates the <see cref="HttpClient"/> used for transfers. The client must not have a short <see cref="HttpClient.Timeout"/>.
    /// Defaults to a shared client with an infinite timeout.
    /// </summary>
    public Func<IServiceProvider, HttpClient>? HttpClientFactory { get; set; }

    /// <summary>Whether transfers keep running while the app is in the background (true for desktop hosts). Reported in the policy.</summary>
    public bool ContinuesInBackground { get; set; } = true;

    /// <summary>The minimum interval between progress events for one transfer. Default: 250 ms.</summary>
    public TimeSpan ProgressInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How often progress is persisted while transferring. Default: 2 seconds.</summary>
    public TimeSpan PersistInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The longest backoff between retries. Default: 5 minutes.</summary>
    public TimeSpan MaximumBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long finished transfers stay visible. Default: 7 days.</summary>
    public TimeSpan FinishedTransferRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>The copy buffer size. Default: 80 KB.</summary>
    public int BufferSize { get; set; } = 81920;
}

/// <summary>
/// A durable transfer queue based on <see cref="HttpClient"/>. Transfers are persisted, wait for connectivity, retry with
/// exponential backoff (honoring <c>Retry-After</c>), and downloads resume from the last received byte using HTTP range
/// requests validated with <c>If-Range</c>. Transfers run only while the app process is alive and resume on next launch.
/// </summary>
[UnsupportedOSPlatform("browser")]
public sealed partial class HttpTransferService : ITransferService, INativeSdkStartupTask, IAsyncDisposable, IDisposable
{
    private const string Prefix = "bns.transfer.";
    private const string PartialSuffix = ".part";
    private static readonly Lazy<HttpClient> s_sharedClient = new(() => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });

    private readonly IServiceProvider _services;
    private readonly INativeStateStore _store;
    private readonly IAppStorage _storage;
    private readonly IConnectivityService _connectivity;
    private readonly HttpTransferOptions _options;
    private readonly ILogger<HttpTransferService> _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, TransferRecord> _records = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TransferInfo>> _waiters = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private HttpClient? _client;
    private bool _loaded;
    private Task? _loop;
    private int _disposed;

    /// <summary>Creates the service.</summary>
    public HttpTransferService(
        IServiceProvider services,
        INativeStateStore store,
        IAppStorage storage,
        IConnectivityService connectivity,
        IOptions<HttpTransferOptions> options,
        ILogger<HttpTransferService> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _services = services;
        _store = store;
        _storage = storage;
        _connectivity = connectivity;
        _options = options.Value;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        Policy = new TransferPolicy
        {
            Adapter = _options.AdapterName,
            ContinuesInBackground = _options.ContinuesInBackground,
            ContinuesAfterTermination = false,
            ResumesDownloads = true,
            Summary = "Transfers run inside the app process, wait for connectivity, retry with backoff and resume after restarts. " +
                      "Downloads continue from the last byte when the server supports range requests. Nothing transfers while the app is closed.",
        };
    }

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.Transfers;

    /// <inheritdoc />
    public TransferPolicy Policy { get; }

    /// <inheritdoc />
    public event EventHandler<TransferInfo>? TransferChanged;

    private HttpClient Client => _client ??= _options.HttpClientFactory?.Invoke(_services) ?? s_sharedClient.Value;

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Available(Capability, _options.AdapterName, "resume-downloads", "retry", "persisted"));

    /// <inheritdoc />
    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        if (_loop is not null)
        {
            return;
        }

        _connectivity.Changed += OnConnectivityChanged;
        _loop = Task.Run(LoopAsync, CancellationToken.None);
    }

    /// <inheritdoc />
    public async ValueTask<NativeResult<TransferInfo>> EnqueueDownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsHttp(request.Uri))
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "Only absolute http and https URLs can be downloaded.");
        }

        var path = ResolvePath(request.DestinationPath);
        if (!path.IsSuccess)
        {
            return path.Error;
        }

        return await EnqueueAsync(new TransferRecord
        {
            Id = request.Id ?? Guid.NewGuid().ToString("N"),
            Direction = TransferDirection.Download,
            Uri = request.Uri.AbsoluteUri,
            LocalPath = path.Value,
            Headers = new Dictionary<string, string>(request.Headers),
            RequireUnmeteredNetwork = request.RequireUnmeteredNetwork,
            MaxRetries = Math.Max(0, request.MaxRetries),
            Method = "GET",
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<NativeResult<TransferInfo>> EnqueueUploadAsync(UploadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsHttp(request.Uri))
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "Only absolute http and https URLs can be uploaded to.");
        }

        var path = ResolvePath(request.SourcePath);
        if (!path.IsSuccess)
        {
            return path.Error;
        }

        if (!File.Exists(path.Value))
        {
            return NativeError.Failed(Capability, NativeErrorCodes.NotFound, $"The file '{path.Value}' does not exist.");
        }

        return await EnqueueAsync(new TransferRecord
        {
            Id = request.Id ?? Guid.NewGuid().ToString("N"),
            Direction = TransferDirection.Upload,
            Uri = request.Uri.AbsoluteUri,
            LocalPath = path.Value,
            Headers = new Dictionary<string, string>(request.Headers),
            RequireUnmeteredNetwork = request.RequireUnmeteredNetwork,
            MaxRetries = Math.Max(0, request.MaxRetries),
            Method = string.IsNullOrWhiteSpace(request.Method) ? "POST" : request.Method.ToUpperInvariant(),
            ContentType = request.ContentType ?? MimeTypes.FromFileName(path.Value) ?? MimeTypes.OctetStream,
            MultipartFieldName = request.MultipartFieldName,
            TotalBytes = new FileInfo(path.Value).Length,
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<NativeResult<bool>> CancelAsync(string transferId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transferId);
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        if (!_records.TryGetValue(transferId, out var record))
        {
            return false;
        }

        if (_active.TryGetValue(transferId, out var cts))
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        if (record.Direction == TransferDirection.Download)
        {
            TryDelete(record.LocalPath + PartialSuffix);
        }

        await UpdateAsync(record, r =>
        {
            r.Status = TransferStatus.Cancelled;
            r.CompletedAt = _time.GetUtcNow();
        }, CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<NativeResult<TransferInfo>> RetryAsync(string transferId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transferId);
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        if (!_records.TryGetValue(transferId, out var record))
        {
            return NativeError.Failed(Capability, NativeErrorCodes.NotFound, $"Transfer '{transferId}' does not exist.");
        }

        if (record.Status is not (TransferStatus.Failed or TransferStatus.Cancelled))
        {
            return record.ToInfo();
        }

        await UpdateAsync(record, r =>
        {
            r.Status = TransferStatus.Queued;
            r.Retries = 0;
            r.ErrorMessage = null;
            r.CompletedAt = null;
            r.NextAttemptAt = null;
        }, cancellationToken).ConfigureAwait(false);
        Wake();
        return record.ToInfo();
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<TransferInfo>> GetTransfersAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return [.. _records.Values.OrderBy(r => r.CreatedAt).Select(r => r.ToInfo())];
    }

    /// <inheritdoc />
    public async ValueTask<NativeResult<TransferInfo>> WaitForCompletionAsync(string transferId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transferId);
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        if (!_records.TryGetValue(transferId, out var record))
        {
            return NativeError.Failed(Capability, NativeErrorCodes.NotFound, $"Transfer '{transferId}' does not exist.");
        }

        var waiter = _waiters.GetOrAdd(transferId, static _ => new TaskCompletionSource<TransferInfo>(TaskCreationOptions.RunContinuationsAsynchronously));
        var info = record.ToInfo();
        if (info.IsFinished)
        {
            _waiters.TryRemove(transferId, out _);
            return ToResult(info);
        }

        return ToResult(await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _connectivity.Changed -= OnConnectivityChanged;
            _shutdown.Cancel();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _connectivity.Changed -= OnConnectivityChanged;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _shutdown.Dispose();
    }

    private NativeResult<TransferInfo> ToResult(TransferInfo info) => info.Status switch
    {
        TransferStatus.Completed => info,
        TransferStatus.Cancelled => NativeError.Cancelled(Capability, "The transfer was cancelled."),
        _ => NativeError.Failed(Capability, NativeErrorCodes.PlatformError, info.ErrorMessage ?? "The transfer failed."),
    };

    private async ValueTask<NativeResult<TransferInfo>> EnqueueAsync(TransferRecord record, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        if (_records.TryGetValue(record.Id, out var existing) && !existing.ToInfo().IsFinished)
        {
            return existing.ToInfo();
        }

        record.Status = TransferStatus.Queued;
        record.CreatedAt = _time.GetUtcNow();
        _records[record.Id] = record;
        await PersistAsync(record, cancellationToken).ConfigureAwait(false);
        Raise(record);
        Wake();
        return record.ToInfo();
    }

    private NativeResult<string> ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "A file path is required.");
        }

        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        var root = _storage.GetDirectory(AppDirectory.AppData);
        if (!root.IsSuccess)
        {
            return root.Error;
        }

        var full = Path.GetFullPath(Path.Combine(root.Value, path));
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(root.Value) + Path.DirectorySeparatorChar;
        return full.StartsWith(rootWithSeparator, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? full
            : NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "Relative paths must stay inside the app data directory.");
    }

    private static bool IsHttp(Uri uri) => uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private async ValueTask EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded)
            {
                return;
            }

            var now = _time.GetUtcNow();
            foreach (var key in await _store.GetKeysAsync(Prefix, cancellationToken).ConfigureAwait(false))
            {
                var json = await _store.GetAsync(key, cancellationToken).ConfigureAwait(false);
                TransferRecord? record;
                try
                {
                    record = json is null ? null : JsonSerializer.Deserialize(json, CoreJsonContext.Default.TransferRecord);
                }
                catch (JsonException)
                {
                    record = null;
                }

                if (record is null)
                {
                    await _store.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (record.ToInfo().IsFinished && record.CompletedAt is { } done && now - done > _options.FinishedTransferRetention)
                {
                    await _store.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (record.Status == TransferStatus.Running)
                {
                    // Interrupted by process exit: resume.
                    record.Status = TransferStatus.Queued;
                }

                _records[record.Id] = record;
            }

            _loaded = true;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task LoopAsync()
    {
        var token = _shutdown.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                StartEligibleTransfers(token);
                var delay = GetNextDelay();
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                var wake = _wake.WaitAsync(delayCts.Token);
                var timer = Task.Delay(delay, _time, delayCts.Token);
                await Task.WhenAny(wake, timer).ConfigureAwait(false);
                await delayCts.CancelAsync().ConfigureAwait(false);
                try
                {
                    await Task.WhenAll(wake, timer).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogLoopFailed(_logger, ex);
            }
        }
    }

    private void StartEligibleTransfers(CancellationToken token)
    {
        var now = _time.GetUtcNow();
        var connectivity = _connectivity.Current;
        foreach (var record in _records.Values.OrderBy(r => r.CreatedAt))
        {
            if (_active.Count >= Math.Max(1, _options.MaxConcurrentTransfers))
            {
                return;
            }

            if (_active.ContainsKey(record.Id) ||
                record.Status is not (TransferStatus.Queued or TransferStatus.Retrying or TransferStatus.WaitingForNetwork) ||
                record.NextAttemptAt is { } next && next > now)
            {
                continue;
            }

            if (!NetworkAllows(record, connectivity))
            {
                if (record.Status != TransferStatus.WaitingForNetwork)
                {
                    _ = UpdateAsync(record, r => r.Status = TransferStatus.WaitingForNetwork, token).AsTask();
                }

                continue;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _active[record.Id] = cts;
            _ = Task.Run(() => RunAsync(record, cts), CancellationToken.None);
        }
    }

    private static bool NetworkAllows(TransferRecord record, ConnectivitySnapshot snapshot)
    {
        if (snapshot.Access == NetworkAccess.Unknown)
        {
            return true;
        }

        if (!snapshot.IsOnline)
        {
            return false;
        }

        return !record.RequireUnmeteredNetwork || snapshot.IsMetered switch
        {
            false => true,
            true => false,
            null => snapshot.ConnectionTypes.Any(t => t is ConnectionType.WiFi or ConnectionType.Ethernet),
        };
    }

    private TimeSpan GetNextDelay()
    {
        var now = _time.GetUtcNow();
        var next = _records.Values
            .Where(r => r.Status is TransferStatus.Retrying or TransferStatus.Queued && r.NextAttemptAt is not null)
            .Select(r => r.NextAttemptAt!.Value)
            .DefaultIfEmpty(now + TimeSpan.FromSeconds(30))
            .Min();
        var delay = next - now;
        return delay < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay;
    }

    private async Task RunAsync(TransferRecord record, CancellationTokenSource cts)
    {
        try
        {
            await UpdateAsync(record, r =>
            {
                r.Status = TransferStatus.Running;
                r.ErrorMessage = null;
            }, cts.Token).ConfigureAwait(false);

            if (record.Direction == TransferDirection.Download)
            {
                await DownloadAsync(record, cts.Token).ConfigureAwait(false);
            }
            else
            {
                await UploadAsync(record, cts.Token).ConfigureAwait(false);
            }

            await UpdateAsync(record, r =>
            {
                r.Status = TransferStatus.Completed;
                r.CompletedAt = _time.GetUtcNow();
                r.NextAttemptAt = null;
            }, CancellationToken.None).ConfigureAwait(false);
            LogCompleted(_logger, record.Id, record.Direction);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Cancelled by the user (status already set) or by shutdown (left Running and resumed on next launch).
        }
        catch (Exception ex)
        {
            await HandleFailureAsync(record, ex).ConfigureAwait(false);
        }
        finally
        {
            _active.TryRemove(record.Id, out _);
            cts.Dispose();
            Wake();
        }
    }

    private async Task HandleFailureAsync(TransferRecord record, Exception ex)
    {
        var (transient, retryAfter, statusCode) = Classify(ex);
        if (statusCode is not null)
        {
            record.HttpStatusCode = statusCode;
        }

        if (transient && !_connectivity.Current.IsOnline && _connectivity.Current.Access != NetworkAccess.Unknown)
        {
            await UpdateAsync(record, r =>
            {
                r.Status = TransferStatus.WaitingForNetwork;
                r.ErrorMessage = ex.Message;
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (transient && record.Retries < record.MaxRetries)
        {
            var backoff = retryAfter ?? TimeSpan.FromSeconds(Math.Min(Math.Pow(2, record.Retries + 1), _options.MaximumBackoff.TotalSeconds));
            await UpdateAsync(record, r =>
            {
                r.Retries++;
                r.Status = TransferStatus.Retrying;
                r.ErrorMessage = ex.Message;
                r.NextAttemptAt = _time.GetUtcNow() + backoff;
            }, CancellationToken.None).ConfigureAwait(false);
            LogRetrying(_logger, record.Id, backoff, ex.Message);
            return;
        }

        await UpdateAsync(record, r =>
        {
            r.Status = TransferStatus.Failed;
            r.ErrorMessage = ex.Message;
            r.CompletedAt = _time.GetUtcNow();
        }, CancellationToken.None).ConfigureAwait(false);
        LogFailed(_logger, record.Id, ex);
    }

    private static (bool Transient, TimeSpan? RetryAfter, int? StatusCode) Classify(Exception ex) => ex switch
    {
        TransferHttpException http => (IsTransient(http.StatusCode), http.RetryAfter, (int)http.StatusCode),
        HttpRequestException hre when hre.StatusCode is { } code => (IsTransient(code), null, (int)code),
        HttpRequestException or IOException or TimeoutException or TaskCanceledException => (true, null, null),
        _ => (false, null, null),
    };

    private static bool IsTransient(HttpStatusCode code) =>
        code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or HttpStatusCode.InternalServerError;

    private async Task DownloadAsync(TransferRecord record, CancellationToken token)
    {
        var partial = record.LocalPath + PartialSuffix;
        var directory = Path.GetDirectoryName(record.LocalPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0L;
        using var request = new HttpRequestMessage(HttpMethod.Get, record.Uri);
        ApplyHeaders(request, record.Headers);
        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
            if (!string.IsNullOrEmpty(record.ETag) && EntityTagHeaderValue.TryParse(record.ETag, out var etag) && !etag.IsWeak)
            {
                request.Headers.IfRange = new RangeConditionHeaderValue(etag);
            }
            else if (record.LastModified is { } lastModified)
            {
                request.Headers.IfRange = new RangeConditionHeaderValue(lastModified);
            }
        }

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        record.HttpStatusCode = (int)response.StatusCode;

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
        {
            var total = response.Content.Headers.ContentRange?.Length;
            if (total == existing)
            {
                File.Move(partial, record.LocalPath, overwrite: true);
                record.BytesTransferred = existing;
                record.TotalBytes = existing;
                return;
            }

            TryDelete(partial);
            throw new TransferHttpException(HttpStatusCode.ServiceUnavailable, null, "The partial download no longer matches the server file; restarting.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new TransferHttpException(response.StatusCode, GetRetryAfter(response), $"The server returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        var append = response.StatusCode == HttpStatusCode.PartialContent && existing > 0;
        if (!append)
        {
            existing = 0;
        }

        record.ETag = response.Headers.ETag?.ToString();
        record.LastModified = response.Content.Headers.LastModified;
        record.TotalBytes = append
            ? response.Content.Headers.ContentRange?.Length ?? existing + response.Content.Headers.ContentLength
            : response.Content.Headers.ContentLength;
        record.BytesTransferred = existing;
        await PersistAsync(record, token).ConfigureAwait(false);
        Raise(record);

        var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using (body.ConfigureAwait(false))
        {
            var file = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, _options.BufferSize, FileOptions.Asynchronous);
            await using (file.ConfigureAwait(false))
            {
                await CopyWithProgressAsync(body, file, record, token).ConfigureAwait(false);
                await file.FlushAsync(token).ConfigureAwait(false);
            }
        }

        if (record.TotalBytes is { } expected && record.BytesTransferred < expected)
        {
            throw new IOException($"The connection closed after {record.BytesTransferred} of {expected} bytes.");
        }

        File.Move(partial, record.LocalPath, overwrite: true);
    }

    private async Task UploadAsync(TransferRecord record, CancellationToken token)
    {
        if (!File.Exists(record.LocalPath))
        {
            throw new FileNotFoundException("The file to upload no longer exists.", record.LocalPath);
        }

        var source = new FileStream(record.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, _options.BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (source.ConfigureAwait(false))
        {
            record.TotalBytes = source.Length;
            record.BytesTransferred = 0;
            HttpContent content = new ProgressStreamContent(source, _options.BufferSize, sent =>
            {
                record.BytesTransferred = sent;
                ReportProgress(record);
            });
            content.Headers.ContentType = MediaTypeHeaderValue.TryParse(record.ContentType, out var mediaType) ? mediaType : new MediaTypeHeaderValue(MimeTypes.OctetStream);

            if (!string.IsNullOrEmpty(record.MultipartFieldName))
            {
                var multipart = new MultipartFormDataContent();
                multipart.Add(content, record.MultipartFieldName, Path.GetFileName(record.LocalPath));
                content = multipart;
            }

            using (content)
            using (var request = new HttpRequestMessage(new HttpMethod(record.Method ?? "POST"), record.Uri) { Content = content })
            {
                ApplyHeaders(request, record.Headers);
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                record.HttpStatusCode = (int)response.StatusCode;
                if (!response.IsSuccessStatusCode)
                {
                    throw new TransferHttpException(response.StatusCode, GetRetryAfter(response), $"The server returned {(int)response.StatusCode} {response.ReasonPhrase}.");
                }
            }
        }
    }

    private async Task CopyWithProgressAsync(Stream source, Stream destination, TransferRecord record, CancellationToken token)
    {
        var buffer = new byte[_options.BufferSize];
        var lastPersist = _time.GetTimestamp();
        int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            record.BytesTransferred += read;
            ReportProgress(record);
            if (_time.GetElapsedTime(lastPersist) >= _options.PersistInterval)
            {
                lastPersist = _time.GetTimestamp();
                await destination.FlushAsync(token).ConfigureAwait(false);
                await PersistAsync(record, token).ConfigureAwait(false);
            }
        }
    }

    private void ReportProgress(TransferRecord record)
    {
        var now = _time.GetTimestamp();
        if (_time.GetElapsedTime(record.LastProgressTimestamp, now) >= _options.ProgressInterval)
        {
            record.LastProgressTimestamp = now;
            Raise(record);
        }
    }

    private static void ApplyHeaders(HttpRequestMessage request, Dictionary<string, string> headers)
    {
        foreach (var (name, value) in headers)
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                request.Content?.Headers.TryAddWithoutValidation(name, value);
            }
        }
    }

    private TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var wait = date - _time.GetUtcNow();
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private async ValueTask UpdateAsync(TransferRecord record, Action<TransferRecord> update, CancellationToken token)
    {
        update(record);
        await PersistAsync(record, token).ConfigureAwait(false);
        Raise(record);
        var info = record.ToInfo();
        if (info.IsFinished && _waiters.TryRemove(record.Id, out var waiter))
        {
            waiter.TrySetResult(info);
        }
    }

    private ValueTask PersistAsync(TransferRecord record, CancellationToken token) =>
        _store.SetAsync(Prefix + record.Id, JsonSerializer.Serialize(record, CoreJsonContext.Default.TransferRecord), token);

    private void Raise(TransferRecord record)
    {
        try
        {
            TransferChanged?.Invoke(this, record.ToInfo());
        }
        catch (Exception ex)
        {
            LogLoopFailed(_logger, ex);
        }
    }

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        if (e.CameOnline)
        {
            foreach (var record in _records.Values.Where(r => r.Status == TransferStatus.WaitingForNetwork))
            {
                record.NextAttemptAt = null;
            }
        }

        Wake();
    }

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [LoggerMessage(EventId = 1501, Level = LogLevel.Error, Message = "The transfer loop failed.")]
    private static partial void LogLoopFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1502, Level = LogLevel.Information, Message = "Transfer {TransferId} ({Direction}) completed.")]
    private static partial void LogCompleted(ILogger logger, string transferId, TransferDirection direction);

    [LoggerMessage(EventId = 1503, Level = LogLevel.Warning, Message = "Transfer {TransferId} will retry in {Delay}: {Reason}")]
    private static partial void LogRetrying(ILogger logger, string transferId, TimeSpan delay, string reason);

    [LoggerMessage(EventId = 1504, Level = LogLevel.Error, Message = "Transfer {TransferId} failed permanently.")]
    private static partial void LogFailed(ILogger logger, string transferId, Exception exception);

    private sealed class TransferHttpException(HttpStatusCode statusCode, TimeSpan? retryAfter, string message) : Exception(message)
    {
        public HttpStatusCode StatusCode { get; } = statusCode;

        public TimeSpan? RetryAfter { get; } = retryAfter;
    }

    private sealed class ProgressStreamContent(Stream source, int bufferSize, Action<long> progress) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await SerializeToStreamAsync(stream, context, CancellationToken.None).ConfigureAwait(false);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            source.Position = 0;
            var buffer = new byte[bufferSize];
            long sent = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                sent += read;
                progress(sent);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = source.Length;
            return true;
        }
    }
}

/// <summary>Persisted transfer state.</summary>
internal sealed class TransferRecord
{
    public required string Id { get; set; }

    public TransferDirection Direction { get; set; }

    public required string Uri { get; set; }

    public required string LocalPath { get; set; }

    public TransferStatus Status { get; set; }

    public long BytesTransferred { get; set; }

    public long? TotalBytes { get; set; }

    public int Retries { get; set; }

    public int MaxRetries { get; set; }

    public int? HttpStatusCode { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset? NextAttemptAt { get; set; }

    public Dictionary<string, string> Headers { get; set; } = [];

    public string? Method { get; set; }

    public string? ContentType { get; set; }

    public string? MultipartFieldName { get; set; }

    public bool RequireUnmeteredNetwork { get; set; }

    public string? ETag { get; set; }

    public DateTimeOffset? LastModified { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public long LastProgressTimestamp { get; set; }

    public TransferInfo ToInfo() => new()
    {
        Id = Id,
        Direction = Direction,
        Uri = new Uri(Uri),
        LocalPath = LocalPath,
        Status = Status,
        BytesTransferred = BytesTransferred,
        TotalBytes = TotalBytes,
        Retries = Retries,
        HttpStatusCode = HttpStatusCode,
        ErrorMessage = ErrorMessage,
        CreatedAt = CreatedAt,
        CompletedAt = CompletedAt,
    };
}
