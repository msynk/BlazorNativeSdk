using System.Collections.Concurrent;
using BlazorNativeSdk.Lifecycle;
using BlazorNativeSdk.Storage;
using BlazorNativeSdk.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace BlazorNativeSdk.Tests;

public sealed class BackgroundWorkTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-03-01T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private readonly ServiceProvider _provider;
    private readonly ScriptedWorker _worker = new();

    public BackgroundWorkTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton(_worker);
        services.AddBlazorNativeSdk(sdk =>
        {
            sdk.AddFakes();
            sdk.AddInProcessBackgroundWork(o => o.MaximumBackoff = TimeSpan.FromMinutes(10));
            sdk.Services.AddKeyedSingleton<IBackgroundWorker>("sync", (sp, _) => sp.GetRequiredService<ScriptedWorker>());
        });
        _provider = services.BuildServiceProvider();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private InProcessBackgroundWorkScheduler Scheduler => _provider.GetRequiredService<InProcessBackgroundWorkScheduler>();

    [Fact]
    public async Task Runs_due_work_and_records_output()
    {
        _worker.Results.Enqueue(WorkResult.Success(new Dictionary<string, string> { ["synced"] = "3" }));

        var scheduled = await Scheduler.ScheduleAsync(new WorkRequest { Id = "w1", WorkerName = "sync", Input = new Dictionary<string, string> { ["folder"] = "inbox" } }, Ct);
        Assert.True(scheduled.IsSuccess);

        Assert.Equal(1, await Scheduler.RunDueWorkAsync(false, Ct));

        var info = Assert.Single((await Scheduler.GetWorkAsync(Ct)).Value!);
        Assert.Equal(WorkStatus.Succeeded, info.Status);
        Assert.Equal("3", info.Output["synced"]);
        Assert.Equal("inbox", Assert.Single(_worker.Contexts).Input["folder"]);
    }

    [Fact]
    public async Task Respects_initial_delay()
    {
        await Scheduler.ScheduleAsync(new WorkRequest { Id = "later", WorkerName = "sync", InitialDelay = TimeSpan.FromMinutes(5) }, Ct);

        Assert.Equal(0, await Scheduler.RunDueWorkAsync(false, Ct));
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, await Scheduler.RunDueWorkAsync(false, Ct));
    }

    [Fact]
    public async Task Retries_with_exponential_backoff_then_fails()
    {
        for (var i = 0; i < 3; i++)
        {
            _worker.Results.Enqueue(WorkResult.Retry("server busy"));
        }

        await Scheduler.ScheduleAsync(new WorkRequest { Id = "r", WorkerName = "sync", BackoffDelay = TimeSpan.FromSeconds(10), MaxAttempts = 3 }, Ct);

        await Scheduler.RunDueWorkAsync(false, Ct);
        var first = (await Scheduler.GetWorkAsync(Ct)).Value!.Single();
        Assert.Equal(WorkStatus.Enqueued, first.Status);
        Assert.Equal(_time.GetUtcNow().AddSeconds(10), first.EarliestNextRun);

        _time.Advance(TimeSpan.FromSeconds(10));
        await Scheduler.RunDueWorkAsync(false, Ct);
        var second = (await Scheduler.GetWorkAsync(Ct)).Value!.Single();
        Assert.Equal(_time.GetUtcNow().AddSeconds(20), second.EarliestNextRun);

        _time.Advance(TimeSpan.FromSeconds(20));
        await Scheduler.RunDueWorkAsync(false, Ct);
        var final = (await Scheduler.GetWorkAsync(Ct)).Value!.Single();
        Assert.Equal(WorkStatus.Failed, final.Status);
        Assert.Equal(3, final.Attempts);
        Assert.Equal("server busy", final.LastMessage);
    }

    [Fact]
    public async Task Periodic_work_is_rescheduled()
    {
        await Scheduler.ScheduleAsync(new WorkRequest { Id = "p", WorkerName = "sync", RepeatInterval = TimeSpan.FromHours(1) }, Ct);

        await Scheduler.RunDueWorkAsync(false, Ct);
        var info = (await Scheduler.GetWorkAsync(Ct)).Value!.Single();

        Assert.Equal(WorkStatus.Enqueued, info.Status);
        Assert.Equal(_time.GetUtcNow().AddHours(1), info.EarliestNextRun);
    }

    [Fact]
    public async Task Network_constraints_wait_for_connectivity()
    {
        var connectivity = _provider.GetRequiredService<FakeConnectivityService>();
        connectivity.GoOffline();
        await Scheduler.ScheduleAsync(new WorkRequest { Id = "n", WorkerName = "sync", Constraints = WorkConstraints.NetworkConnected }, Ct);

        Assert.Equal(0, await Scheduler.RunDueWorkAsync(false, Ct));

        connectivity.GoOnline(ConnectionType.Cellular, metered: true);
        Assert.Equal(1, await Scheduler.RunDueWorkAsync(false, Ct));
    }

    [Fact]
    public async Task Network_constraint_accepts_local_access_but_not_a_captive_portal()
    {
        // Windows reports local access when its internet probe fails, although servers may be reachable.
        var connectivity = _provider.GetRequiredService<FakeConnectivityService>();
        connectivity.Set(new ConnectivitySnapshot { Access = NetworkAccess.ConstrainedInternet, ConnectionTypes = [ConnectionType.Ethernet] });
        await Scheduler.ScheduleAsync(new WorkRequest { Id = "n", WorkerName = "sync", Constraints = WorkConstraints.NetworkConnected }, Ct);

        Assert.Equal(0, await Scheduler.RunDueWorkAsync(false, Ct));

        connectivity.Set(new ConnectivitySnapshot { Access = NetworkAccess.Local, ConnectionTypes = [ConnectionType.Ethernet] });
        Assert.Equal(1, await Scheduler.RunDueWorkAsync(false, Ct));
    }

    [Fact]
    public async Task Without_a_durable_store_the_policy_does_not_promise_persistence()
    {
        // AddFakes registers the in-memory state store.
        Assert.False(Scheduler.Policy.PersistsAcrossLaunches);
        Assert.False(Scheduler.Policy.SurvivesReboot);
        Assert.Contains("lost when the app exits", Scheduler.Policy.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("resumes after restarts", Scheduler.Policy.Summary, StringComparison.Ordinal);

        var status = await Scheduler.GetStatusAsync(Ct);
        Assert.DoesNotContain("persisted", status.Features);
        Assert.NotNull(status.Reason);
    }

    [Fact]
    public async Task Unmetered_constraint_skips_metered_networks()
    {
        var connectivity = _provider.GetRequiredService<FakeConnectivityService>();
        connectivity.GoOnline(ConnectionType.Cellular, metered: true);
        await Scheduler.ScheduleAsync(new WorkRequest { Id = "u", WorkerName = "sync", Constraints = WorkConstraints.UnmeteredNetwork }, Ct);

        Assert.Equal(0, await Scheduler.RunDueWorkAsync(false, Ct));
        connectivity.GoOnline(ConnectionType.WiFi);
        Assert.Equal(1, await Scheduler.RunDueWorkAsync(false, Ct));
    }

    [Fact]
    public async Task Unknown_workers_are_rejected_at_schedule_time()
    {
        var result = await Scheduler.ScheduleAsync(new WorkRequest { Id = "x", WorkerName = "missing" }, Ct);

        Assert.Equal(NativeResultStatus.Failed, result.Status);
        Assert.Equal(NativeErrorCodes.InvalidArgument, result.Error!.Code);
    }

    [Fact]
    public async Task Keep_policy_preserves_existing_work_and_cancel_removes_it()
    {
        await Scheduler.ScheduleAsync(new WorkRequest { Id = "k", WorkerName = "sync", InitialDelay = TimeSpan.FromHours(1) }, Ct);
        await Scheduler.ScheduleAsync(new WorkRequest { Id = "k", WorkerName = "sync", ExistingPolicy = ExistingWorkPolicy.Keep }, Ct);

        Assert.Equal(0, await Scheduler.RunDueWorkAsync(false, Ct));
        Assert.True((await Scheduler.CancelAsync("k", Ct)).Value);
        Assert.False((await Scheduler.CancelAsync("k", Ct)).Value);
        Assert.Empty((await Scheduler.GetWorkAsync(Ct)).Value!);
    }

    [Fact]
    public async Task Worker_exceptions_become_retries()
    {
        _worker.Throw = true;
        await Scheduler.ScheduleAsync(new WorkRequest { Id = "e", WorkerName = "sync" }, Ct);

        await Scheduler.RunDueWorkAsync(false, Ct);

        var info = (await Scheduler.GetWorkAsync(Ct)).Value!.Single();
        Assert.Equal(WorkStatus.Enqueued, info.Status);
        Assert.Contains("boom", info.LastMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Does_not_run_in_background_when_configured_for_mobile()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddBlazorNativeSdk(sdk =>
        {
            sdk.AddFakes();
            sdk.AddInProcessBackgroundWork(o => o.RunWhileInBackground = false);
            sdk.AddBackgroundWorker<ScriptedWorker>("sync");
        });
        await using var provider = services.BuildServiceProvider();
        var scheduler = provider.GetRequiredService<InProcessBackgroundWorkScheduler>();
        var lifecycle = provider.GetRequiredService<FakeAppLifecycle>();
        await scheduler.ScheduleAsync(new WorkRequest { Id = "bg", WorkerName = "sync" }, Ct);

        await lifecycle.SimulateBackgroundAsync();
        Assert.Equal(0, await scheduler.RunDueWorkAsync(false, Ct));
        Assert.Equal(1, await scheduler.RunDueWorkAsync(isOsInitiated: true, Ct));
        Assert.False(scheduler.Policy.RunsInBackground);
    }

    [Fact]
    public async Task Interrupted_runs_are_requeued_on_start()
    {
        var store = _provider.GetRequiredService<INativeStateStore>();
        await Scheduler.ScheduleAsync(new WorkRequest { Id = "crash", WorkerName = "sync", InitialDelay = TimeSpan.FromDays(1) }, Ct);
        var key = (await store.GetKeysAsync("bns.work.", Ct)).Single();
        var json = (await store.GetAsync(key, Ct))!.Replace("\"enqueued\"", "\"running\"", StringComparison.OrdinalIgnoreCase);
        await store.SetAsync(key, json, Ct);

        await Scheduler.StartAsync(Ct);

        var info = (await Scheduler.GetWorkAsync(Ct)).Value!.Single();
        Assert.Equal(WorkStatus.Enqueued, info.Status);
        Assert.Equal(_time.GetUtcNow(), info.EarliestNextRun);
    }

    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();

    private sealed class ScriptedWorker : IBackgroundWorker
    {
        public ConcurrentQueue<WorkResult> Results { get; } = new();

        public ConcurrentQueue<WorkContext> Contexts { get; } = new();

        public bool Throw { get; set; }

        public ValueTask<WorkResult> ExecuteAsync(WorkContext context, CancellationToken cancellationToken)
        {
            Contexts.Enqueue(context);
            if (Throw)
            {
                throw new InvalidOperationException("boom");
            }

            return ValueTask.FromResult(Results.TryDequeue(out var result) ? result : WorkResult.Success());
        }
    }
}

public sealed class LifecycleTests
{
    [Fact]
    public async Task Transitions_raise_events_and_run_handlers_inside_a_lease_when_backgrounding()
    {
        var lifecycle = new FakeAppLifecycle();
        var events = new List<(AppLifecycleState, AppLifecycleState)>();
        var tokens = new List<bool>();
        lifecycle.StateChanged += (_, e) => events.Add((e.Previous, e.Current));
        using var _ = lifecycle.RegisterTransitionHandler((e, ct) =>
        {
            if (e.IsBackgrounding)
            {
                tokens.Add(ct.CanBeCanceled);
            }

            return ValueTask.CompletedTask;
        });

        await lifecycle.SimulateBackgroundAsync();
        await lifecycle.SimulateResumeAsync();

        Assert.Equal(
            [(AppLifecycleState.Active, AppLifecycleState.Inactive), (AppLifecycleState.Inactive, AppLifecycleState.Background),
             (AppLifecycleState.Background, AppLifecycleState.Inactive), (AppLifecycleState.Inactive, AppLifecycleState.Active)],
            events);
        Assert.Equal([true], tokens);
        Assert.False(lifecycle.ReportState(AppLifecycleState.Active));
    }

    [Fact]
    public async Task Handlers_stop_when_the_lease_expires()
    {
        var lifecycle = new FakeAppLifecycle();
        var gate = new TaskCompletionSource();
        var ran = 0;
        using var a = lifecycle.RegisterTransitionHandler(async (e, ct) =>
        {
            if (e.IsBackgrounding)
            {
                gate.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
        });
        using var b = lifecycle.RegisterTransitionHandler((e, _) =>
        {
            if (e.IsBackgrounding)
            {
                Interlocked.Increment(ref ran);
            }

            return ValueTask.CompletedTask;
        });

        lifecycle.ReportState(AppLifecycleState.Background);
        await gate.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        lifecycle.LastLease!.Expire();
        await lifecycle.LastTransitionCompletion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(0, ran);
    }
}
