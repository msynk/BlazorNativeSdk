using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;
using Foundation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>
/// <see cref="ITransferService"/> backed by a background <c>NSURLSession</c>: iOS performs the transfers in a system process
/// while the app is suspended or terminated, and launches the app in the background to deliver the results.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Transfers are persisted in <see cref="INativeStateStore"/> and survive app termination. When the user force-quits the
/// app, iOS cancels its background transfers; they are retried when the app runs again.</item>
/// <item>The system decides when transfers run (Low Power Mode, network quality and thermal state defer them); there is no
/// exact timing. Network loss is handled by the system; failed transfers are retried with exponential backoff
/// (honoring <c>Retry-After</c>) through new tasks with an <c>earliestBeginDate</c>, so retries also happen while the app is
/// suspended.</item>
/// <item>Interrupted downloads resume from the system's resume data when the server supports it. Uploads always start again
/// from the beginning; multipart bodies are written to a file first because background sessions upload from files only.</item>
/// <item>Forward <c>application:handleEventsForBackgroundURLSession:completionHandler:</c> with
/// <see cref="AppleBackgroundTransfers.HandleEventsForBackgroundUrl"/>.</item>
/// </list>
/// </remarks>
public sealed partial class AppleBackgroundTransferService : ITransferService, INativeSdkStartupTask, IDisposable
{
    private const string AdapterName = "ios-urlsession";
    private const string Prefix = "bns.urlsession.";
    private static readonly TimeSpan s_progressInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan s_finishedRetention = TimeSpan.FromDays(7);

    // Give a freshly attached session time to deliver events queued while the app was not running before looking for
    // transfers that lost their task.
    private static readonly TimeSpan s_reconcileDelay = TimeSpan.FromSeconds(10);

    private readonly INativeStateStore _store;
    private readonly IAppStorage _storage;
    private readonly IConnectivityService _connectivity;
    private readonly ILogger<AppleBackgroundTransferService> _logger;
    private readonly TimeProvider _time;
    private readonly string _sessionIdentifier;
    private readonly ConcurrentDictionary<string, AppleTransferRecord> _records = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TransferInfo>> _waiters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ulong, string?> _downloads = new();
    private readonly Channel<Func<Task>> _events = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private NSUrlSession? _session;
    private string? _workDirectory;
    private int _disposed;

    /// <summary>Creates the service.</summary>
    public AppleBackgroundTransferService(
        INativeStateStore store,
        IAppStorage storage,
        IConnectivityService connectivity,
        IOptions<AppleNativeOptions> options,
        ILogger<AppleBackgroundTransferService> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _storage = storage;
        _connectivity = connectivity;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _sessionIdentifier = string.IsNullOrWhiteSpace(options.Value.BackgroundTransferSessionIdentifier)
            ? AppleBackgroundTransfers.DefaultSessionIdentifier
            : options.Value.BackgroundTransferSessionIdentifier;
    }

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.Transfers;

    /// <inheritdoc />
    public TransferPolicy Policy { get; } = new()
    {
        Adapter = AdapterName,
        ContinuesInBackground = true,
        ContinuesAfterTermination = true,
        ResumesDownloads = true,
        Summary = "iOS performs transfers in a background URL session while the app is suspended or closed and relaunches the app to deliver the results. " +
                  "The system decides when transfers run. Transfers stop when the user force-quits the app and are retried when it runs again. " +
                  "Downloads resume when the server supports it; uploads start again from the beginning.",
    };

    /// <inheritdoc />
    public event EventHandler<TransferInfo>? TransferChanged;

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Available(Capability, AdapterName,
            "resume-downloads", "retry", "persisted", "continues-in-background", "continues-after-termination"));

    /// <inheritdoc />
    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is not null || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            await LoadAsync(cancellationToken).ConfigureAwait(false);
            _ = Task.Run(PumpAsync, CancellationToken.None);

            // Events the system queued while the app was not running are delivered as soon as the session exists.
            _session = AppleBackgroundTransfers.Attach(_sessionIdentifier, this);
            _connectivity.Changed += OnConnectivityChanged;
        }
        finally
        {
            _gate.Release();
        }

        _ = ReconcileAsync();
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

        return await EnqueueAsync(new AppleTransferRecord
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

        return await EnqueueAsync(new AppleTransferRecord
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
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<NativeResult<bool>> CancelAsync(string transferId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transferId);
        await StartAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_records.TryGetValue(transferId, out var record))
            {
                return false;
            }

            if (record.IsFinished)
            {
                return true;
            }

            // Mark first: the task's NSURLErrorCancelled completion is then recognized as requested by the app.
            record.Status = TransferStatus.Cancelled;
            record.CompletedAt = _time.GetUtcNow();
            foreach (var task in await GetTasksAsync().ConfigureAwait(false))
            {
                if (string.Equals(task.TaskDescription, transferId, StringComparison.Ordinal))
                {
                    task.Cancel();
                }
            }

            DeleteWorkFiles(record);
            await SaveAsync(record).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<NativeResult<TransferInfo>> RetryAsync(string transferId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transferId);
        await StartAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_records.TryGetValue(transferId, out var record))
            {
                return NativeError.Failed(Capability, NativeErrorCodes.NotFound, $"Transfer '{transferId}' does not exist.");
            }

            if (record.Status is not (TransferStatus.Failed or TransferStatus.Cancelled))
            {
                return record.ToInfo();
            }

            if (record.Direction == TransferDirection.Upload && !File.Exists(record.LocalPath))
            {
                return NativeError.Failed(Capability, NativeErrorCodes.NotFound, $"The file '{record.LocalPath}' no longer exists.");
            }

            record.Status = InitialStatus();
            record.Retries = 0;
            record.ErrorMessage = null;
            record.HttpStatusCode = null;
            record.CompletedAt = null;
            record.NextAttemptAt = null;
            record.BytesTransferred = 0;
            if (StartTask(record, earliestBegin: null) is { } failed)
            {
                Fail(record, failed.Message, _time.GetUtcNow());
                await SaveAsync(record).ConfigureAwait(false);
                return failed;
            }

            await SaveAsync(record).ConfigureAwait(false);
            return record.ToInfo();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<TransferInfo>> GetTransfersAsync(CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        return [.. _records.Values.OrderBy(r => r.CreatedAt).Select(r => r.ToInfo())];
    }

    /// <inheritdoc />
    public async ValueTask<NativeResult<TransferInfo>> WaitForCompletionAsync(string transferId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transferId);
        await StartAsync(cancellationToken).ConfigureAwait(false);
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
    /// <remarks>The background session is kept: transfers continue, and a new container takes the session over.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        AppleBackgroundTransfers.Detach(_sessionIdentifier, this);
        _connectivity.Changed -= OnConnectivityChanged;
        _events.Writer.TryComplete();
        _shutdown.Cancel();
    }

    /// <summary>Progress of a download or upload task (session queue).</summary>
    internal void OnProgress(NSUrlSessionTask task, long transferred, long expected)
    {
        if (Find(task) is not { } record)
        {
            return;
        }

        record.BytesTransferred = transferred;
        if (expected > 0)
        {
            record.TotalBytes = expected;
        }

        if (record.Status != TransferStatus.Running)
        {
            var taskId = (ulong)task.TaskIdentifier;
            Post(async () =>
            {
                if (!record.IsFinished && record.TaskId == taskId && record.Status != TransferStatus.Running)
                {
                    record.Status = TransferStatus.Running;
                    record.NextAttemptAt = null;
                    await SaveAsync(record).ConfigureAwait(false);
                }
            });
            return;
        }

        var now = _time.GetTimestamp();
        if (_time.GetElapsedTime(record.LastProgressTimestamp, now) >= s_progressInterval)
        {
            record.LastProgressTimestamp = now;
            Raise(record);
        }
    }

    /// <summary>
    /// A download task finished receiving its body (session queue). The file at <paramref name="location"/> is deleted when this
    /// method returns, so it is moved to the destination here.
    /// </summary>
    internal void OnDownloadFinished(NSUrlSessionDownloadTask task, NSUrl location)
    {
        if (Find(task) is not { } record || location.Path is not { } source)
        {
            return;
        }

        // An error body (404 page, ...) must not replace the destination; the completion handles the status.
        if (task.Response is not NSHttpUrlResponse { StatusCode: >= 200 and < 300 })
        {
            return;
        }

        string? error = null;
        try
        {
            if (Path.GetDirectoryName(record.LocalPath) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            File.Move(source, record.LocalPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"The downloaded file could not be moved to '{record.LocalPath}': {ex.Message}";
        }

        _downloads[(ulong)task.TaskIdentifier] = error;
    }

    /// <summary>A task completed (session queue). Its outcome is processed in order with the other session events.</summary>
    internal void OnCompleted(NSUrlSessionTask task, NSError? error)
    {
        var response = task.Response as NSHttpUrlResponse;
        var completion = new TaskCompletion(
            task.TaskDescription,
            (ulong)task.TaskIdentifier,
            (int)(response?.StatusCode ?? 0),
            response?.GetHttpHeaderValue("Retry-After"),
            error?.Domain,
            error?.Code ?? 0,
            error?.LocalizedDescription,
            (error?.UserInfo?[NSUrlSessionDownloadDelegate.TaskResumeDataKey] as NSData)?.ToArray());
        Post(() => CompleteAsync(completion));
    }

    /// <summary>iOS delivered every queued event of the session (session queue).</summary>
    internal void OnBackgroundEventsFinished() => Post(() =>
    {
        // Runs after the events posted before it were processed and persisted.
        AppleBackgroundTransfers.CompleteBackgroundEvents(_sessionIdentifier);
        return Task.CompletedTask;
    });

    private async Task CompleteAsync(TaskCompletion completion)
    {
        var delivered = _downloads.TryRemove(completion.TaskId, out var moveError);
        if (completion.TransferId is null || !_records.TryGetValue(completion.TransferId, out var record) ||
            record.IsFinished || record.TaskId != completion.TaskId)
        {
            // Cancelled by the app, or a task replaced by a retry.
            return;
        }

        var now = _time.GetUtcNow();
        AppleTransferOutcome outcome;
        string? message;
        TimeSpan? retryAfter = null;
        if (completion.ErrorDomain is null)
        {
            record.HttpStatusCode = completion.StatusCode;
            outcome = AppleTransferMapping.ClassifyResponse(completion.StatusCode, record.Retries, record.MaxRetries);
            message = outcome == AppleTransferOutcome.Completed ? null : $"The server returned {completion.StatusCode}.";
            retryAfter = AppleTransferMapping.ParseRetryAfter(completion.RetryAfter, now);
            if (outcome == AppleTransferOutcome.Completed && record.Direction == TransferDirection.Download && moveError is not null)
            {
                outcome = AppleTransferOutcome.Failed;
                message = moveError;
            }
            else if (outcome == AppleTransferOutcome.Completed && record.Direction == TransferDirection.Download && !delivered)
            {
                outcome = record.Retries < record.MaxRetries ? AppleTransferOutcome.Retry : AppleTransferOutcome.Failed;
                message = "The system did not deliver the downloaded file.";
            }
        }
        else
        {
            if (completion.StatusCode != 0)
            {
                record.HttpStatusCode = completion.StatusCode;
            }

            outcome = AppleTransferMapping.ClassifyError(completion.ErrorDomain, completion.ErrorCode, record.Retries, record.MaxRetries);
            message = completion.ErrorMessage ?? $"{completion.ErrorDomain} {completion.ErrorCode}";
        }

        if (completion.ResumeData is { Length: > 0 } resumeData && outcome == AppleTransferOutcome.Retry)
        {
            SaveResumeData(record, resumeData);
        }
        else
        {
            TryDelete(ResumeDataPath(record));
        }

        switch (outcome)
        {
            case AppleTransferOutcome.Completed:
                record.Status = TransferStatus.Completed;
                record.CompletedAt = now;
                record.NextAttemptAt = null;
                record.ErrorMessage = null;
                if (record.Direction == TransferDirection.Download && File.Exists(record.LocalPath))
                {
                    record.TotalBytes = new FileInfo(record.LocalPath).Length;
                }

                if (record.TotalBytes is { } total)
                {
                    record.BytesTransferred = total;
                }

                DeleteWorkFiles(record);
                LogCompleted(_logger, record.Id, record.Direction);
                break;

            case AppleTransferOutcome.Retry:
                var delay = AppleTransferMapping.GetBackoff(record.Retries, retryAfter);
                record.Retries++;
                record.Status = TransferStatus.Retrying;
                record.ErrorMessage = message;
                record.NextAttemptAt = now + delay;
                LogRetrying(_logger, record.Id, delay, message ?? string.Empty);
                if (StartTask(record, record.NextAttemptAt) is { } failed)
                {
                    Fail(record, failed.Message, now);
                }

                break;

            default:
                Fail(record, message, now);
                break;
        }

        await SaveAsync(record).ConfigureAwait(false);
    }

    private void Fail(AppleTransferRecord record, string? message, DateTimeOffset now)
    {
        record.Status = TransferStatus.Failed;
        record.ErrorMessage = message;
        record.CompletedAt = now;
        record.NextAttemptAt = null;
        DeleteWorkFiles(record);
        LogFailed(_logger, record.Id, message ?? string.Empty);
    }

    private async ValueTask<NativeResult<TransferInfo>> EnqueueAsync(AppleTransferRecord record, CancellationToken cancellationToken)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_records.TryGetValue(record.Id, out var existing) && !existing.IsFinished)
            {
                return existing.ToInfo();
            }

            if (_session is null)
            {
                return NativeError.Unavailable(Capability, NativeErrorCodes.HostNotReady, "The transfer service was disposed.");
            }

            record.Status = InitialStatus();
            record.CreatedAt = _time.GetUtcNow();
            if (StartTask(record, earliestBegin: null) is { } failed)
            {
                return failed;
            }

            _records[record.Id] = record;
            await SaveAsync(record).ConfigureAwait(false);
            return record.ToInfo();
        }
        finally
        {
            _gate.Release();
        }
    }

    // Creates and resumes the session task of a transfer. Call under _gate.
    private NativeError? StartTask(AppleTransferRecord record, DateTimeOffset? earliestBegin)
    {
        try
        {
            var session = _session ?? throw new ObjectDisposedException(nameof(AppleBackgroundTransferService));
            NSUrlSessionTask task;
            if (record.Direction == TransferDirection.Download)
            {
                using var resumeData = LoadResumeData(record);
                if (resumeData is not null)
                {
                    task = session.CreateDownloadTask(resumeData);
                }
                else
                {
                    using var request = BuildRequest(record);
                    task = session.CreateDownloadTask(request);
                }
            }
            else
            {
                var body = PrepareUploadBody(record);
                using var request = BuildRequest(record);
                using var file = NSUrl.FromFilename(body);
                task = session.CreateUploadTask(request, file);
            }

            task.TaskDescription = record.Id;
            if (earliestBegin is { } begin && begin > _time.GetUtcNow())
            {
                task.EarliestBeginDate = (NSDate)begin.UtcDateTime;
            }

            record.TaskId = (ulong)task.TaskIdentifier;
            task.Resume();
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStartFailed(_logger, record.Id, ex);
            return NativeError.FromException(Capability, ex);
        }
    }

    private static NSMutableUrlRequest BuildRequest(AppleTransferRecord record)
    {
        using var url = new NSUrl(record.Uri);
        var request = new NSMutableUrlRequest(url) { HttpMethod = record.Method ?? "GET" };
        if (record.Direction == TransferDirection.Upload)
        {
            request["Content-Type"] = record.BodyContentType ?? record.ContentType ?? MimeTypes.OctetStream;
        }

        foreach (var (name, value) in record.Headers)
        {
            request[name] = value;
        }

        if (record.RequireUnmeteredNetwork)
        {
            request.AllowsExpensiveNetworkAccess = false;
        }

        return request;
    }

    // Background sessions upload from files only: a raw upload sends the source file, a multipart upload a body file
    // written next to the transfer state.
    private string PrepareUploadBody(AppleTransferRecord record)
    {
        if (string.IsNullOrEmpty(record.MultipartFieldName))
        {
            if (!File.Exists(record.LocalPath))
            {
                throw new FileNotFoundException("The file to upload no longer exists.", record.LocalPath);
            }

            record.TotalBytes = new FileInfo(record.LocalPath).Length;
            return record.LocalPath;
        }

        var bodyPath = BodyPath(record);
        if (record.BodyContentType is not null && File.Exists(bodyPath))
        {
            return bodyPath;
        }

        var boundary = "bns-" + Guid.NewGuid().ToString("N");
        using (var source = new FileStream(record.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var body = new FileStream(bodyPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            body.Write(AppleTransferMapping.MultipartPreamble(boundary, record.MultipartFieldName, Path.GetFileName(record.LocalPath),
                record.ContentType ?? MimeTypes.OctetStream));
            source.CopyTo(body);
            body.Write(AppleTransferMapping.MultipartEpilogue(boundary));
        }

        record.BodyContentType = AppleTransferMapping.MultipartContentType(boundary);
        record.TotalBytes = new FileInfo(bodyPath).Length;
        return bodyPath;
    }

    private async Task ReconcileAsync()
    {
        try
        {
            await Task.Delay(s_reconcileDelay, _time, _shutdown.Token).ConfigureAwait(false);
            var known = _records.Values.Where(r => !r.IsFinished).ToDictionary(r => r.Id, r => r.TaskId, StringComparer.Ordinal);
            var live = new Dictionary<string, ulong>(StringComparer.Ordinal);
            foreach (var task in await GetTasksAsync().ConfigureAwait(false))
            {
                if (task.TaskDescription is not { } id || task.State == NSUrlSessionTaskState.Completed)
                {
                    continue;
                }

                if (!_records.TryGetValue(id, out var owner) || owner.IsFinished || owner.TaskId != (ulong)task.TaskIdentifier)
                {
                    // Left over from a transfer that finished, was cancelled or got a newer task.
                    task.Cancel();
                    continue;
                }

                live[id] = (ulong)task.TaskIdentifier;
            }

            await _gate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                foreach (var (id, taskId) in known)
                {
                    if (live.ContainsKey(id) || !_records.TryGetValue(id, out var record) || record.IsFinished || record.TaskId != taskId)
                    {
                        continue;
                    }

                    // The task is gone (the app ended before it was created, or the system dropped it): start it again.
                    LogRecreated(_logger, id);
                    if (StartTask(record, record.NextAttemptAt) is { } failed)
                    {
                        Fail(record, failed.Message, _time.GetUtcNow());
                    }

                    await SaveAsync(record).ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            LogEventFailed(_logger, ex);
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        foreach (var key in await _store.GetKeysAsync(Prefix, cancellationToken).ConfigureAwait(false))
        {
            var json = await _store.GetAsync(key, cancellationToken).ConfigureAwait(false);
            AppleTransferRecord? record;
            try
            {
                record = json is null ? null : JsonSerializer.Deserialize(json, AppleTransferJsonContext.Default.AppleTransferRecord);
            }
            catch (JsonException)
            {
                record = null;
            }

            if (record is null || record.IsFinished && record.CompletedAt is { } done && now - done > s_finishedRetention)
            {
                await _store.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
                continue;
            }

            _records[record.Id] = record;
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var work in _events.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                await _gate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                try
                {
                    await work().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogEventFailed(_logger, ex);
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Post(Func<Task> work)
    {
        if (!_events.Writer.TryWrite(work))
        {
            LogEventDropped(_logger);
        }
    }

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e) => Post(async () =>
    {
        var snapshot = _connectivity.Current;
        var offline = snapshot.Access != NetworkAccess.Unknown && !snapshot.IsOnline;
        foreach (var record in _records.Values)
        {
            var status = record.Status switch
            {
                TransferStatus.Queued or TransferStatus.Running when offline => TransferStatus.WaitingForNetwork,
                TransferStatus.WaitingForNetwork when !offline => TransferStatus.Queued,
                _ => record.Status,
            };
            if (status != record.Status)
            {
                record.Status = status;
                await SaveAsync(record).ConfigureAwait(false);
            }
        }
    });

    private TransferStatus InitialStatus()
    {
        var snapshot = _connectivity.Current;
        return snapshot.Access != NetworkAccess.Unknown && !snapshot.IsOnline ? TransferStatus.WaitingForNetwork : TransferStatus.Queued;
    }

    private AppleTransferRecord? Find(NSUrlSessionTask task) =>
        task.TaskDescription is { } id && _records.TryGetValue(id, out var record) && !record.IsFinished && record.TaskId == (ulong)task.TaskIdentifier
            ? record
            : null;

    private async Task<NSUrlSessionTask[]> GetTasksAsync() =>
        _session is { } session ? await session.GetAllTasksAsync().ConfigureAwait(false) ?? [] : [];

    private NativeResult<string> ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "A file path is required.");
        }

        var root = _storage.GetDirectory(AppDirectory.AppData);
        if (!root.IsSuccess)
        {
            return root.Error;
        }

        return AppleTransferMapping.ResolvePath(path, root.Value) is { } full
            ? full
            : NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "Relative paths must stay inside the app data directory.");
    }

    private string WorkDirectory()
    {
        if (_workDirectory is null)
        {
            var root = _storage.GetDirectory(AppDirectory.AppData);
            var directory = Path.Combine(root.IsSuccess ? root.Value : Path.GetTempPath(), "bns-transfers");
            Directory.CreateDirectory(directory);
            _workDirectory = directory;
        }

        return _workDirectory;
    }

    private string ResumeDataPath(AppleTransferRecord record) => Path.Combine(WorkDirectory(), record.Id + ".resume");

    private string BodyPath(AppleTransferRecord record) => Path.Combine(WorkDirectory(), record.Id + ".body");

    private NSData? LoadResumeData(AppleTransferRecord record)
    {
        var path = ResumeDataPath(record);
        try
        {
            return File.Exists(path) ? NSData.FromArray(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SaveResumeData(AppleTransferRecord record, byte[] data)
    {
        try
        {
            File.WriteAllBytes(ResumeDataPath(record), data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The download then starts again from the beginning.
            LogEventFailed(_logger, ex);
        }
    }

    private void DeleteWorkFiles(AppleTransferRecord record)
    {
        TryDelete(ResumeDataPath(record));
        TryDelete(BodyPath(record));
        record.BodyContentType = null;
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

    private async Task SaveAsync(AppleTransferRecord record)
    {
        await _store.SetAsync(Prefix + record.Id, JsonSerializer.Serialize(record, AppleTransferJsonContext.Default.AppleTransferRecord), CancellationToken.None)
            .ConfigureAwait(false);
        Raise(record);
        var info = record.ToInfo();
        if (info.IsFinished && _waiters.TryRemove(record.Id, out var waiter))
        {
            waiter.TrySetResult(info);
        }
    }

    private void Raise(AppleTransferRecord record)
    {
        try
        {
            TransferChanged?.Invoke(this, record.ToInfo());
        }
        catch (Exception ex)
        {
            LogEventFailed(_logger, ex);
        }
    }

    private NativeResult<TransferInfo> ToResult(TransferInfo info) => info.Status switch
    {
        TransferStatus.Completed => info,
        TransferStatus.Cancelled => NativeError.Cancelled(Capability, "The transfer was cancelled."),
        _ => NativeError.Failed(Capability, NativeErrorCodes.PlatformError, info.ErrorMessage ?? "The transfer failed."),
    };

    private static bool IsHttp(Uri uri) => uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    [LoggerMessage(EventId = 3101, Level = LogLevel.Information, Message = "Transfer {TransferId} ({Direction}) completed.")]
    private static partial void LogCompleted(ILogger logger, string transferId, TransferDirection direction);

    [LoggerMessage(EventId = 3102, Level = LogLevel.Warning, Message = "Transfer {TransferId} will retry in {Delay}: {Reason}")]
    private static partial void LogRetrying(ILogger logger, string transferId, TimeSpan delay, string reason);

    [LoggerMessage(EventId = 3103, Level = LogLevel.Error, Message = "Transfer {TransferId} failed permanently: {Reason}")]
    private static partial void LogFailed(ILogger logger, string transferId, string reason);

    [LoggerMessage(EventId = 3104, Level = LogLevel.Error, Message = "The session task of transfer {TransferId} could not be created.")]
    private static partial void LogStartFailed(ILogger logger, string transferId, Exception exception);

    [LoggerMessage(EventId = 3105, Level = LogLevel.Information, Message = "Transfer {TransferId} had no session task; a new one was created.")]
    private static partial void LogRecreated(ILogger logger, string transferId);

    [LoggerMessage(EventId = 3106, Level = LogLevel.Error, Message = "Processing a background URL session event failed.")]
    private static partial void LogEventFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3107, Level = LogLevel.Warning, Message = "A background URL session event arrived after the transfer service was disposed.")]
    private static partial void LogEventDropped(ILogger logger);

    private sealed record TaskCompletion(
        string? TransferId,
        ulong TaskId,
        int StatusCode,
        string? RetryAfter,
        string? ErrorDomain,
        long ErrorCode,
        string? ErrorMessage,
        byte[]? ResumeData);
}

/// <summary>Persisted state of a background <c>URLSession</c> transfer.</summary>
internal sealed class AppleTransferRecord
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

    /// <summary>The <c>taskIdentifier</c> of the current session task; completions of older tasks are ignored.</summary>
    public ulong TaskId { get; set; }

    /// <summary>The <c>Content-Type</c> of the multipart body file, once written.</summary>
    public string? BodyContentType { get; set; }

    [JsonIgnore]
    public long LastProgressTimestamp { get; set; }

    [JsonIgnore]
    public bool IsFinished => Status is TransferStatus.Completed or TransferStatus.Failed or TransferStatus.Cancelled;

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

/// <summary>Source-generated JSON metadata for transfer records (trimming and AOT safe).</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppleTransferRecord))]
internal sealed partial class AppleTransferJsonContext : JsonSerializerContext
{
}
