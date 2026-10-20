using System.Globalization;
using BlazorNativeSdk.Lifecycle;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>What a <see cref="NativeSdkWorker"/> run returns to WorkManager.</summary>
internal enum AndroidWorkOutcome
{
    Success,
    Retry,
    Failure,
}

/// <summary>
/// Pure WorkManager mapping: tags, input/output keys, state and result mapping, interval clamping. Works on plain values so
/// it can be unit tested without the AndroidX bindings.
/// </summary>
internal static class AndroidWorkMapper
{
    public const string Tag = "bns";
    public const string IdTagPrefix = "bns.id:";
    public const string WorkerTagPrefix = "bns.worker:";
    public const string EnqueuedTagPrefix = "bns.enqueued:";
    public const string UniqueNamePrefix = "bns:";
    public const string WorkerNameKey = "bns.worker";
    public const string WorkIdKey = "bns.id";
    public const string MaxAttemptsKey = "bns.max-attempts";
    public const string InputPrefix = "bns.in.";
    public const string OutputPrefix = "bns.out.";
    public const string MessageKey = "bns.message";

    /// <summary>WorkManager's minimum periodic interval (<c>PeriodicWorkRequest.MIN_PERIODIC_INTERVAL_MILLIS</c>).</summary>
    public static readonly TimeSpan MinimumPeriodicInterval = TimeSpan.FromMinutes(15);

    /// <summary>WorkManager's minimum backoff (<c>WorkRequest.MIN_BACKOFF_MILLIS</c>).</summary>
    public static readonly TimeSpan MinimumBackoff = TimeSpan.FromSeconds(10);

    /// <summary>WorkManager's maximum backoff (<c>WorkRequest.MAX_BACKOFF_MILLIS</c>).</summary>
    public static readonly TimeSpan MaximumBackoff = TimeSpan.FromHours(5);

    /// <summary>The JobScheduler execution limit for one run.</summary>
    public static readonly TimeSpan MaximumExecutionTime = TimeSpan.FromMinutes(10);

    public static string UniqueName(string workId) => UniqueNamePrefix + workId;

    public static TimeSpan ClampRepeatInterval(TimeSpan interval) => interval < MinimumPeriodicInterval ? MinimumPeriodicInterval : interval;

    public static TimeSpan ClampBackoff(TimeSpan backoff) =>
        backoff < MinimumBackoff ? MinimumBackoff : backoff > MaximumBackoff ? MaximumBackoff : backoff;

    public static TimeSpan ClampDelay(TimeSpan delay) => delay < TimeSpan.Zero ? TimeSpan.Zero : delay;

    /// <summary>The tags attached to a request.</summary>
    public static string[] CreateTags(string workId, string workerName, DateTimeOffset enqueuedAt) =>
    [
        Tag,
        IdTagPrefix + workId,
        WorkerTagPrefix + workerName,
        EnqueuedTagPrefix + enqueuedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
    ];

    /// <summary>Extracts the work id, worker name and enqueue time from tags.</summary>
    public static (string? WorkId, string? WorkerName, long EnqueuedAt) ParseTags(IEnumerable<string> tags)
    {
        string? id = null;
        string? worker = null;
        long enqueued = 0;
        foreach (var tag in tags)
        {
            if (tag.StartsWith(IdTagPrefix, StringComparison.Ordinal))
            {
                id = tag[IdTagPrefix.Length..];
            }
            else if (tag.StartsWith(WorkerTagPrefix, StringComparison.Ordinal))
            {
                worker = tag[WorkerTagPrefix.Length..];
            }
            else if (tag.StartsWith(EnqueuedTagPrefix, StringComparison.Ordinal) &&
                     long.TryParse(tag.AsSpan(EnqueuedTagPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                enqueued = value;
            }
        }

        return (id, worker, enqueued);
    }

    /// <summary>Encodes the request into WorkManager input data entries.</summary>
    public static Dictionary<string, string> EncodeInput(WorkRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [WorkerNameKey] = request.WorkerName,
            [WorkIdKey] = request.Id,
            [MaxAttemptsKey] = Math.Max(1, request.MaxAttempts).ToString(CultureInfo.InvariantCulture),
        };
        foreach (var (key, value) in request.Input)
        {
            data[InputPrefix + key] = value;
        }

        return data;
    }

    /// <summary>Decodes input data entries.</summary>
    public static (string? WorkerName, string? WorkId, int MaxAttempts, Dictionary<string, string> Input) DecodeInput(IEnumerable<KeyValuePair<string, string?>> entries)
    {
        string? worker = null;
        string? id = null;
        var maxAttempts = 5;
        var input = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            if (key == WorkerNameKey)
            {
                worker = value;
            }
            else if (key == WorkIdKey)
            {
                id = value;
            }
            else if (key == MaxAttemptsKey && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                maxAttempts = Math.Max(1, parsed);
            }
            else if (key.StartsWith(InputPrefix, StringComparison.Ordinal) && value is not null)
            {
                input[key[InputPrefix.Length..]] = value;
            }
        }

        return (worker, id, maxAttempts, input);
    }

    /// <summary>Encodes a worker result into output data entries.</summary>
    public static Dictionary<string, string> EncodeOutput(WorkResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        if (result.Message is not null)
        {
            data[MessageKey] = result.Message;
        }

        foreach (var (key, value) in result.Output)
        {
            data[OutputPrefix + key] = value;
        }

        return data;
    }

    /// <summary>Decodes output data entries.</summary>
    public static (Dictionary<string, string> Output, string? Message) DecodeOutput(IEnumerable<KeyValuePair<string, string?>> entries)
    {
        string? message = null;
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            if (key == MessageKey)
            {
                message = value;
            }
            else if (key.StartsWith(OutputPrefix, StringComparison.Ordinal) && value is not null)
            {
                output[key[OutputPrefix.Length..]] = value;
            }
        }

        return (output, message);
    }

    /// <summary>Maps a <c>WorkInfo.State</c> name.</summary>
    public static WorkStatus MapState(string? stateName) => stateName switch
    {
        "RUNNING" => WorkStatus.Running,
        "SUCCEEDED" => WorkStatus.Succeeded,
        "FAILED" => WorkStatus.Failed,
        "CANCELLED" => WorkStatus.Cancelled,
        _ => WorkStatus.Enqueued, // ENQUEUED, BLOCKED
    };

    /// <summary>
    /// Maps a worker result. A retry becomes a failure once <paramref name="maxAttempts"/> runs have happened
    /// (<paramref name="runAttemptCount"/> is zero-based, as reported by WorkManager).
    /// </summary>
    public static AndroidWorkOutcome MapResult(WorkResultKind kind, int runAttemptCount, int maxAttempts) => kind switch
    {
        WorkResultKind.Success => AndroidWorkOutcome.Success,
        WorkResultKind.Retry when runAttemptCount + 1 >= Math.Max(1, maxAttempts) => AndroidWorkOutcome.Failure,
        WorkResultKind.Retry => AndroidWorkOutcome.Retry,
        _ => AndroidWorkOutcome.Failure,
    };

    /// <summary>
    /// Picks the record that represents a work id when WorkManager keeps several (replaced or finished generations): pending
    /// work first, then the most recently enqueued.
    /// </summary>
    public static T? SelectCurrent<T>(IEnumerable<T> candidates, Func<T, WorkStatus> status, Func<T, long> enqueuedAt)
        where T : class =>
        candidates
            .OrderByDescending(c => status(c) is WorkStatus.Enqueued or WorkStatus.Running)
            .ThenByDescending(enqueuedAt)
            .FirstOrDefault();
}
