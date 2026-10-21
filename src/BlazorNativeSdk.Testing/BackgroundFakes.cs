using System.Collections.Concurrent;
using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorNativeSdk.Testing;

/// <summary>
/// An <see cref="IBackgroundWorkScheduler"/> that records scheduled work and runs it only when a test says so, with the
/// same state transitions as the Core in-process scheduler (replace/keep policies, retries with backoff up to
/// <see cref="WorkRequest.MaxAttempts"/>, periodic work re-enqueued after each run, cancelled work removed).
/// </summary>
/// <remarks>
/// <see cref="RunAsync"/> executes the <see cref="IBackgroundWorker"/> registered under the work's name (resolved from a new
/// scope of the service provider passed to the constructor, as the real scheduler does); <see cref="CompleteAsync"/> applies
/// an outcome without running anything. When a service provider is available, scheduling a worker name that is not
/// registered fails with <see cref="NativeErrorCodes.InvalidArgument"/>, like the real scheduler.
/// </remarks>
public sealed class FakeBackgroundWorkScheduler : FakeCapability, IBackgroundWorkScheduler
{
    private readonly ConcurrentDictionary<string, Entry> _work = new(StringComparer.Ordinal);
    private readonly IServiceProvider? _services;
    private readonly TimeProvider _time;

    /// <summary>Creates the fake.</summary>
    /// <param name="services">Resolves keyed <see cref="IBackgroundWorker"/>s for <see cref="RunAsync"/>; optional.</param>
    /// <param name="timeProvider">The clock used for run times and backoff; defaults to the system clock.</param>
    public FakeBackgroundWorkScheduler(IServiceProvider? services = null, TimeProvider? timeProvider = null)
        : base(NativeCapabilities.BackgroundWork, "persisted", "runs-in-background")
    {
        _services = services;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public event EventHandler<WorkInfo>? WorkChanged;

    /// <inheritdoc />
    /// <remarks>Settable, so tests can simulate the guarantees of a specific platform.</remarks>
    public BackgroundExecutionPolicy Policy { get; set; } = new()
    {
        Adapter = "fake",
        RunsInBackground = true,
        RunsAfterTermination = true,
        SurvivesReboot = true,
        PersistsAcrossLaunches = true,
        SupportedConstraints = WorkConstraints.NetworkConnected | WorkConstraints.UnmeteredNetwork | WorkConstraints.Charging |
            WorkConstraints.BatteryNotLow | WorkConstraints.StorageNotLow | WorkConstraints.DeviceIdle,
        Summary = "Fake scheduler: work runs only when a test calls RunAsync or CompleteAsync.",
    };

    /// <summary>Results for <see cref="ScheduleAsync"/>; a queued failure is returned instead of scheduling. Defaults to success.</summary>
    public ResultQueue ScheduleResults { get; } = new();

    /// <summary>The requests of the currently known work, by work id.</summary>
    public IReadOnlyDictionary<string, WorkRequest> Requests => _work.ToDictionary(static e => e.Key, static e => e.Value.Request, StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<NativeResult<WorkInfo>> ScheduleAsync(WorkRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        Record(nameof(ScheduleAsync));
        if (StatusError() is { } error)
        {
            return ValueTask.FromResult<NativeResult<WorkInfo>>(error);
        }

        if (string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.WorkerName))
        {
            return Invalid("Work id and worker name are required.");
        }

        if (_services?.GetService<IServiceProviderIsKeyedService>() is { } inspector && !inspector.IsKeyedService(typeof(IBackgroundWorker), request.WorkerName))
        {
            return Invalid($"No background worker is registered with the name '{request.WorkerName}'.");
        }

        if (request.RepeatInterval is { } interval && interval <= TimeSpan.Zero)
        {
            return Invalid("RepeatInterval must be positive.");
        }

        if (ScheduleResults.Next() is { IsSuccess: false } scripted)
        {
            return ValueTask.FromResult(NativeResult.Failure<WorkInfo>(scripted.Error!));
        }

        if (request.ExistingPolicy == ExistingWorkPolicy.Keep &&
            _work.TryGetValue(request.Id, out var existing) &&
            existing.Info.Status is WorkStatus.Enqueued or WorkStatus.Running)
        {
            return ValueTask.FromResult(NativeResult.Success(existing.Info));
        }

        var entry = new Entry(request, new WorkInfo
        {
            Id = request.Id,
            WorkerName = request.WorkerName,
            Status = WorkStatus.Enqueued,
            EarliestNextRun = _time.GetUtcNow() + request.InitialDelay,
            Constraints = request.Constraints,
            RepeatInterval = request.RepeatInterval,
            IsLongRunning = request.IsLongRunning,
        });
        _work[request.Id] = entry;
        WorkChanged?.Invoke(this, entry.Info);
        return ValueTask.FromResult(NativeResult.Success(entry.Info));
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<bool>> CancelAsync(string workId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workId);
        cancellationToken.ThrowIfCancellationRequested();
        Record(nameof(CancelAsync));
        if (StatusError() is { } error)
        {
            return ValueTask.FromResult<NativeResult<bool>>(error);
        }

        if (!_work.TryRemove(workId, out var entry))
        {
            return ValueTask.FromResult(NativeResult.Success(false));
        }

        WorkChanged?.Invoke(this, entry.Info with { Status = WorkStatus.Cancelled, EarliestNextRun = null });
        return ValueTask.FromResult(NativeResult.Success(true));
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<IReadOnlyList<WorkInfo>>> GetWorkAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Record(nameof(GetWorkAsync));
        if (StatusError() is { } error)
        {
            return ValueTask.FromResult<NativeResult<IReadOnlyList<WorkInfo>>>(error);
        }

        return ValueTask.FromResult(NativeResult.Success<IReadOnlyList<WorkInfo>>([.. _work.Values.Select(static e => e.Info).OrderBy(static i => i.Id, StringComparer.Ordinal)]));
    }

    /// <summary>Gets the current state of a work item, or <see langword="null"/> when it is unknown.</summary>
    public WorkInfo? Find(string workId) => _work.TryGetValue(workId, out var entry) ? entry.Info : null;

    /// <summary>
    /// Runs a work item now: marks it <see cref="WorkStatus.Running"/>, executes the worker registered under its name and
    /// applies the result. A worker that throws counts as <see cref="WorkResult.Retry"/>, as in the real scheduler.
    /// </summary>
    /// <param name="workId">The work id.</param>
    /// <param name="isOsInitiated">The value of <see cref="WorkContext.IsOsInitiated"/>.</param>
    /// <param name="cancellationToken">Passed to the worker.</param>
    /// <returns>The work state after the run.</returns>
    /// <exception cref="InvalidOperationException">The work is unknown, or no service provider or worker is available.</exception>
    public async ValueTask<WorkInfo> RunAsync(string workId, bool isOsInitiated = false, CancellationToken cancellationToken = default)
    {
        var entry = Get(workId);
        if (_services is null)
        {
            throw new InvalidOperationException("Create the fake with an IServiceProvider to run workers, or use CompleteAsync.");
        }

        var running = Start(entry);
        WorkResult result;
        var scope = _services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var worker = scope.ServiceProvider.GetKeyedService<IBackgroundWorker>(entry.Request.WorkerName)
                ?? throw new InvalidOperationException($"No background worker is registered with the name '{entry.Request.WorkerName}'.");
            try
            {
                result = await worker.ExecuteAsync(new WorkContext
                {
                    WorkId = running.Id,
                    WorkerName = running.WorkerName,
                    Input = entry.Request.Input,
                    Attempt = running.Attempts,
                    IsOsInitiated = isOsInitiated,
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                result = WorkResult.Retry(ex.Message);
            }
        }

        return Finish(entry, result);
    }

    /// <summary>Simulates a run of a work item that ended with <paramref name="result"/>, without executing a worker.</summary>
    /// <returns>The work state after the run.</returns>
    /// <exception cref="InvalidOperationException">The work is unknown.</exception>
    public ValueTask<WorkInfo> CompleteAsync(string workId, WorkResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var entry = Get(workId);
        Start(entry);
        return ValueTask.FromResult(Finish(entry, result));
    }

    private static ValueTask<NativeResult<WorkInfo>> Invalid(string message) =>
        ValueTask.FromResult<NativeResult<WorkInfo>>(NativeError.Failed(NativeCapabilities.BackgroundWork, NativeErrorCodes.InvalidArgument, message));

    private Entry Get(string workId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workId);
        return _work.TryGetValue(workId, out var entry) ? entry : throw new InvalidOperationException($"Work '{workId}' is not scheduled.");
    }

    private WorkInfo Start(Entry entry)
    {
        WorkInfo running;
        lock (entry)
        {
            running = entry.Info = entry.Info with
            {
                Status = WorkStatus.Running,
                Attempts = entry.Info.Attempts + 1,
                LastRunAt = _time.GetUtcNow(),
                EarliestNextRun = null,
            };
        }

        WorkChanged?.Invoke(this, running);
        return running;
    }

    private WorkInfo Finish(Entry entry, WorkResult result)
    {
        var now = _time.GetUtcNow();
        WorkInfo info;
        lock (entry)
        {
            var current = entry.Info with { LastMessage = result.Message };
            var request = entry.Request;
            info = result.Kind switch
            {
                WorkResultKind.Success when request.RepeatInterval is { } interval =>
                    current with { Status = WorkStatus.Enqueued, Attempts = 0, EarliestNextRun = now + interval, Output = result.Output },
                WorkResultKind.Success => current with { Status = WorkStatus.Succeeded, Output = result.Output },
                WorkResultKind.Retry when current.Attempts < request.MaxAttempts =>
                    current with { Status = WorkStatus.Enqueued, EarliestNextRun = now + Backoff(request.BackoffDelay, current.Attempts) },
                _ when request.RepeatInterval is { } repeat =>
                    current with { Status = WorkStatus.Enqueued, Attempts = 0, EarliestNextRun = now + repeat },
                _ => current with { Status = WorkStatus.Failed },
            };
            entry.Info = info;
        }

        if (!_work.TryGetValue(info.Id, out var stillScheduled) || !ReferenceEquals(stillScheduled, entry))
        {
            // Cancelled or replaced while running: the outcome is discarded, as in the real scheduler.
            return info;
        }

        WorkChanged?.Invoke(this, info);
        return info;
    }

    private static TimeSpan Backoff(TimeSpan initial, int attempts) =>
        TimeSpan.FromTicks((long)Math.Min(initial.Ticks * Math.Pow(2, Math.Max(0, attempts - 1)), TimeSpan.FromHours(1).Ticks));

    private sealed class Entry(WorkRequest request, WorkInfo info)
    {
        public WorkRequest Request { get; } = request;

        public WorkInfo Info { get; set; } = info;
    }
}

/// <summary>
/// An <see cref="ITransferService"/> that records requests and lets tests drive each transfer through its states
/// (<see cref="SimulateProgress"/>, <see cref="SimulateCompleted"/>, <see cref="SimulateFailed"/>, ...). Nothing is downloaded
/// or uploaded and no file is touched.
/// </summary>
/// <remarks>
/// Enqueue, cancel, retry and wait semantics match the Core HTTP transfer service: enqueuing the id of an active transfer
/// returns it, only failed or cancelled transfers restart on retry, and <see cref="WaitForCompletionAsync"/> maps
/// <see cref="TransferStatus.Cancelled"/> and <see cref="TransferStatus.Failed"/> to failed results.
/// </remarks>
public sealed class FakeTransferService : FakeCapability, ITransferService
{
    private readonly ConcurrentDictionary<string, TransferInfo> _transfers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TransferInfo>> _waiters = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<DownloadRequest> _downloads = new();
    private readonly ConcurrentQueue<UploadRequest> _uploads = new();
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;

    /// <summary>Creates the fake.</summary>
    /// <param name="timeProvider">The clock used for <see cref="TransferInfo.CreatedAt"/> and <see cref="TransferInfo.CompletedAt"/>.</param>
    public FakeTransferService(TimeProvider? timeProvider = null)
        : base(NativeCapabilities.Transfers)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public event EventHandler<TransferInfo>? TransferChanged;

    /// <inheritdoc />
    /// <remarks>Settable, so tests can simulate the guarantees of a specific platform.</remarks>
    public TransferPolicy Policy { get; set; } = new()
    {
        Adapter = "fake",
        ContinuesInBackground = true,
        ContinuesAfterTermination = true,
        ResumesDownloads = true,
        Summary = "Fake transfers: tests drive progress and completion.",
    };

    /// <summary>Results for the enqueue methods; a queued failure is returned instead of enqueuing. Defaults to success.</summary>
    public ResultQueue EnqueueResults { get; } = new();

    /// <summary>The download requests received, in order.</summary>
    public IReadOnlyList<DownloadRequest> Downloads => [.. _downloads];

    /// <summary>The upload requests received, in order.</summary>
    public IReadOnlyList<UploadRequest> Uploads => [.. _uploads];

    /// <inheritdoc />
    public ValueTask<NativeResult<TransferInfo>> EnqueueDownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        Record(nameof(EnqueueDownloadAsync));
        _downloads.Enqueue(request);
        if (Validate(request.Uri, request.DestinationPath, "downloaded") is { } invalid)
        {
            return ValueTask.FromResult<NativeResult<TransferInfo>>(invalid);
        }

        return ValueTask.FromResult(Enqueue(request.Id, TransferDirection.Download, request.Uri, request.DestinationPath));
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<TransferInfo>> EnqueueUploadAsync(UploadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        Record(nameof(EnqueueUploadAsync));
        _uploads.Enqueue(request);
        if (Validate(request.Uri, request.SourcePath, "uploaded to") is { } invalid)
        {
            return ValueTask.FromResult<NativeResult<TransferInfo>>(invalid);
        }

        return ValueTask.FromResult(Enqueue(request.Id, TransferDirection.Upload, request.Uri, request.SourcePath));
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<bool>> CancelAsync(string transferId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transferId);
        cancellationToken.ThrowIfCancellationRequested();
        Record(nameof(CancelAsync));
        if (StatusError() is { } error)
        {
            return ValueTask.FromResult<NativeResult<bool>>(error);
        }

        var changed = TryUpdate(transferId, t => t with { Status = TransferStatus.Cancelled, CompletedAt = _time.GetUtcNow() });
        return ValueTask.FromResult(NativeResult.Success(changed is not null));
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<TransferInfo>> RetryAsync(string transferId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transferId);
        cancellationToken.ThrowIfCancellationRequested();
        Record(nameof(RetryAsync));
        if (StatusError() is { } error)
        {
            return ValueTask.FromResult<NativeResult<TransferInfo>>(error);
        }

        if (!_transfers.TryGetValue(transferId, out var current))
        {
            return ValueTask.FromResult<NativeResult<TransferInfo>>(NotFound(transferId));
        }

        if (current.Status is not (TransferStatus.Failed or TransferStatus.Cancelled))
        {
            return ValueTask.FromResult(NativeResult.Success(current));
        }

        var restarted = TryUpdate(transferId, t => t with { Status = TransferStatus.Queued, Retries = 0, ErrorMessage = null, CompletedAt = null });
        return ValueTask.FromResult(NativeResult.Success(restarted ?? current));
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<TransferInfo>> GetTransfersAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Record(nameof(GetTransfersAsync));
        return ValueTask.FromResult<IReadOnlyList<TransferInfo>>([.. _transfers.Values.OrderBy(static t => t.CreatedAt).ThenBy(static t => t.Id, StringComparer.Ordinal)]);
    }

    /// <inheritdoc />
    public async ValueTask<NativeResult<TransferInfo>> WaitForCompletionAsync(string transferId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transferId);
        Record(nameof(WaitForCompletionAsync));
        TaskCompletionSource<TransferInfo> waiter;
        lock (_gate)
        {
            if (!_transfers.TryGetValue(transferId, out var current))
            {
                return NotFound(transferId);
            }

            if (current.IsFinished)
            {
                return ToResult(current);
            }

            waiter = _waiters.GetOrAdd(transferId, static _ => new TaskCompletionSource<TransferInfo>(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        return ToResult(await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Gets a transfer, or <see langword="null"/> when it is unknown.</summary>
    public TransferInfo? Find(string transferId) => _transfers.GetValueOrDefault(transferId);

    /// <summary>Reports progress: the transfer becomes <see cref="TransferStatus.Running"/>.</summary>
    public TransferInfo SimulateProgress(string transferId, long bytesTransferred, long? totalBytes = null) =>
        Update(transferId, t => t with { Status = TransferStatus.Running, BytesTransferred = bytesTransferred, TotalBytes = totalBytes ?? t.TotalBytes });

    /// <summary>Simulates connectivity loss: the transfer becomes <see cref="TransferStatus.WaitingForNetwork"/>.</summary>
    public TransferInfo SimulateWaitingForNetwork(string transferId) => Update(transferId, static t => t with { Status = TransferStatus.WaitingForNetwork });

    /// <summary>Completes a transfer successfully; waiters receive it.</summary>
    public TransferInfo SimulateCompleted(string transferId, int httpStatusCode = 200) => Update(transferId, t => t with
    {
        Status = TransferStatus.Completed,
        BytesTransferred = t.TotalBytes ?? t.BytesTransferred,
        HttpStatusCode = httpStatusCode,
        CompletedAt = _time.GetUtcNow(),
    });

    /// <summary>Fails a transfer permanently; waiters receive a failed result with <paramref name="message"/>.</summary>
    public TransferInfo SimulateFailed(string transferId, string message = "The transfer failed.", int? httpStatusCode = null) => Update(transferId, t => t with
    {
        Status = TransferStatus.Failed,
        ErrorMessage = message,
        HttpStatusCode = httpStatusCode ?? t.HttpStatusCode,
        CompletedAt = _time.GetUtcNow(),
    });

    private NativeResult<TransferInfo> Enqueue(string? id, TransferDirection direction, Uri uri, string localPath)
    {
        if (StatusError() is { } error)
        {
            return error;
        }

        if (EnqueueResults.Next() is { IsSuccess: false } scripted)
        {
            return NativeResult.Failure<TransferInfo>(scripted.Error!);
        }

        TransferInfo info;
        lock (_gate)
        {
            var transferId = id ?? Guid.NewGuid().ToString("N");
            if (_transfers.TryGetValue(transferId, out var existing) && !existing.IsFinished)
            {
                return existing;
            }

            info = new TransferInfo
            {
                Id = transferId,
                Direction = direction,
                Uri = uri,
                LocalPath = localPath,
                Status = TransferStatus.Queued,
                CreatedAt = _time.GetUtcNow(),
            };
            _transfers[transferId] = info;
            _waiters.TryRemove(transferId, out _);
        }

        TransferChanged?.Invoke(this, info);
        return info;
    }

    private TransferInfo Update(string transferId, Func<TransferInfo, TransferInfo> update)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transferId);
        return TryUpdate(transferId, update) ?? throw new InvalidOperationException($"Transfer '{transferId}' does not exist.");
    }

    private TransferInfo? TryUpdate(string transferId, Func<TransferInfo, TransferInfo> update)
    {
        TransferInfo updated;
        TaskCompletionSource<TransferInfo>? waiter = null;
        lock (_gate)
        {
            if (!_transfers.TryGetValue(transferId, out var current))
            {
                return null;
            }

            updated = update(current);
            _transfers[transferId] = updated;
            if (updated.IsFinished)
            {
                _waiters.TryRemove(transferId, out waiter);
            }
        }

        TransferChanged?.Invoke(this, updated);
        waiter?.TrySetResult(updated);
        return updated;
    }

    private NativeError? Validate(Uri uri, string localPath, string verb)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https"))
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, $"Only absolute http and https URLs can be {verb}.");
        }

        return string.IsNullOrWhiteSpace(localPath)
            ? NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "A file path is required.")
            : null;
    }

    private NativeResult<TransferInfo> ToResult(TransferInfo info) => info.Status switch
    {
        TransferStatus.Completed => info,
        TransferStatus.Cancelled => NativeError.Cancelled(Capability, "The transfer was cancelled."),
        _ => NativeError.Failed(Capability, NativeErrorCodes.PlatformError, info.ErrorMessage ?? "The transfer failed."),
    };

    private NativeError NotFound(string transferId) =>
        NativeError.Failed(Capability, NativeErrorCodes.NotFound, $"Transfer '{transferId}' does not exist.");
}
