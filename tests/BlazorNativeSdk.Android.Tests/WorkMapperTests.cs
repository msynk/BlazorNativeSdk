using BlazorNativeSdk.Lifecycle;

namespace BlazorNativeSdk.AndroidPlatform.Tests;

/// <summary>WorkManager mapping; limits from docs/adapters/android.md, "Background execution: guarantees and limits".</summary>
public sealed class WorkMapperTests
{
    [Fact]
    public void Limits_match_WorkManager()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), AndroidWorkMapper.MinimumPeriodicInterval);  // PeriodicWorkRequest.MIN_PERIODIC_INTERVAL_MILLIS
        Assert.Equal(TimeSpan.FromSeconds(10), AndroidWorkMapper.MinimumBackoff);           // WorkRequest.MIN_BACKOFF_MILLIS
        Assert.Equal(TimeSpan.FromHours(5), AndroidWorkMapper.MaximumBackoff);              // WorkRequest.MAX_BACKOFF_MILLIS
        Assert.Equal(TimeSpan.FromMinutes(10), AndroidWorkMapper.MaximumExecutionTime);     // "~10 min per run"
    }

    [Theory]
    [InlineData(0, 15)]
    [InlineData(1, 15)]
    [InlineData(14.99, 15)]
    [InlineData(15, 15)]
    [InlineData(16, 16)]
    [InlineData(1440, 1440)]
    public void Periodic_intervals_are_clamped_to_at_least_15_minutes(double minutes, double expected) =>
        Assert.Equal(TimeSpan.FromMinutes(expected), AndroidWorkMapper.ClampRepeatInterval(TimeSpan.FromMinutes(minutes)));

    [Theory]
    [InlineData(-5, 10)]
    [InlineData(0, 10)]
    [InlineData(1, 10)]
    [InlineData(9.999, 10)]
    [InlineData(10, 10)]
    [InlineData(30, 30)]
    [InlineData(5 * 3600, 5 * 3600)]
    [InlineData(5 * 3600 + 1, 5 * 3600)]
    [InlineData(24 * 3600, 5 * 3600)]
    public void Backoff_is_clamped_between_10_seconds_and_5_hours(double seconds, double expected) =>
        Assert.Equal(TimeSpan.FromSeconds(expected), AndroidWorkMapper.ClampBackoff(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(-60, 0)]
    [InlineData(0, 0)]
    [InlineData(42, 42)]
    public void Negative_initial_delays_become_zero(double seconds, double expected) =>
        Assert.Equal(TimeSpan.FromSeconds(expected), AndroidWorkMapper.ClampDelay(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Each_work_id_is_a_unique_work_name() => Assert.Equal("bns:sync-42", AndroidWorkMapper.UniqueName("sync-42"));

    [Fact]
    public void Tags_round_trip()
    {
        var enqueuedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_123);

        var tags = AndroidWorkMapper.CreateTags("sync-42", "sync", enqueuedAt);
        var (workId, workerName, enqueued) = AndroidWorkMapper.ParseTags(tags);

        Assert.Equal(AndroidWorkMapper.Tag, tags[0]);
        Assert.Equal("sync-42", workId);
        Assert.Equal("sync", workerName);
        Assert.Equal(enqueuedAt.ToUnixTimeMilliseconds(), enqueued);
    }

    [Fact]
    public void Tag_values_may_contain_the_prefix_separator()
    {
        var (workId, workerName, _) = AndroidWorkMapper.ParseTags(AndroidWorkMapper.CreateTags("a:b:c", "w:1", DateTimeOffset.UnixEpoch));

        Assert.Equal("a:b:c", workId);
        Assert.Equal("w:1", workerName);
    }

    [Fact]
    public void Foreign_and_malformed_tags_are_ignored()
    {
        var (workId, workerName, enqueued) = AndroidWorkMapper.ParseTags(
            ["androidx.work.impl.workers.ConstraintTrackingWorker", "bns", "bns.enqueued:not-a-number", "other:tag"]);

        Assert.Null(workId);
        Assert.Null(workerName);
        Assert.Equal(0, enqueued);
    }

    [Fact]
    public void Input_round_trips()
    {
        var request = new WorkRequest
        {
            Id = "sync-42",
            WorkerName = "sync",
            MaxAttempts = 3,
            Input = new Dictionary<string, string> { ["folder"] = "inbox", ["bns.id"] = "user key that looks like an SDK key" },
        };

        var encoded = AndroidWorkMapper.EncodeInput(request);
        var (workerName, workId, maxAttempts, input) = AndroidWorkMapper.DecodeInput(Entries(encoded));

        Assert.Equal("sync", workerName);
        Assert.Equal("sync-42", workId);
        Assert.Equal(3, maxAttempts);
        Assert.Equal(request.Input, input);
        Assert.Equal("inbox", encoded[AndroidWorkMapper.InputPrefix + "folder"]);
    }

    [Theory]
    [InlineData(0, "1")]
    [InlineData(-3, "1")]
    [InlineData(1, "1")]
    [InlineData(7, "7")]
    public void Max_attempts_are_at_least_one(int maxAttempts, string expected)
    {
        var encoded = AndroidWorkMapper.EncodeInput(new WorkRequest { Id = "w", WorkerName = "x", MaxAttempts = maxAttempts });

        Assert.Equal(expected, encoded[AndroidWorkMapper.MaxAttemptsKey]);
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData("garbage", 5)]
    [InlineData("0", 1)]
    [InlineData("-2", 1)]
    [InlineData("9", 9)]
    public void Decoded_max_attempts_default_to_five_and_are_at_least_one(string? stored, int expected)
    {
        var entries = new List<KeyValuePair<string, string?>> { new(AndroidWorkMapper.WorkerNameKey, "x") };
        if (stored is not null)
        {
            entries.Add(new(AndroidWorkMapper.MaxAttemptsKey, stored));
        }

        Assert.Equal(expected, AndroidWorkMapper.DecodeInput(entries).MaxAttempts);
    }

    [Fact]
    public void Decoding_input_skips_null_values_and_foreign_keys()
    {
        var (_, _, _, input) = AndroidWorkMapper.DecodeInput(
        [
            new("bns.in.kept", "1"),
            new("bns.in.dropped", null),
            new("unrelated", "2"),
        ]);

        Assert.Equal(new Dictionary<string, string> { ["kept"] = "1" }, input);
    }

    [Fact]
    public void Output_round_trips()
    {
        var result = WorkResult.Success(new Dictionary<string, string> { ["count"] = "3" }) with { Message = "done" };

        var (output, message) = AndroidWorkMapper.DecodeOutput(Entries(AndroidWorkMapper.EncodeOutput(result)));

        Assert.Equal(result.Output, output);
        Assert.Equal("done", message);
    }

    [Fact]
    public void A_missing_message_is_not_encoded()
    {
        var encoded = AndroidWorkMapper.EncodeOutput(WorkResult.Success());

        Assert.Empty(encoded);
        Assert.Null(AndroidWorkMapper.DecodeOutput(Entries(encoded)).Message);
    }

    [Theory]
    [InlineData("ENQUEUED", WorkStatus.Enqueued)]
    [InlineData("BLOCKED", WorkStatus.Enqueued)]
    [InlineData("RUNNING", WorkStatus.Running)]
    [InlineData("SUCCEEDED", WorkStatus.Succeeded)]
    [InlineData("FAILED", WorkStatus.Failed)]
    [InlineData("CANCELLED", WorkStatus.Cancelled)]
    [InlineData(null, WorkStatus.Enqueued)]
    [InlineData("SOMETHING_NEW", WorkStatus.Enqueued)]
    public void Maps_WorkInfo_states(string? state, WorkStatus expected) =>
        Assert.Equal(expected, AndroidWorkMapper.MapState(state));

    [Theory]
    [InlineData(WorkResultKind.Success, 0, 5, nameof(AndroidWorkOutcome.Success))]
    [InlineData(WorkResultKind.Success, 9, 1, nameof(AndroidWorkOutcome.Success))]
    [InlineData(WorkResultKind.Failure, 0, 5, nameof(AndroidWorkOutcome.Failure))]
    [InlineData(WorkResultKind.Retry, 0, 5, nameof(AndroidWorkOutcome.Retry))]
    [InlineData(WorkResultKind.Retry, 3, 5, nameof(AndroidWorkOutcome.Retry))]
    [InlineData(WorkResultKind.Retry, 4, 5, nameof(AndroidWorkOutcome.Failure))]    // fifth run: MaxAttempts reached
    [InlineData(WorkResultKind.Retry, 10, 5, nameof(AndroidWorkOutcome.Failure))]
    [InlineData(WorkResultKind.Retry, 0, 1, nameof(AndroidWorkOutcome.Failure))]    // a single attempt never retries
    [InlineData(WorkResultKind.Retry, 0, 0, nameof(AndroidWorkOutcome.Failure))]    // invalid max treated as 1
    [InlineData(WorkResultKind.Retry, 0, 2, nameof(AndroidWorkOutcome.Retry))]
    [InlineData(WorkResultKind.Retry, 1, 2, nameof(AndroidWorkOutcome.Failure))]
    public void MaxAttempts_caps_retries(WorkResultKind kind, int runAttemptCount, int maxAttempts, string expected) =>
        Assert.Equal(Enum.Parse<AndroidWorkOutcome>(expected), AndroidWorkMapper.MapResult(kind, runAttemptCount, maxAttempts));

    [Fact]
    public void Pending_work_wins_over_newer_finished_generations()
    {
        var current = AndroidWorkMapper.SelectCurrent(
            [new Generation(WorkStatus.Succeeded, 300), new Generation(WorkStatus.Enqueued, 100), new Generation(WorkStatus.Cancelled, 200)],
            g => g.Status,
            g => g.EnqueuedAt);

        Assert.Equal(new Generation(WorkStatus.Enqueued, 100), current);
    }

    [Fact]
    public void The_most_recently_enqueued_generation_wins_otherwise()
    {
        var current = AndroidWorkMapper.SelectCurrent(
            [new Generation(WorkStatus.Failed, 100), new Generation(WorkStatus.Succeeded, 300), new Generation(WorkStatus.Running, 50), new Generation(WorkStatus.Enqueued, 70)],
            g => g.Status,
            g => g.EnqueuedAt);

        Assert.Equal(new Generation(WorkStatus.Enqueued, 70), current);
        Assert.Equal(
            new Generation(WorkStatus.Succeeded, 300),
            AndroidWorkMapper.SelectCurrent([new Generation(WorkStatus.Failed, 100), new Generation(WorkStatus.Succeeded, 300)], g => g.Status, g => g.EnqueuedAt));
    }

    [Fact]
    public void No_candidates_select_nothing() =>
        Assert.Null(AndroidWorkMapper.SelectCurrent(Array.Empty<Generation>(), g => g.Status, g => g.EnqueuedAt));

    // WorkManager Data values are nullable; the encoders produce non-null values.
    private static IEnumerable<KeyValuePair<string, string?>> Entries(Dictionary<string, string> data) =>
        data.Select(static pair => new KeyValuePair<string, string?>(pair.Key, pair.Value));

    private sealed record Generation(WorkStatus Status, long EnqueuedAt);
}
