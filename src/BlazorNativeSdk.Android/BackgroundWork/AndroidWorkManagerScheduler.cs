using AndroidX.Work;
using BlazorNativeSdk.Lifecycle;
using Google.Common.Util.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AndroidXExistingPeriodicWorkPolicy = AndroidX.Work.ExistingPeriodicWorkPolicy;
using AndroidXExistingWorkPolicy = AndroidX.Work.ExistingWorkPolicy;
using AndroidXWorkInfo = AndroidX.Work.WorkInfo;
using ExistingWorkPolicy = BlazorNativeSdk.Lifecycle.ExistingWorkPolicy;
using TimeUnit = Java.Util.Concurrent.TimeUnit;
using WorkInfo = BlazorNativeSdk.Lifecycle.WorkInfo;
using WorkRequest = BlazorNativeSdk.Lifecycle.WorkRequest;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>
/// <see cref="IBackgroundWorkScheduler"/> backed by AndroidX WorkManager: work runs in <see cref="NativeSdkWorker"/>, which
/// resolves the registered <see cref="IBackgroundWorker"/> from a new DI scope.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Work survives app restarts, process death and reboots, and runs while the app is closed. It does not run after
/// the user force-stops the app until the app is opened again (an Android rule).</item>
/// <item>Timing is never exact: Doze, App Standby buckets and battery saver defer work. Periodic work runs at most every
/// 15 minutes (shorter intervals are clamped) and each run is stopped after about 10 minutes.</item>
/// <item>Each id is a unique work name (<c>bns:{id}</c>). <see cref="ExistingWorkPolicy.Replace"/> maps to <c>REPLACE</c> for
/// one-time work and <c>UPDATE</c> for periodic work (falling back to <c>CANCEL_AND_REENQUEUE</c> when the work type
/// changes); <see cref="ExistingWorkPolicy.Keep"/> maps to <c>KEEP</c>.</item>
/// <item>Input and output are stored in WorkManager <c>Data</c>, limited to 10 KB in total.</item>
/// <item>A device-idle constraint cannot be combined with backoff in WorkManager, so such work retries on WorkManager's default schedule.</item>
/// <item><see cref="WorkChanged"/> is raised for operations in this process, including runs that happen while the app's
/// process is alive; runs in a process the OS started for the work are visible through <see cref="GetWorkAsync"/>.</item>
/// </list>
/// </remarks>
public sealed partial class AndroidWorkManagerScheduler : IBackgroundWorkScheduler
{
    private const string AdapterName = "android-workmanager";
    private readonly IServiceProvider _services;
    private readonly ILogger<AndroidWorkManagerScheduler> _logger;
    private readonly TimeProvider _time;

    /// <summary>Creates the scheduler.</summary>
    public AndroidWorkManagerScheduler(IServiceProvider services, ILogger<AndroidWorkManagerScheduler> logger, TimeProvider? timeProvider = null)
    {
        _services = services;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        Policy = new BackgroundExecutionPolicy
        {
            Adapter = AdapterName,
            RunsInBackground = true,
            RunsAfterTermination = true,
            SurvivesReboot = true,
            PersistsAcrossLaunches = true,
            ExactTiming = false,
            MinimumPeriodicInterval = AndroidWorkMapper.MinimumPeriodicInterval,
            MaximumExecutionTime = AndroidWorkMapper.MaximumExecutionTime,
            SupportedConstraints = WorkConstraints.NetworkConnected | WorkConstraints.UnmeteredNetwork | WorkConstraints.Charging |
                                   WorkConstraints.BatteryNotLow | WorkConstraints.StorageNotLow | WorkConstraints.DeviceIdle,
            Summary = "WorkManager runs work in the background, after the app is closed and after reboots, when its constraints are met. " +
                      "The OS decides the exact time (Doze, App Standby and battery saver defer work); periodic work runs at most every " +
                      "15 minutes and each run is limited to about 10 minutes. Nothing runs after the user force-stops the app until it is opened again.",
        };
    }

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.BackgroundWork;

    /// <inheritdoc />
    public BackgroundExecutionPolicy Policy { get; }

    /// <inheritdoc />
    public event EventHandler<WorkInfo>? WorkChanged;

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Available(Capability, AdapterName,
            "persisted", "runs-in-background", "runs-after-termination", "survives-reboot"));

    /// <inheritdoc />
    public ValueTask<NativeResult<WorkInfo>> ScheduleAsync(WorkRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return NativeOperation.RunAsync<WorkInfo>(Capability, async ct =>
        {
            if (Validate(request) is { } invalid)
            {
                return invalid;
            }

            var now = _time.GetUtcNow();
            Data data;
            try
            {
                data = ToData(AndroidWorkMapper.EncodeInput(request));
            }
            catch (Java.Lang.IllegalStateException ex)
            {
                return NativeError.Failed(Capability, NativeErrorCodes.TooLarge,
                    "The work input exceeds WorkManager's 10 KB limit; store large payloads elsewhere and pass a key.", ex);
            }

            using (data)
            {
                using var constraints = BuildConstraints(request.Constraints);
                var tags = AndroidWorkMapper.CreateTags(request.Id, request.WorkerName, now);
                var delay = AndroidWorkMapper.ClampDelay(request.InitialDelay);
                var backoff = (request.Constraints & WorkConstraints.DeviceIdle) == 0 ? AndroidWorkMapper.ClampBackoff(request.BackoffDelay) : (TimeSpan?)null;
                var workManager = WorkManager.GetInstance(AndroidContext.App);
                var uniqueName = AndroidWorkMapper.UniqueName(request.Id);

                if (request.RepeatInterval is { } repeat)
                {
                    using var periodic = BuildPeriodic(AndroidWorkMapper.ClampRepeatInterval(repeat), data, constraints, tags, delay, backoff);
                    if (request.ExistingPolicy == ExistingWorkPolicy.Keep)
                    {
                        await AwaitAsync(workManager.EnqueueUniquePeriodicWork(uniqueName, AndroidXExistingPeriodicWorkPolicy.Keep!, periodic), ct).ConfigureAwait(false);
                    }
                    else
                    {
                        try
                        {
                            await AwaitAsync(workManager.EnqueueUniquePeriodicWork(uniqueName, AndroidXExistingPeriodicWorkPolicy.Update!, periodic), ct).ConfigureAwait(false);
                        }
                        catch (Java.Lang.Exception ex)
                        {
                            // UPDATE cannot change a one-time work into periodic work.
                            LogUpdateFallback(_logger, request.Id, ex);
                            await AwaitAsync(workManager.EnqueueUniquePeriodicWork(uniqueName, AndroidXExistingPeriodicWorkPolicy.CancelAndReenqueue!, periodic), ct)
                                .ConfigureAwait(false);
                        }
                    }
                }
                else
                {
                    using var oneTime = BuildOneTime(data, constraints, tags, delay, backoff);
                    var policy = request.ExistingPolicy == ExistingWorkPolicy.Keep ? AndroidXExistingWorkPolicy.Keep! : AndroidXExistingWorkPolicy.Replace!;
                    await AwaitAsync(workManager.EnqueueUniqueWork(uniqueName, policy, oneTime), ct).ConfigureAwait(false);
                }

                var info = (await GetInfosAsync(workManager.GetWorkInfosForUniqueWork(uniqueName), ct).ConfigureAwait(false))
                    .FirstOrDefault(i => i.Id == request.Id)
                    ?? new WorkInfo
                    {
                        Id = request.Id,
                        WorkerName = request.WorkerName,
                        Status = WorkStatus.Enqueued,
                        EarliestNextRun = now + delay,
                    };
                LogScheduled(_logger, request.Id, request.WorkerName, info.Status);
                Raise(info);
                return info;
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<bool>> CancelAsync(string workId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workId);
        return NativeOperation.RunAsync<bool>(Capability, async ct =>
        {
            var workManager = WorkManager.GetInstance(AndroidContext.App);
            var uniqueName = AndroidWorkMapper.UniqueName(workId);
            var existing = await GetInfosAsync(workManager.GetWorkInfosForUniqueWork(uniqueName), ct).ConfigureAwait(false);
            if (existing.Count == 0)
            {
                return false;
            }

            await AwaitAsync(workManager.CancelUniqueWork(uniqueName), ct).ConfigureAwait(false);
            var current = existing[0];
            Raise(current with { Status = WorkStatus.Cancelled, EarliestNextRun = null });
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>WorkManager keeps finished work for at least 7 days (one-time work) before pruning it.</remarks>
    public ValueTask<NativeResult<IReadOnlyList<WorkInfo>>> GetWorkAsync(CancellationToken cancellationToken = default) =>
        NativeOperation.RunAsync<IReadOnlyList<WorkInfo>>(Capability, async ct =>
        {
            var workManager = WorkManager.GetInstance(AndroidContext.App);
            return NativeResult.Success<IReadOnlyList<WorkInfo>>(await GetInfosAsync(workManager.GetWorkInfosByTag(AndroidWorkMapper.Tag), ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>Raises <see cref="WorkChanged"/> for a run executed by <see cref="NativeSdkWorker"/> in this process.</summary>
    internal void ReportRun(WorkInfo info) => Raise(info);

    internal static Data ToData(IReadOnlyDictionary<string, string> values)
    {
        using var builder = new Data.Builder();
        foreach (var (key, value) in values)
        {
            builder.PutString(key, value);
        }

        return builder.Build();
    }

    internal static IEnumerable<KeyValuePair<string, string?>> FromData(Data? data)
    {
        if (data?.KeyValueMap is not { } map)
        {
            return [];
        }

        return [.. map.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value?.ToString()))];
    }

    private NativeError? Validate(WorkRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.WorkerName))
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "Work id and worker name are required.");
        }

        if (request.RepeatInterval is { } interval && interval <= TimeSpan.Zero)
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "RepeatInterval must be positive.");
        }

        if (_services.GetService<IServiceProviderIsKeyedService>() is { } keyed && !keyed.IsKeyedService(typeof(IBackgroundWorker), request.WorkerName))
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument,
                $"No background worker is registered with the name '{request.WorkerName}'. Call services.AddBackgroundWorker<TWorker>(\"{request.WorkerName}\").");
        }

        return null;
    }

    private static Constraints BuildConstraints(WorkConstraints constraints)
    {
        using var builder = new Constraints.Builder();
        var network = (constraints & WorkConstraints.UnmeteredNetwork) != 0 ? NetworkType.Unmetered
            : (constraints & WorkConstraints.NetworkConnected) != 0 ? NetworkType.Connected
            : NetworkType.NotRequired;
        builder.SetRequiredNetworkType(network!);
        builder.SetRequiresCharging((constraints & WorkConstraints.Charging) != 0);
        builder.SetRequiresBatteryNotLow((constraints & WorkConstraints.BatteryNotLow) != 0);
        builder.SetRequiresStorageNotLow((constraints & WorkConstraints.StorageNotLow) != 0);
        builder.SetRequiresDeviceIdle((constraints & WorkConstraints.DeviceIdle) != 0);
        return builder.Build();
    }

    private static OneTimeWorkRequest BuildOneTime(Data data, Constraints constraints, string[] tags, TimeSpan delay, TimeSpan? backoff)
    {
        using var builder = new OneTimeWorkRequest.Builder(typeof(NativeSdkWorker));
        builder.SetInputData(data);
        builder.SetConstraints(constraints);
        foreach (var tag in tags)
        {
            builder.AddTag(tag);
        }

        if (delay > TimeSpan.Zero)
        {
            builder.SetInitialDelay(delay);
        }

        if (backoff is { } value)
        {
            builder.SetBackoffCriteria(BackoffPolicy.Exponential!, value);
        }

        builder.KeepResultsForAtLeast(TimeSpan.FromDays(7));
        return builder.Build();
    }

    private static PeriodicWorkRequest BuildPeriodic(TimeSpan repeat, Data data, Constraints constraints, string[] tags, TimeSpan delay, TimeSpan? backoff)
    {
        using var builder = new PeriodicWorkRequest.Builder(typeof(NativeSdkWorker), repeat);
        builder.SetInputData(data);
        builder.SetConstraints(constraints);
        foreach (var tag in tags)
        {
            builder.AddTag(tag);
        }

        if (delay > TimeSpan.Zero)
        {
            builder.SetInitialDelay((long)delay.TotalMilliseconds, TimeUnit.Milliseconds!);
        }

        if (backoff is { } value)
        {
            builder.SetBackoffCriteria(BackoffPolicy.Exponential!, value);
        }

        return builder.Build();
    }

    private static async Task AwaitAsync(IOperation operation, CancellationToken cancellationToken)
    {
        using (operation)
        {
            var future = operation.Result ?? throw new InvalidOperationException("WorkManager returned no operation result.");
            await Task.Run(() => GetUnwrapped(future), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<List<WorkInfo>> GetInfosAsync(IListenableFuture future, CancellationToken cancellationToken)
    {
        using (future)
        {
            var result = await Task.Run(() => GetUnwrapped(future), cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                return [];
            }

            using (result)
            {
                var list = Android.Runtime.JavaList<AndroidXWorkInfo>.FromJniHandle(result.Handle, Android.Runtime.JniHandleOwnership.DoNotTransfer);
                var mapped = new List<(WorkInfo Info, long EnqueuedAt)>();
                if (list is not null)
                {
                    foreach (var item in list)
                    {
                        using (item)
                        {
                            if (Map(item) is { } entry)
                            {
                                mapped.Add(entry);
                            }
                        }
                    }
                }

                return [.. mapped
                    .GroupBy(m => m.Info.Id, StringComparer.Ordinal)
                    .Select(g => AndroidWorkMapper.SelectCurrent(g.Select(x => new Box(x.Info, x.EnqueuedAt)), b => b.Info.Status, b => b.EnqueuedAt)!.Info)];
            }
        }
    }

    private static Java.Lang.Object? GetUnwrapped(Java.Util.Concurrent.IFuture future)
    {
        try
        {
            return future.Get();
        }
        catch (Java.Util.Concurrent.ExecutionException ex) when (ex.Cause is { } cause)
        {
            throw cause;
        }
    }

    private static (WorkInfo Info, long EnqueuedAt)? Map(AndroidXWorkInfo? info)
    {
        if (info is null)
        {
            return null;
        }

        var (workId, workerName, enqueuedAt) = AndroidWorkMapper.ParseTags(info.Tags ?? []);
        if (workId is null)
        {
            return null;
        }

        // Enum constants and Data.EMPTY are shared peers: they are not disposed.
        var status = AndroidWorkMapper.MapState(info.GetState()?.Name());
        var next = info.NextScheduleTimeMillis;
        var (output, message) = AndroidWorkMapper.DecodeOutput(FromData(info.OutputData));
        return (new WorkInfo
        {
            Id = workId,
            WorkerName = workerName ?? string.Empty,
            Status = status,
            Attempts = info.RunAttemptCount,
            EarliestNextRun = status == WorkStatus.Enqueued && next > 0 && next < long.MaxValue ? DateTimeOffset.FromUnixTimeMilliseconds(next) : null,
            LastMessage = message,
            Output = output,
        }, enqueuedAt);
    }

    private void Raise(WorkInfo info)
    {
        try
        {
            WorkChanged?.Invoke(this, info);
        }
        catch (Exception ex)
        {
            LogHandlerFailed(_logger, info.Id, ex);
        }
    }

    [LoggerMessage(EventId = 3971, Level = LogLevel.Debug, Message = "Scheduled work {WorkId} ({WorkerName}): {Status}.")]
    private static partial void LogScheduled(ILogger logger, string workId, string workerName, WorkStatus status);

    [LoggerMessage(EventId = 3972, Level = LogLevel.Information, Message = "Periodic work {WorkId} could not be updated in place; it was cancelled and re-enqueued.")]
    private static partial void LogUpdateFallback(ILogger logger, string workId, Exception exception);

    [LoggerMessage(EventId = 3973, Level = LogLevel.Error, Message = "A WorkChanged handler failed for {WorkId}.")]
    private static partial void LogHandlerFailed(ILogger logger, string workId, Exception exception);

    private sealed record Box(WorkInfo Info, long EnqueuedAt);
}
