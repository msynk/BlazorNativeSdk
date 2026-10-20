using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace BlazorNativeSdk.Lifecycle;

/// <summary>
/// A persisted snapshot of an in-progress user workflow (a multi-step form, a checkout, a capture flow) that can be
/// resumed after the OS killed the app, for example while the camera or another app was in the foreground.
/// </summary>
public sealed record WorkflowCheckpoint
{
    /// <summary>The workflow id, unique per workflow instance (for example <c>checkout</c> or <c>inspection-17</c>).</summary>
    public required string WorkflowId { get; init; }

    /// <summary>The serialized workflow state (JSON).</summary>
    public required string StateJson { get; init; }

    /// <summary>The app route that resumes the workflow.</summary>
    public string? Route { get; init; }

    /// <summary>A user-facing description, such as "Unfinished inspection report".</summary>
    public string? Description { get; init; }

    /// <summary>An application-defined schema version so old checkpoints can be migrated or discarded.</summary>
    public int Version { get; init; } = 1;

    /// <summary>When the checkpoint was saved.</summary>
    public DateTimeOffset SavedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the checkpoint stops being offered for resumption.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Deserializes <see cref="StateJson"/> using source-generated metadata (trimming/AOT safe).</summary>
    public T? GetState<T>(JsonTypeInfo<T> typeInfo) => JsonSerializer.Deserialize(StateJson, typeInfo);

    /// <summary>Deserializes <see cref="StateJson"/> using reflection.</summary>
    [RequiresUnreferencedCode("Uses reflection-based JSON serialization. Use the JsonTypeInfo overload for trimmed/AOT apps.")]
    [RequiresDynamicCode("Uses reflection-based JSON serialization. Use the JsonTypeInfo overload for trimmed/AOT apps.")]
    public T? GetState<T>(JsonSerializerOptions? options = null) => JsonSerializer.Deserialize<T>(StateJson, options);
}

/// <summary>Stores checkpoints of interrupted workflows.</summary>
/// <remarks>Checkpoints are stored unencrypted in the app's state store. Do not put secrets in workflow state.</remarks>
public interface IWorkflowCheckpointStore : INativeCapability
{
    /// <summary>Saves or replaces a checkpoint.</summary>
    ValueTask SaveAsync(WorkflowCheckpoint checkpoint, CancellationToken cancellationToken = default);

    /// <summary>Gets a non-expired checkpoint.</summary>
    ValueTask<WorkflowCheckpoint?> GetAsync(string workflowId, CancellationToken cancellationToken = default);

    /// <summary>Lists non-expired checkpoints, most recent first. Expired checkpoints are deleted.</summary>
    ValueTask<IReadOnlyList<WorkflowCheckpoint>> GetPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes a checkpoint because the workflow completed or was abandoned.</summary>
    ValueTask CompleteAsync(string workflowId, CancellationToken cancellationToken = default);
}

/// <summary>Typed helpers for <see cref="IWorkflowCheckpointStore"/>.</summary>
public static class WorkflowCheckpointStoreExtensions
{
    /// <summary>Saves typed state using source-generated JSON metadata.</summary>
    public static ValueTask SaveStateAsync<T>(
        this IWorkflowCheckpointStore store,
        string workflowId,
        T state,
        JsonTypeInfo<T> typeInfo,
        string? route = null,
        TimeSpan? timeToLive = null,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var now = DateTimeOffset.UtcNow;
        return store.SaveAsync(new WorkflowCheckpoint
        {
            WorkflowId = workflowId,
            StateJson = JsonSerializer.Serialize(state, typeInfo),
            Route = route,
            Description = description,
            SavedAt = now,
            ExpiresAt = timeToLive is { } ttl ? now + ttl : null,
        }, cancellationToken);
    }

    /// <summary>Saves typed state using reflection-based JSON serialization.</summary>
    [RequiresUnreferencedCode("Uses reflection-based JSON serialization. Use the JsonTypeInfo overload for trimmed/AOT apps.")]
    [RequiresDynamicCode("Uses reflection-based JSON serialization. Use the JsonTypeInfo overload for trimmed/AOT apps.")]
    public static ValueTask SaveStateAsync<T>(
        this IWorkflowCheckpointStore store,
        string workflowId,
        T state,
        string? route = null,
        TimeSpan? timeToLive = null,
        string? description = null,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var now = DateTimeOffset.UtcNow;
        return store.SaveAsync(new WorkflowCheckpoint
        {
            WorkflowId = workflowId,
            StateJson = JsonSerializer.Serialize(state, options),
            Route = route,
            Description = description,
            SavedAt = now,
            ExpiresAt = timeToLive is { } ttl ? now + ttl : null,
        }, cancellationToken);
    }

    /// <summary>Loads typed state using source-generated JSON metadata.</summary>
    public static async ValueTask<T?> LoadStateAsync<T>(
        this IWorkflowCheckpointStore store,
        string workflowId,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var checkpoint = await store.GetAsync(workflowId, cancellationToken).ConfigureAwait(false);
        return checkpoint is null ? default : checkpoint.GetState(typeInfo);
    }
}
