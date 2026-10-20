using System.Collections.Concurrent;
using System.Text.Json;
using BlazorNativeSdk.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk.Lifecycle;

/// <summary>Options for <see cref="InProcessBackgroundWorkScheduler"/>.</summary>
public sealed class InProcessWorkOptions
{
    /// <summary>The adapter name reported in the policy and status.</summary>
    public string AdapterName { get; set; } = "in-process";

    /// <summary>
    /// Run work while the app is in the background. Desktop hosts keep running and should leave this enabled; mobile and
    /// browser hosts disable it because the OS may suspend the process mid-run.
    /// </summary>
    public bool RunWhileInBackground { get; set; } = true;

    /// <summary>The longest the scheduler sleeps before re-evaluating work. Default: 1 minute.</summary>
    public TimeSpan MaximumIdleDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Cancel a worker run after this duration. Default: no limit.</summary>
    public TimeSpan? MaximumExecutionTime { get; set; }

    /// <summary>The longest backoff between retries. Default: 1 hour.</summary>
    public TimeSpan MaximumBackoff { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How long finished one-time work stays visible in <see cref="IBackgroundWorkScheduler.GetWorkAsync"/>. Default: 7 days.</summary>
    public TimeSpan FinishedWorkRetention { get; set; } = TimeSpan.FromDays(7);
}

/// <summary>
/// A persisted work scheduler that runs <see cref="IBackgroundWorker"/>s inside the app process. Work survives app
/// restarts (it is stored in <see cref="INativeStateStore"/>) and resumes on the next launch, but it only runs while the
/// process is alive. OS adapters (iOS <c>BGTaskScheduler</c>) call <see cref="RunDueWorkAsync"/> when the OS wakes the app.
/// </summary>
public sealed partial class InProcessBackgroundWorkScheduler : IBackgroundWorkScheduler, INativeSdkStartupTask, IAsyncDisposable, IDisposable
{
    private const string Prefix = "bns.work.";
    private readonly IServiceProvider _services;
    private readonly INativeStateStore _store;
    private readonly IConnectivityService _connectivity;
    private readonly IAppLifecycle _lifecycle;
    private readonly InProcessWorkOptions _options;
    private readonly ILogger<InProcessBackgroundWorkScheduler> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private Task? _loop;
    private int _disposed;

    /// <summary>Creates the scheduler.</summary>
    public InProcessBackgroundWorkScheduler(
        IServiceProvider services,
        INativeStateStore store,
        IConnectivityService connectivity,
        IAppLifecycle lifecycle,
        IOptions<InProcessWorkOptions> options,
        ILogger<InProcessBackgroundWorkScheduler> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _services = services;
        _store = store;
        _connectivity = connectivity;
        _lifecycle = lifecycle;
        _options = options.Value;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        Policy = new BackgroundExecutionPolicy
        {
            Adapter = _options.AdapterName,
            RunsInBackground = _options.RunWhileInBackground,
            RunsAfterTermination = false,
            SurvivesReboot = true,
            PersistsAcrossLaunches = true,
            ExactTiming = false,
            MaximumExecutionTime = _options.MaximumExecutionTime,
            SupportedConstraints = WorkConstraints.NetworkConnected | WorkConstraints.UnmeteredNetwork,
            Summary = _options.RunWhileInBackground
                ? "Work runs inside the app process while it is running (including in the background) and resumes after restarts. Nothing runs while the app is closed."
                : "Work runs inside the app process while it is in the foreground and resumes after restarts. Nothing runs while the app is in the background or closed.",
        };
    }

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.BackgroundWork;

    /// <inheritdoc />
    public BackgroundExecutionPolicy Policy { get; }

    /// <inheritdoc />
    public event EventHandler<WorkInfo>? WorkChanged;

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var features = new List<string> { "persisted" };
        if (_options.RunWhileInBackground)
        {
            features.Add("runs-in-background");
        }

        return ValueTask.FromResult(_store is Storage.InMemoryStateStore
            ? CapabilityStatus.Available(Capability, _options.AdapterName, features) with { Reason = "No durable state store is registered; scheduled work is lost when the app exits." }
            : CapabilityStatus.Available(Capability, _options.AdapterName, features));
    }

    /// <inheritdoc />
    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (_loop is not null)
        {
            return;
        }

        // Recover from a crash or kill during a run: anything marked Running is re-queued.
        foreach (var record in await LoadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (record.Status == WorkStatus.Running)
            {
                record.Status = WorkStatus.Enqueued;
                record.NextRunAt = _time.GetUtcNow();
                await SaveAsync(record, cancellationToken).ConfigureAwait(false);
            }
        }

        _connectivity.Changed += OnConditionsChanged;
        _lifecycle.StateChanged += OnConditionsChanged;
        _loop = Task.Run(LoopAsync, CancellationToken.None);
    }

    /// <inheritdoc />
    public async ValueTask<NativeResult<WorkInfo>> ScheduleAsync(WorkRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.WorkerName))
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "Work id and worker name are required.");
        }

        if (!IsWorkerRegistered(request.WorkerName))
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument,
                $"No background worker is registered with the name '{request.WorkerName}'. Call services.AddBackgroundWorker<TWorker>(\"{request.WorkerName}\").");
        }

        if (request.RepeatInterval is { } interval && interval <= TimeSpan.Zero)
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "RepeatInterval must be positive.");
        }

        var existing = await LoadAsync(request.Id, cancellationToken).ConfigureAwait(false);
        if (existing is not null && request.ExistingPolicy == ExistingWorkPolicy.Keep && existing.Status is WorkStatus.Enqueued or WorkStatus.Running)
        {
            return existing.ToInfo();
        }

        if (existing is not null && _running.TryGetValue(request.Id, out var runningCts))
        {
            await runningCts.CancelAsync().ConfigureAwait(false);
        }

        var record = WorkRecord.FromRequest(request, _time.GetUtcNow());
        await SaveAsync(record, cancellationToken).ConfigureAwait(false);
        Raise(record);
        Wake();
        return record.ToInfo();
    }

    /// <inheritdoc />
    public async ValueTask<NativeResult<bool>> CancelAsync(string workId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workId);
        var record = await LoadAsync(workId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return false;
        }

        if (_running.TryGetValue(workId, out var cts))
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        await _store.RemoveAsync(Prefix + workId, cancellationToken).ConfigureAwait(false);
        record.Status = WorkStatus.Cancelled;
        Raise(record);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<NativeResult<IReadOnlyList<WorkInfo>>> GetWorkAsync(CancellationToken cancellationToken = default)
    {
        var records = await LoadAllAsync(cancellationToken).ConfigureAwait(false);
        return NativeResult.Success<IReadOnlyList<WorkInfo>>([.. records.Select(r => r.ToInfo())]);
    }

    /// <summary>
    /// Runs all work that is due and whose constraints are satisfied. Called by the scheduler loop and by OS adapters when
    /// the system grants background time.
    /// </summary>
    /// <param name="isOsInitiated">Whether the OS woke the app to run work.</param>
    /// <param name="cancellationToken">Cancelled when execution time is revoked.</param>
    /// <returns>The number of work items executed.</returns>
    public async Task<int> RunDueWorkAsync(bool isOsInitiated, CancellationToken cancellationToken = default)
    {
        if (!isOsInitiated && !_options.RunWhileInBackground && _lifecycle.State == AppLifecycleState.Background)
        {
            return 0;
        }

        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var executed = 0;
            var now = _time.GetUtcNow();
            foreach (var record in (await LoadAllAsync(cancellationToken).ConfigureAwait(false)).OrderBy(r => r.NextRunAt))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (record.Status == WorkStatus.Enqueued && record.NextRunAt <= now && ConstraintsSatisfied(record.Constraints))
                {
                    await ExecuteAsync(record, isOsInitiated, cancellationToken).ConfigureAwait(false);
                    executed++;
                }
                else if (record.Status is WorkStatus.Succeeded or WorkStatus.Failed &&
                         record.LastRunAt is { } last && now - last > _options.FinishedWorkRetention)
                {
                    await _store.RemoveAsync(Prefix + record.Id, cancellationToken).ConfigureAwait(false);
                }
            }

            return executed;
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _connectivity.Changed -= OnConditionsChanged;
            _lifecycle.StateChanged -= OnConditionsChanged;
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

        _connectivity.Changed -= OnConditionsChanged;
        _lifecycle.StateChanged -= OnConditionsChanged;
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

    private bool IsWorkerRegistered(string name)
    {
        if (_services.GetService<IServiceProviderIsKeyedService>() is { } inspector)
        {
            return inspector.IsKeyedService(typeof(IBackgroundWorker), name);
        }

        return true;
    }

    private bool ConstraintsSatisfied(WorkConstraints constraints)
    {
        if ((constraints & (WorkConstraints.NetworkConnected | WorkConstraints.UnmeteredNetwork)) == 0)
        {
            return true;
        }

        var snapshot = _connectivity.Current;
        if (snapshot.Access == NetworkAccess.Unknown)
        {
            // Without a connectivity adapter we cannot evaluate network constraints; let the worker decide.
            return true;
        }

        if (!snapshot.IsOnline)
        {
            return false;
        }

        if ((constraints & WorkConstraints.UnmeteredNetwork) != 0)
        {
            return snapshot.IsMetered switch
            {
                false => true,
                true => false,
                null => snapshot.ConnectionTypes.Any(t => t is ConnectionType.WiFi or ConnectionType.Ethernet),
            };
        }

        return true;
    }

    private async Task ExecuteAsync(WorkRecord record, bool isOsInitiated, CancellationToken cancellationToken)
    {
        record.Status = WorkStatus.Running;
        record.Attempts++;
        record.LastRunAt = _time.GetUtcNow();
        await SaveAsync(record, cancellationToken).ConfigureAwait(false);
        Raise(record);

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        if (_options.MaximumExecutionTime is { } limit)
        {
            runCts.CancelAfter(limit);
        }

        _running[record.Id] = runCts;
        WorkResult result;
        try
        {
            await using var scope = _services.CreateAsyncScope();
            var worker = scope.ServiceProvider.GetKeyedService<IBackgroundWorker>(record.WorkerName);
            if (worker is null)
            {
                result = WorkResult.Failure($"No background worker is registered with the name '{record.WorkerName}'.");
            }
            else
            {
                var context = new WorkContext
                {
                    WorkId = record.Id,
                    WorkerName = record.WorkerName,
                    Input = record.Input,
                    Attempt = record.Attempts,
                    IsOsInitiated = isOsInitiated,
                };
                result = await worker.ExecuteAsync(context, runCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (runCts.IsCancellationRequested)
        {
            result = WorkResult.Retry("The run was cancelled before completion.");
        }
        catch (Exception ex)
        {
            LogWorkerFailed(_logger, record.Id, ex);
            result = WorkResult.Retry(ex.Message);
        }
        finally
        {
            _running.TryRemove(record.Id, out _);
        }

        // The work may have been cancelled or replaced while it ran.
        var current = await LoadAsync(record.Id, CancellationToken.None).ConfigureAwait(false);
        if (current is null || current.CreatedAt != record.CreatedAt)
        {
            return;
        }

        ApplyResult(record, result);
        await SaveAsync(record, CancellationToken.None).ConfigureAwait(false);
        LogWorkCompleted(_logger, record.Id, result.Kind, record.Status);
        Raise(record);
    }

    private void ApplyResult(WorkRecord record, WorkResult result)
    {
        var now = _time.GetUtcNow();
        record.LastMessage = result.Message;
        switch (result.Kind)
        {
            case WorkResultKind.Success:
                record.Output = new Dictionary<string, string>(result.Output);
                if (record.RepeatInterval is { } interval)
                {
                    record.Status = WorkStatus.Enqueued;
                    record.Attempts = 0;
                    record.NextRunAt = now + interval;
                }
                else
                {
                    record.Status = WorkStatus.Succeeded;
                }

                break;

            case WorkResultKind.Retry when record.Attempts < record.MaxAttempts:
                record.Status = WorkStatus.Enqueued;
                var factor = Math.Pow(2, Math.Max(0, record.Attempts - 1));
                var delay = TimeSpan.FromTicks((long)Math.Min(record.BackoffDelay.Ticks * factor, _options.MaximumBackoff.Ticks));
                record.NextRunAt = now + delay;
                break;

            default:
                if (record.RepeatInterval is { } repeat)
                {
                    record.Status = WorkStatus.Enqueued;
                    record.Attempts = 0;
                    record.NextRunAt = now + repeat;
                }
                else
                {
                    record.Status = WorkStatus.Failed;
                }

                break;
        }
    }

    private async Task LoopAsync()
    {
        var token = _shutdown.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await RunDueWorkAsync(isOsInitiated: false, token).ConfigureAwait(false);
                var delay = await GetNextDelayAsync(token).ConfigureAwait(false);
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
                try
                {
                    await Task.Delay(_options.MaximumIdleDelay, _time, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task<TimeSpan> GetNextDelayAsync(CancellationToken token)
    {
        var now = _time.GetUtcNow();
        var next = (await LoadAllAsync(token).ConfigureAwait(false))
            .Where(r => r.Status == WorkStatus.Enqueued)
            .Select(r => (DateTimeOffset?)r.NextRunAt)
            .Min();
        if (next is null)
        {
            return _options.MaximumIdleDelay;
        }

        var delay = next.Value - now;
        if (delay < TimeSpan.FromMilliseconds(50))
        {
            delay = TimeSpan.FromMilliseconds(50);
        }

        return delay > _options.MaximumIdleDelay ? _options.MaximumIdleDelay : delay;
    }

    private void OnConditionsChanged(object? sender, EventArgs e) => Wake();

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

    private void Raise(WorkRecord record)
    {
        try
        {
            WorkChanged?.Invoke(this, record.ToInfo());
        }
        catch (Exception ex)
        {
            LogWorkerFailed(_logger, record.Id, ex);
        }
    }

    private async ValueTask<WorkRecord?> LoadAsync(string id, CancellationToken cancellationToken)
    {
        var json = await _store.GetAsync(Prefix + id, cancellationToken).ConfigureAwait(false);
        return Deserialize(json);
    }

    private async ValueTask<List<WorkRecord>> LoadAllAsync(CancellationToken cancellationToken)
    {
        var result = new List<WorkRecord>();
        foreach (var key in await _store.GetKeysAsync(Prefix, cancellationToken).ConfigureAwait(false))
        {
            if (Deserialize(await _store.GetAsync(key, cancellationToken).ConfigureAwait(false)) is { } record)
            {
                result.Add(record);
            }
        }

        return result;
    }

    private ValueTask SaveAsync(WorkRecord record, CancellationToken cancellationToken) =>
        _store.SetAsync(Prefix + record.Id, JsonSerializer.Serialize(record, CoreJsonContext.Default.WorkRecord), cancellationToken);

    private static WorkRecord? Deserialize(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, CoreJsonContext.Default.WorkRecord);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(EventId = 1401, Level = LogLevel.Error, Message = "Background work {WorkId} threw an exception.")]
    private static partial void LogWorkerFailed(ILogger logger, string workId, Exception exception);

    [LoggerMessage(EventId = 1402, Level = LogLevel.Debug, Message = "Background work {WorkId} finished with {Result}; status {Status}.")]
    private static partial void LogWorkCompleted(ILogger logger, string workId, WorkResultKind result, WorkStatus status);

    [LoggerMessage(EventId = 1403, Level = LogLevel.Error, Message = "The background work loop failed; retrying.")]
    private static partial void LogLoopFailed(ILogger logger, Exception exception);
}

/// <summary>Persisted work state.</summary>
internal sealed class WorkRecord
{
    public required string Id { get; set; }

    public required string WorkerName { get; set; }

    public Dictionary<string, string> Input { get; set; } = [];

    public WorkConstraints Constraints { get; set; }

    public TimeSpan? RepeatInterval { get; set; }

    public TimeSpan BackoffDelay { get; set; }

    public int MaxAttempts { get; set; }

    public bool IsLongRunning { get; set; }

    public WorkStatus Status { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset NextRunAt { get; set; }

    public DateTimeOffset? LastRunAt { get; set; }

    public string? LastMessage { get; set; }

    public Dictionary<string, string> Output { get; set; } = [];

    public static WorkRecord FromRequest(WorkRequest request, DateTimeOffset now) => new()
    {
        Id = request.Id,
        WorkerName = request.WorkerName,
        Input = new Dictionary<string, string>(request.Input),
        Constraints = request.Constraints,
        RepeatInterval = request.RepeatInterval,
        BackoffDelay = request.BackoffDelay <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : request.BackoffDelay,
        MaxAttempts = Math.Max(1, request.MaxAttempts),
        IsLongRunning = request.IsLongRunning,
        Status = WorkStatus.Enqueued,
        CreatedAt = now,
        NextRunAt = now + (request.InitialDelay < TimeSpan.Zero ? TimeSpan.Zero : request.InitialDelay),
    };

    public WorkInfo ToInfo() => new()
    {
        Id = Id,
        WorkerName = WorkerName,
        Status = Status,
        Attempts = Attempts,
        EarliestNextRun = Status == WorkStatus.Enqueued ? NextRunAt : null,
        LastRunAt = LastRunAt,
        LastMessage = LastMessage,
        Output = Output,
        Constraints = Constraints,
        RepeatInterval = RepeatInterval,
        IsLongRunning = IsLongRunning,
    };
}
