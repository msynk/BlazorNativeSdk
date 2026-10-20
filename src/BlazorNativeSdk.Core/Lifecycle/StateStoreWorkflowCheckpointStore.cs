using System.Text.Json;
using BlazorNativeSdk.Serialization;
using BlazorNativeSdk.Storage;

namespace BlazorNativeSdk.Lifecycle;

/// <summary><see cref="IWorkflowCheckpointStore"/> backed by <see cref="INativeStateStore"/>.</summary>
public sealed class StateStoreWorkflowCheckpointStore : IWorkflowCheckpointStore
{
    private const string Prefix = "bns.workflow.";
    private readonly INativeStateStore _store;
    private readonly TimeProvider _time;

    /// <summary>Creates the store.</summary>
    public StateStoreWorkflowCheckpointStore(INativeStateStore store, TimeProvider? timeProvider = null)
    {
        _store = store;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.WorkflowCheckpoints;

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_store is InMemoryStateStore
            ? CapabilityStatus.Unavailable(Capability, "storage.not-durable", "No durable state store is registered; checkpoints are lost when the app exits.", "core")
            : CapabilityStatus.Available(Capability, "core", "durable"));

    /// <inheritdoc />
    public ValueTask SaveAsync(WorkflowCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.WorkflowId);
        var json = JsonSerializer.Serialize(checkpoint, CoreJsonContext.Default.WorkflowCheckpoint);
        return _store.SetAsync(Prefix + checkpoint.WorkflowId, json, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<WorkflowCheckpoint?> GetAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        var checkpoint = await ReadAsync(Prefix + workflowId, cancellationToken).ConfigureAwait(false);
        if (checkpoint is not null && IsExpired(checkpoint))
        {
            await _store.RemoveAsync(Prefix + workflowId, cancellationToken).ConfigureAwait(false);
            return null;
        }

        return checkpoint;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<WorkflowCheckpoint>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<WorkflowCheckpoint>();
        foreach (var key in await _store.GetKeysAsync(Prefix, cancellationToken).ConfigureAwait(false))
        {
            var checkpoint = await ReadAsync(key, cancellationToken).ConfigureAwait(false);
            if (checkpoint is null)
            {
                continue;
            }

            if (IsExpired(checkpoint))
            {
                await _store.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
                continue;
            }

            result.Add(checkpoint);
        }

        result.Sort((a, b) => b.SavedAt.CompareTo(a.SavedAt));
        return result;
    }

    /// <inheritdoc />
    public async ValueTask CompleteAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        await _store.RemoveAsync(Prefix + workflowId, cancellationToken).ConfigureAwait(false);
    }

    private bool IsExpired(WorkflowCheckpoint checkpoint) => checkpoint.ExpiresAt is { } expires && expires <= _time.GetUtcNow();

    private async ValueTask<WorkflowCheckpoint?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        var json = await _store.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, CoreJsonContext.Default.WorkflowCheckpoint);
        }
        catch (JsonException)
        {
            // A corrupt checkpoint must not break the app; discard it.
            await _store.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            return null;
        }
    }
}
