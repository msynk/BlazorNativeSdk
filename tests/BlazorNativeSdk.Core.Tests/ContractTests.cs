using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;
using BlazorNativeSdk.Storage;
using BlazorNativeSdk.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace BlazorNativeSdk.Tests;

/// <summary>Runs the capability contracts against the fakes and against Core's real implementations.</summary>
public sealed class ContractTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bns-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _appDirectories = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Fakes_satisfy_the_contracts()
    {
        using var appStorage = new FakeAppStorage();
        await using var ipc = new FakeIpcService();

        await CapabilityContracts.VerifySecureStorageAsync(new InMemorySecureStorage(), Ct);
        await CapabilityContracts.VerifyClipboardAsync(new FakeClipboardService(), Ct);
        await CapabilityContracts.VerifyAppStorageAsync(appStorage, Ct);
        await CapabilityContracts.VerifyWorkflowCheckpointStoreAsync(new InMemoryWorkflowCheckpointStore(), cancellationToken: Ct);
        await CapabilityContracts.VerifyIpcAsync(ipc, Ct);
        await CapabilityContracts.VerifyStateStoreAsync(new InMemoryStateStore(), Ct);
    }

    [Fact]
    public async Task Contracts_accept_unsupported_user_folders_and_unsupported_capabilities()
    {
        using var mobileLike = new FakeAppStorage().SimulateUnsupportedDirectory(AppDirectory.Downloads);
        await CapabilityContracts.VerifyAppStorageAsync(mobileLike, Ct);

        using var unsupported = new FakeAppStorage();
        unsupported.SimulateUnsupported();
        await CapabilityContracts.VerifyAppStorageAsync(unsupported, Ct);

        var ipc = new FakeIpcService();
        ipc.SimulateUnsupported();
        await CapabilityContracts.VerifyIpcAsync(ipc, Ct);
    }

    [Fact]
    public async Task Contracts_reject_implementations_that_break_them()
    {
        using var privateUnsupported = new FakeAppStorage().SimulateUnsupportedDirectory(AppDirectory.AppData);
        var appData = await Assert.ThrowsAsync<ContractViolationException>(() => CapabilityContracts.VerifyAppStorageAsync(privateUnsupported, Ct));
        Assert.Contains("AppData", appData.Message, StringComparison.Ordinal);

        var ignoresExpiry = await Assert.ThrowsAsync<ContractViolationException>(() =>
            CapabilityContracts.VerifyWorkflowCheckpointStoreAsync(new IgnoresExpiryStore(), cancellationToken: Ct));
        Assert.Contains("expired", ignoresExpiry.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Physical_app_storage_satisfies_the_contract()
    {
        var storage = new PhysicalAppStorage(Options.Create(new NativeSdkOptions { AppId = "bns-contract-" + Guid.NewGuid().ToString("N")[..12] }));
        foreach (var directory in new[] { AppDirectory.AppData, AppDirectory.Cache, AppDirectory.Temp })
        {
            _appDirectories.Add(storage.GetDirectory(directory).GetValueOrThrow());
        }

        await CapabilityContracts.VerifyAppStorageAsync(storage, Ct);
        await CapabilityContracts.VerifyStatusAsync(storage, NativeCapabilities.AppStorage, Ct);
    }

    [Fact]
    public async Task Checkpoint_store_satisfies_the_contract_over_the_file_store()
    {
        var store = new StateStoreWorkflowCheckpointStore(new FileStateStore(_directory));

        await CapabilityContracts.VerifyWorkflowCheckpointStoreAsync(store, cancellationToken: Ct);
        await CapabilityContracts.VerifyStatusAsync(store, NativeCapabilities.WorkflowCheckpoints, Ct);
        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public async Task Checkpoint_stores_satisfy_the_contract_with_a_fake_clock()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero));

        await CapabilityContracts.VerifyWorkflowCheckpointStoreAsync(new StateStoreWorkflowCheckpointStore(new InMemoryStateStore(), time), time, Ct);
        await CapabilityContracts.VerifyWorkflowCheckpointStoreAsync(new InMemoryWorkflowCheckpointStore(time), time, Ct);
    }

    [Fact]
    public async Task Every_fake_and_every_fallback_reports_an_honest_status()
    {
        foreach (var configure in new Action<INativeSdkBuilder>?[] { null, static sdk => sdk.AddFakes() })
        {
            await using var provider = new ServiceCollection().AddBlazorNativeSdk(configure).BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var registry = scope.ServiceProvider.GetRequiredService<ICapabilityRegistry>();

            Assert.Equal(NativeCapabilities.All.Count, registry.Capabilities.Count);
            foreach (var capability in registry.Capabilities)
            {
                await CapabilityContracts.VerifyStatusAsync(registry.GetProvider(capability)!, capability, Ct);
            }
        }
    }

    public void Dispose()
    {
        foreach (var directory in _appDirectories.Append(_directory))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    /// <summary>A broken store that keeps returning expired checkpoints.</summary>
    private sealed class IgnoresExpiryStore : FakeCapability, IWorkflowCheckpointStore
    {
        private readonly Dictionary<string, WorkflowCheckpoint> _checkpoints = [];

        public IgnoresExpiryStore()
            : base(NativeCapabilities.WorkflowCheckpoints)
        {
        }

        public ValueTask SaveAsync(WorkflowCheckpoint checkpoint, CancellationToken cancellationToken = default)
        {
            _checkpoints[checkpoint.WorkflowId] = checkpoint;
            return ValueTask.CompletedTask;
        }

        public ValueTask<WorkflowCheckpoint?> GetAsync(string workflowId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_checkpoints.GetValueOrDefault(workflowId));

        public ValueTask<IReadOnlyList<WorkflowCheckpoint>> GetPendingAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<WorkflowCheckpoint>>([.. _checkpoints.Values.OrderByDescending(c => c.SavedAt)]);

        public ValueTask CompleteAsync(string workflowId, CancellationToken cancellationToken = default)
        {
            _checkpoints.Remove(workflowId);
            return ValueTask.CompletedTask;
        }
    }
}
