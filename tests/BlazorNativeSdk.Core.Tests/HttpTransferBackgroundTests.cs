using System.Collections.Concurrent;
using System.Net;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;
using BlazorNativeSdk.Storage;
using BlazorNativeSdk.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorNativeSdk.Tests;

public sealed class HttpTransferBackgroundTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bns-transfer-bg-tests", Guid.NewGuid().ToString("N"));
    private readonly GatedServer _server = new();
    private readonly RecordingScheduler _scheduler = new();
    private ServiceProvider? _provider;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Enqueuing_hands_the_queue_to_the_scheduler()
    {
        var transfers = Create(useBackgroundWork: true);
        await transfers.StartAsync(Ct);
        Assert.Empty(_scheduler.Requests);

        await transfers.EnqueueDownloadAsync(new DownloadRequest { Id = "a", Uri = new Uri("https://files.test/a"), DestinationPath = "a.bin" }, Ct);

        var request = await _scheduler.NextAsync();
        Assert.Equal(HttpTransferService.BackgroundWorkName, request.Id);
        Assert.Equal(HttpTransferService.BackgroundWorkName, request.WorkerName);
        Assert.Equal(WorkConstraints.NetworkConnected, request.Constraints);
        Assert.Equal(ExistingWorkPolicy.Replace, request.ExistingPolicy);
        Assert.Equal(int.MaxValue, request.MaxAttempts);
    }

    [Fact]
    public async Task Starting_with_unfinished_transfers_keeps_an_existing_schedule()
    {
        var store = new InMemoryStateStore();
        var first = Create(useBackgroundWork: false, store);
        await first.EnqueueDownloadAsync(new DownloadRequest { Id = "a", Uri = new Uri("https://files.test/a"), DestinationPath = "a.bin" }, Ct);
        await _provider!.DisposeAsync();

        var second = Create(useBackgroundWork: true, store);
        await second.StartAsync(Ct);

        Assert.Equal(ExistingWorkPolicy.Keep, (await _scheduler.NextAsync()).ExistingPolicy);
    }

    [Fact]
    public async Task Policy_and_status_report_the_scheduler_guarantees()
    {
        var transfers = Create(useBackgroundWork: true, new FileStateStore(Path.Combine(_root, "state")));

        Assert.True(transfers.Policy.ContinuesInBackground);
        Assert.True(transfers.Policy.ContinuesAfterTermination);
        Assert.True(transfers.Policy.ResumesDownloads);
        var status = await transfers.GetStatusAsync(Ct);
        Assert.Contains("persisted", status.Features);
        Assert.Contains("continues-in-background", status.Features);
        Assert.Contains("continues-after-termination", status.Features);
        Assert.Null(status.Reason);
    }

    [Fact]
    public async Task Without_a_durable_store_nothing_continues_after_termination()
    {
        var transfers = Create(useBackgroundWork: true, new InMemoryStateStore());

        Assert.True(transfers.Policy.ContinuesInBackground);
        Assert.False(transfers.Policy.ContinuesAfterTermination);
        Assert.Contains("lost when the app exits", transfers.Policy.Summary, StringComparison.Ordinal);
        var status = await transfers.GetStatusAsync(Ct);
        Assert.DoesNotContain("persisted", status.Features);
        Assert.DoesNotContain("continues-after-termination", status.Features);
        Assert.NotNull(status.Reason);
    }

    [Fact]
    public async Task Without_background_work_nothing_is_scheduled_and_the_policy_is_process_bound()
    {
        var transfers = Create(useBackgroundWork: false);
        await transfers.StartAsync(Ct);

        await transfers.EnqueueDownloadAsync(new DownloadRequest { Id = "a", Uri = new Uri("https://files.test/a"), DestinationPath = "a.bin" }, Ct);
        await transfers.WaitForCompletionAsync("a", Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Empty(_scheduler.Requests);
        Assert.False(transfers.Policy.ContinuesInBackground);
        Assert.False(transfers.Policy.ContinuesAfterTermination);
    }

    [Fact]
    public async Task A_background_run_succeeds_once_every_transfer_finished()
    {
        var transfers = Create(useBackgroundWork: true);
        _server.Hold();
        await transfers.EnqueueDownloadAsync(new DownloadRequest { Id = "a", Uri = new Uri("https://files.test/a"), DestinationPath = "a.bin" }, Ct);

        var run = RunWorkerAsync(Ct);
        await _server.WaitForRequestAsync();
        Assert.False(run.IsCompleted);

        _server.Release();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(WorkResultKind.Success, result.Kind);
        Assert.Equal(TransferStatus.Completed, (await transfers.GetTransfersAsync(Ct)).Single().Status);
    }

    [Fact]
    public async Task A_background_run_asks_for_a_retry_while_transfers_wait_for_the_network()
    {
        var transfers = Create(useBackgroundWork: true);
        _provider!.GetRequiredService<FakeConnectivityService>().GoOffline();
        await transfers.EnqueueDownloadAsync(new DownloadRequest { Id = "a", Uri = new Uri("https://files.test/a"), DestinationPath = "a.bin" }, Ct);

        var result = await RunWorkerAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(WorkResultKind.Retry, result.Kind);
        Assert.Equal(TransferStatus.WaitingForNetwork, (await transfers.GetTransfersAsync(Ct)).Single().Status);
    }

    [Fact]
    public async Task Transfers_enqueued_during_a_background_run_are_drained_by_it()
    {
        var transfers = Create(useBackgroundWork: true);
        _server.Hold();
        await transfers.EnqueueDownloadAsync(new DownloadRequest { Id = "a", Uri = new Uri("https://files.test/a"), DestinationPath = "a.bin" }, Ct);
        await _scheduler.NextAsync();

        var run = RunWorkerAsync(Ct);
        await _server.WaitForRequestAsync();
        await transfers.EnqueueDownloadAsync(new DownloadRequest { Id = "b", Uri = new Uri("https://files.test/b"), DestinationPath = "b.bin" }, Ct);
        _server.Release();

        Assert.Equal(WorkResultKind.Success, (await run.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Kind);
        Assert.All(await transfers.GetTransfersAsync(Ct), t => Assert.Equal(TransferStatus.Completed, t.Status));
        Assert.Empty(_scheduler.Requests);
    }

    [Fact]
    public async Task Cancelling_a_background_run_propagates_and_releases_it()
    {
        var transfers = Create(useBackgroundWork: true);
        _server.Hold();
        await transfers.EnqueueDownloadAsync(new DownloadRequest { Id = "a", Uri = new Uri("https://files.test/a"), DestinationPath = "a.bin" }, Ct);
        await _scheduler.NextAsync();
        using var stop = new CancellationTokenSource();

        var run = RunWorkerAsync(stop.Token);
        await _server.WaitForRequestAsync();
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10), Ct));

        // The run is released, so a new transfer schedules work again.
        await transfers.EnqueueDownloadAsync(new DownloadRequest { Id = "b", Uri = new Uri("https://files.test/b"), DestinationPath = "b.bin" }, Ct);
        Assert.Equal(ExistingWorkPolicy.Replace, (await _scheduler.NextAsync()).ExistingPolicy);
        _server.Release();
    }

    public async ValueTask DisposeAsync()
    {
        _server.Release();
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private HttpTransferService Create(bool useBackgroundWork, INativeStateStore? store = null)
    {
        var services = new ServiceCollection();
        services.AddBlazorNativeSdk(sdk =>
        {
            sdk.AddFakes();
            sdk.AddCapability<IAppStorage>(NativeCapabilities.AppStorage, _ => new TempAppStorage(_root));
            sdk.AddCapability<IBackgroundWorkScheduler>(NativeCapabilities.BackgroundWork, _ => _scheduler);
            sdk.AddHttpTransfers(o =>
            {
                o.HttpClientFactory = _ => new HttpClient(_server);
                o.UseBackgroundWork = useBackgroundWork;
                o.ContinuesInBackground = false;
            });
        });
        if (store is not null)
        {
            services.AddSingleton(store);
        }

        _provider = services.BuildServiceProvider();
        return _provider.GetRequiredService<HttpTransferService>();
    }

    private Task<WorkResult> RunWorkerAsync(CancellationToken cancellationToken) => Task.Run(async () =>
    {
        await using var scope = _provider!.CreateAsyncScope();
        var worker = scope.ServiceProvider.GetRequiredKeyedService<IBackgroundWorker>(HttpTransferService.BackgroundWorkName);
        return await worker.ExecuteAsync(
            new WorkContext { WorkId = HttpTransferService.BackgroundWorkName, WorkerName = HttpTransferService.BackgroundWorkName, IsOsInitiated = true },
            cancellationToken);
    }, Ct);

    private sealed class TempAppStorage(string root) : FakeCapability(NativeCapabilities.AppStorage), IAppStorage
    {
        public NativeResult<string> GetDirectory(AppDirectory directory)
        {
            Directory.CreateDirectory(root);
            return root;
        }
    }

    private sealed class RecordingScheduler() : FakeCapability(NativeCapabilities.BackgroundWork), IBackgroundWorkScheduler
    {
        private readonly ConcurrentQueue<WorkRequest> _requests = new();
        private readonly SemaphoreSlim _scheduled = new(0);

        public BackgroundExecutionPolicy Policy { get; } = new()
        {
            Adapter = "recording",
            RunsInBackground = true,
            RunsAfterTermination = true,
            SurvivesReboot = true,
            PersistsAcrossLaunches = true,
            Summary = "test",
        };

        public List<WorkRequest> Requests => [.. _requests];

        public event EventHandler<Lifecycle.WorkInfo>? WorkChanged;

        public async Task<WorkRequest> NextAsync()
        {
            Assert.True(await _scheduled.WaitAsync(TimeSpan.FromSeconds(10), Ct), "Nothing was scheduled.");
            Assert.True(_requests.TryDequeue(out var request));
            return request;
        }

        public ValueTask<NativeResult<Lifecycle.WorkInfo>> ScheduleAsync(WorkRequest request, CancellationToken cancellationToken = default)
        {
            _requests.Enqueue(request);
            _scheduled.Release();
            var info = new Lifecycle.WorkInfo { Id = request.Id, WorkerName = request.WorkerName, Status = WorkStatus.Enqueued };
            WorkChanged?.Invoke(this, info);
            return ValueTask.FromResult<NativeResult<Lifecycle.WorkInfo>>(info);
        }

        public ValueTask<NativeResult<bool>> CancelAsync(string workId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<NativeResult<bool>>(false);

        public ValueTask<NativeResult<IReadOnlyList<Lifecycle.WorkInfo>>> GetWorkAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<NativeResult<IReadOnlyList<Lifecycle.WorkInfo>>>(Array.Empty<Lifecycle.WorkInfo>());
    }

    // Serves a small body; Hold() makes responses wait until Release() so tests can observe running transfers.
    private sealed class GatedServer : HttpMessageHandler
    {
        private readonly SemaphoreSlim _requested = new(0);
        private TaskCompletionSource _gate = CreateOpenGate();

        public void Hold() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public async Task WaitForRequestAsync() =>
            Assert.True(await _requested.WaitAsync(TimeSpan.FromSeconds(10), Ct), "No request arrived.");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requested.Release();
            await _gate.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        }

        private static TaskCompletionSource CreateOpenGate()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gate.SetResult();
            return gate;
        }
    }
}
