namespace BlazorNativeSdk.Lifecycle;

/// <summary>Conditions that must hold before work runs.</summary>
[Flags]
public enum WorkConstraints
{
    /// <summary>No constraints.</summary>
    None = 0,

    /// <summary>Any network connection.</summary>
    NetworkConnected = 1,

    /// <summary>An unmetered (typically Wi-Fi) connection.</summary>
    UnmeteredNetwork = 2,

    /// <summary>The device is charging.</summary>
    Charging = 4,

    /// <summary>The battery is not low.</summary>
    BatteryNotLow = 8,

    /// <summary>Storage is not low.</summary>
    StorageNotLow = 16,

    /// <summary>The device is idle (Android only).</summary>
    DeviceIdle = 32,
}

/// <summary>What to do when work with the same id already exists.</summary>
public enum ExistingWorkPolicy
{
    /// <summary>Replace the existing work.</summary>
    Replace,

    /// <summary>Keep the existing work and ignore the new request.</summary>
    Keep,
}

/// <summary>A request to run a registered <see cref="IBackgroundWorker"/>.</summary>
public sealed record WorkRequest
{
    /// <summary>A unique, stable id. Scheduling the same id again follows <see cref="ExistingPolicy"/>.</summary>
    public required string Id { get; init; }

    /// <summary>The name the worker was registered with.</summary>
    public required string WorkerName { get; init; }

    /// <summary>Input passed to the worker. Keep it small; store larger payloads elsewhere and pass a key.</summary>
    public IReadOnlyDictionary<string, string> Input { get; init; } = new Dictionary<string, string>();

    /// <summary>Execution constraints. Unsupported constraints are reported by <see cref="BackgroundExecutionPolicy.SupportedConstraints"/>.</summary>
    public WorkConstraints Constraints { get; init; }

    /// <summary>The minimum delay before the first run.</summary>
    public TimeSpan InitialDelay { get; init; }

    /// <summary>Run periodically with this interval. Platforms enforce minimums (<see cref="BackgroundExecutionPolicy.MinimumPeriodicInterval"/>).</summary>
    public TimeSpan? RepeatInterval { get; init; }

    /// <summary>What to do when work with the same id already exists.</summary>
    public ExistingWorkPolicy ExistingPolicy { get; init; } = ExistingWorkPolicy.Replace;

    /// <summary>The initial delay before retrying after <see cref="WorkResult.Retry"/>; doubles with each attempt.</summary>
    public TimeSpan BackoffDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The maximum number of attempts before the work is marked failed.</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>
    /// The work needs more time than a short refresh window (iOS schedules it as a <c>BGProcessingTask</c>, typically
    /// overnight while charging). Ignored where irrelevant.
    /// </summary>
    public bool IsLongRunning { get; init; }
}

/// <summary>The state of scheduled work.</summary>
public enum WorkStatus
{
    /// <summary>Waiting for its time or constraints.</summary>
    Enqueued,

    /// <summary>Executing.</summary>
    Running,

    /// <summary>Completed successfully (one-time work).</summary>
    Succeeded,

    /// <summary>Failed permanently.</summary>
    Failed,

    /// <summary>Cancelled.</summary>
    Cancelled,
}

/// <summary>Information about scheduled work.</summary>
public sealed record WorkInfo
{
    /// <summary>The work id.</summary>
    public required string Id { get; init; }

    /// <summary>The worker name.</summary>
    public required string WorkerName { get; init; }

    /// <summary>The status.</summary>
    public required WorkStatus Status { get; init; }

    /// <summary>Number of attempts so far.</summary>
    public int Attempts { get; init; }

    /// <summary>The earliest next run time, when known. Platforms decide the actual time.</summary>
    public DateTimeOffset? EarliestNextRun { get; init; }

    /// <summary>When the work last ran.</summary>
    public DateTimeOffset? LastRunAt { get; init; }

    /// <summary>A message from the last run.</summary>
    public string? LastMessage { get; init; }

    /// <summary>Output from the last successful run.</summary>
    public IReadOnlyDictionary<string, string> Output { get; init; } = new Dictionary<string, string>();

    /// <summary>The constraints requested for the work.</summary>
    public WorkConstraints Constraints { get; init; }

    /// <summary>The repeat interval of periodic work (after platform clamping), or <see langword="null"/> for one-time work.</summary>
    public TimeSpan? RepeatInterval { get; init; }

    /// <summary>Whether the work was scheduled as long-running.</summary>
    public bool IsLongRunning { get; init; }
}

/// <summary>Context passed to a worker.</summary>
public sealed record WorkContext
{
    /// <summary>The work id.</summary>
    public required string WorkId { get; init; }

    /// <summary>The worker name.</summary>
    public required string WorkerName { get; init; }

    /// <summary>Input from the request.</summary>
    public IReadOnlyDictionary<string, string> Input { get; init; } = new Dictionary<string, string>();

    /// <summary>The attempt number, starting at 1.</summary>
    public int Attempt { get; init; } = 1;

    /// <summary>
    /// <see langword="true"/> when the OS woke the app to run this work (no UI may exist). Workers must not depend on
    /// Blazor components, JS interop or navigation in that case.
    /// </summary>
    public bool IsOsInitiated { get; init; }
}

/// <summary>The outcome kinds of a worker run.</summary>
public enum WorkResultKind
{
    /// <summary>Done.</summary>
    Success,

    /// <summary>Try again later with backoff.</summary>
    Retry,

    /// <summary>Do not retry.</summary>
    Failure,
}

/// <summary>The result of a worker run.</summary>
public sealed record WorkResult
{
    /// <summary>The outcome.</summary>
    public required WorkResultKind Kind { get; init; }

    /// <summary>A diagnostic message.</summary>
    public string? Message { get; init; }

    /// <summary>Output data.</summary>
    public IReadOnlyDictionary<string, string> Output { get; init; } = new Dictionary<string, string>();

    /// <summary>Creates a success result.</summary>
    public static WorkResult Success(IReadOnlyDictionary<string, string>? output = null) =>
        new() { Kind = WorkResultKind.Success, Output = output ?? new Dictionary<string, string>() };

    /// <summary>Creates a retry result.</summary>
    public static WorkResult Retry(string? message = null) => new() { Kind = WorkResultKind.Retry, Message = message };

    /// <summary>Creates a failure result.</summary>
    public static WorkResult Failure(string? message = null) => new() { Kind = WorkResultKind.Failure, Message = message };
}

/// <summary>A unit of deferrable work. Register implementations with <c>AddBackgroundWorker&lt;T&gt;(name)</c>.</summary>
/// <remarks>Workers are resolved from a new DI scope per run and must be idempotent: platforms may run them more than once.</remarks>
public interface IBackgroundWorker
{
    /// <summary>Executes the work. Honor <paramref name="cancellationToken"/>: it fires when the OS revokes execution time.</summary>
    ValueTask<WorkResult> ExecuteAsync(WorkContext context, CancellationToken cancellationToken);
}

/// <summary>
/// The guarantees a background work adapter actually provides. Surface this in documentation and UX instead of
/// assuming unrestricted background execution.
/// </summary>
public sealed record BackgroundExecutionPolicy
{
    /// <summary>The adapter name.</summary>
    public required string Adapter { get; init; }

    /// <summary>Work can run while the app is in the background.</summary>
    public required bool RunsInBackground { get; init; }

    /// <summary>The OS can start the app process to run work after it was terminated.</summary>
    public required bool RunsAfterTermination { get; init; }

    /// <summary>Scheduled work survives a device reboot.</summary>
    public required bool SurvivesReboot { get; init; }

    /// <summary>Pending work is persisted and resumes when the app starts again.</summary>
    public required bool PersistsAcrossLaunches { get; init; }

    /// <summary>Work runs at the requested time. <see langword="false"/> everywhere except foreground in-process schedulers.</summary>
    public bool ExactTiming { get; init; }

    /// <summary>The shortest periodic interval the platform honors.</summary>
    public TimeSpan? MinimumPeriodicInterval { get; init; }

    /// <summary>The maximum time a single run may take before the OS stops it.</summary>
    public TimeSpan? MaximumExecutionTime { get; init; }

    /// <summary>Constraints the adapter can enforce.</summary>
    public WorkConstraints SupportedConstraints { get; init; }

    /// <summary>
    /// Constraints that are only enforced in some situations (for example iOS enforces <see cref="WorkConstraints.Charging"/>
    /// only for long-running work). <see cref="Summary"/> explains when.
    /// </summary>
    public WorkConstraints PartiallySupportedConstraints { get; init; }

    /// <summary>A short human-readable summary of the guarantees and caveats.</summary>
    public required string Summary { get; init; }
}

/// <summary>Schedules deferrable work that survives app restarts where the platform allows it.</summary>
public interface IBackgroundWorkScheduler : INativeCapability
{
    /// <summary>The guarantees the current adapter provides.</summary>
    BackgroundExecutionPolicy Policy { get; }

    /// <summary>Schedules work.</summary>
    ValueTask<NativeResult<WorkInfo>> ScheduleAsync(WorkRequest request, CancellationToken cancellationToken = default);

    /// <summary>Cancels work.</summary>
    /// <returns><see langword="true"/> when work with the id existed.</returns>
    ValueTask<NativeResult<bool>> CancelAsync(string workId, CancellationToken cancellationToken = default);

    /// <summary>Lists known work.</summary>
    ValueTask<NativeResult<IReadOnlyList<WorkInfo>>> GetWorkAsync(CancellationToken cancellationToken = default);

    /// <summary>Raised when work changes state (in-process only; OS-initiated runs are reported on next launch).</summary>
    event EventHandler<WorkInfo>? WorkChanged;
}
