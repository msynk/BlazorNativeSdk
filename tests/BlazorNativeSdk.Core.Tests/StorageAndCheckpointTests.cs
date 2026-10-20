using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.Lifecycle;
using BlazorNativeSdk.Storage;
using BlazorNativeSdk.Testing;
using Microsoft.Extensions.Time.Testing;

namespace BlazorNativeSdk.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bns-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public Task In_memory_store_satisfies_the_contract() => CapabilityContracts.VerifyStateStoreAsync(new InMemoryStateStore(), Ct);

    [Fact]
    public Task File_store_satisfies_the_contract() => CapabilityContracts.VerifyStateStoreAsync(new FileStateStore(_directory), Ct);

    [Fact]
    public async Task File_store_persists_across_instances_and_leaves_no_temp_files()
    {
        await new FileStateStore(_directory).SetAsync("a/b", "value", Ct);

        Assert.Equal("value", await new FileStateStore(_directory).GetAsync("a/b", Ct));
        Assert.DoesNotContain(Directory.EnumerateFiles(_directory), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task File_store_handles_concurrent_writers()
    {
        var store = new FileStateStore(_directory);
        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => store.SetAsync("k" + (i % 5), "v" + i, Ct).AsTask()));

        Assert.Equal(5, (await store.GetKeysAsync("k", Ct)).Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

public sealed class WorkflowCheckpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Saves_lists_and_completes_checkpoints()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var store = new StateStoreWorkflowCheckpointStore(new FileBackedFake(), time);

        await store.SaveAsync(new WorkflowCheckpoint { WorkflowId = "a", StateJson = "{\"step\":1}", Route = "/a", SavedAt = time.GetUtcNow() }, Ct);
        time.Advance(TimeSpan.FromMinutes(1));
        await store.SaveAsync(new WorkflowCheckpoint { WorkflowId = "b", StateJson = "{}", SavedAt = time.GetUtcNow(), ExpiresAt = time.GetUtcNow().AddMinutes(5) }, Ct);

        var pending = await store.GetPendingAsync(Ct);
        Assert.Equal(["b", "a"], pending.Select(p => p.WorkflowId));
        Assert.Equal("/a", (await store.GetAsync("a", Ct))!.Route);

        await store.CompleteAsync("a", Ct);
        Assert.Null(await store.GetAsync("a", Ct));
    }

    [Fact]
    public async Task Expired_checkpoints_are_removed()
    {
        var time = new FakeTimeProvider();
        var backing = new InMemoryStateStore();
        var store = new StateStoreWorkflowCheckpointStore(backing, time);
        await store.SaveAsync(new WorkflowCheckpoint { WorkflowId = "x", StateJson = "{}", ExpiresAt = time.GetUtcNow().AddSeconds(10) }, Ct);

        time.Advance(TimeSpan.FromSeconds(11));

        Assert.Empty(await store.GetPendingAsync(Ct));
        Assert.Empty(await backing.GetKeysAsync("", Ct));
    }

    [Fact]
    public async Task Corrupt_checkpoints_are_discarded()
    {
        var backing = new InMemoryStateStore();
        await backing.SetAsync("bns.workflow.bad", "{not json", Ct);
        var store = new StateStoreWorkflowCheckpointStore(backing);

        Assert.Empty(await store.GetPendingAsync(Ct));
        Assert.Null(await backing.GetAsync("bns.workflow.bad", Ct));
    }

    [Fact]
    public async Task Typed_state_round_trips_with_source_generated_metadata()
    {
        var store = new StateStoreWorkflowCheckpointStore(new InMemoryStateStore());

        await store.SaveStateAsync("checkout", new CheckoutState("SKU-1", 3), TestJsonContext.Default.CheckoutState, route: "/checkout", cancellationToken: Ct);

        var state = await store.LoadStateAsync("checkout", TestJsonContext.Default.CheckoutState, Ct);
        Assert.Equal(new CheckoutState("SKU-1", 3), state);
    }

    [Fact]
    public async Task Status_reports_durability()
    {
        Assert.Equal(CapabilityAvailability.Unavailable, (await new StateStoreWorkflowCheckpointStore(new InMemoryStateStore()).GetStatusAsync(Ct)).Availability);
        Assert.True((await new StateStoreWorkflowCheckpointStore(new FileBackedFake()).GetStatusAsync(Ct)).IsAvailable);
    }

    /// <summary>A durable-looking store (anything other than <see cref="InMemoryStateStore"/>).</summary>
    private sealed class FileBackedFake : INativeStateStore
    {
        private readonly InMemoryStateStore _inner = new();

        public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default) => _inner.GetAsync(key, cancellationToken);

        public ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default) => _inner.SetAsync(key, value, cancellationToken);

        public ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken = default) => _inner.RemoveAsync(key, cancellationToken);

        public ValueTask<IReadOnlyList<string>> GetKeysAsync(string prefix, CancellationToken cancellationToken = default) => _inner.GetKeysAsync(prefix, cancellationToken);
    }
}

public sealed record CheckoutState(string Sku, int Quantity);

[System.Text.Json.Serialization.JsonSerializable(typeof(CheckoutState))]
internal sealed partial class TestJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

public sealed class WindowPlacementGuardTests
{
    private static readonly DisplayInfo Primary = new("1", "Primary", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), 1, true);
    private static readonly DisplayInfo Right = new("2", "Right", new(1920, 0, 1280, 1024), new(1920, 0, 1280, 1024), 1, false);

    [Fact]
    public void Visible_placements_are_unchanged()
    {
        var placement = new WindowPlacement { Bounds = new(100, 100, 800, 600) };
        Assert.Same(placement, WindowPlacementGuard.EnsureVisible(placement, [Primary, Right]));
    }

    [Fact]
    public void Windows_on_a_disconnected_monitor_move_to_the_primary_display()
    {
        var placement = new WindowPlacement { Bounds = new(2200, 100, 800, 600), State = NativeWindowState.Maximized };

        var fixedPlacement = WindowPlacementGuard.EnsureVisible(placement, [Primary]);

        Assert.Equal(NativeWindowState.Maximized, fixedPlacement.State);
        Assert.Equal("1", fixedPlacement.DisplayId);
        Assert.InRange(fixedPlacement.Bounds.X, 0, 1920 - 800);
        Assert.InRange(fixedPlacement.Bounds.Y, 0, 1040 - 600);
    }

    [Fact]
    public void Oversized_windows_are_shrunk_to_the_work_area()
    {
        var fixedPlacement = WindowPlacementGuard.EnsureVisible(new WindowPlacement { Bounds = new(-5000, -5000, 4000, 3000) }, [Primary]);

        Assert.Equal(new WindowBounds(0, 0, 1920, 1040), fixedPlacement.Bounds);
    }

    [Fact]
    public void Windows_with_an_unreachable_title_bar_are_moved()
    {
        var fixedPlacement = WindowPlacementGuard.EnsureVisible(new WindowPlacement { Bounds = new(100, -500, 800, 1200) }, [Primary]);

        Assert.True(fixedPlacement.Bounds.Y >= 0);
    }
}
