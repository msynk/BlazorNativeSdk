namespace BlazorNativeSdk;

/// <summary>
/// Durable storage for small JSON documents used by the SDK itself (window placement, work queues, workflow
/// checkpoints, transfer state). Not encrypted; never store secrets here.
/// </summary>
/// <remarks>
/// Implementations: file-based in hybrid/desktop hosts, <c>localStorage</c> in browsers and in-memory in tests.
/// Infrastructure failures throw, because callers cannot meaningfully continue without durable state.
/// </remarks>
public interface INativeStateStore
{
    /// <summary>Gets a stored document or <see langword="null"/>.</summary>
    ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Stores a document atomically.</summary>
    ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default);

    /// <summary>Removes a document.</summary>
    ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Lists stored keys that start with <paramref name="prefix"/>.</summary>
    ValueTask<IReadOnlyList<string>> GetKeysAsync(string prefix, CancellationToken cancellationToken = default);
}
