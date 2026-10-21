using System.Collections.Concurrent;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;

namespace BlazorNativeSdk.Testing;

/// <summary>
/// An <see cref="IAppStorage"/> that maps every <see cref="AppDirectory"/> to a subfolder of <see cref="RootDirectory"/>
/// (a fresh temporary folder by default). Directories are created on demand, like the real adapters do.
/// </summary>
/// <remarks>
/// Use <see cref="SimulateUnsupportedDirectory"/> to mimic hosts without an addressable folder (for example
/// <see cref="AppDirectory.Downloads"/> on Android and iOS). Disposing the fake deletes the temporary root it created; a root
/// passed to the constructor is never deleted.
/// </remarks>
public sealed class FakeAppStorage : FakeCapability, IAppStorage, IDisposable
{
    private readonly ConcurrentDictionary<AppDirectory, string> _unsupported = new();
    private readonly bool _ownsRoot;

    /// <summary>Creates the fake.</summary>
    /// <param name="rootDirectory">The folder that holds the app directories. Defaults to a new folder under the system temp path.</param>
    public FakeAppStorage(string? rootDirectory = null)
        : base(NativeCapabilities.AppStorage)
    {
        _ownsRoot = rootDirectory is null;
        RootDirectory = Path.GetFullPath(rootDirectory ?? Path.Combine(Path.GetTempPath(), "bns-fake-app-storage", Guid.NewGuid().ToString("N")));
    }

    /// <summary>The folder that holds the app directories.</summary>
    public string RootDirectory { get; }

    /// <inheritdoc />
    public NativeResult<string> GetDirectory(AppDirectory directory)
    {
        Record(nameof(GetDirectory));
        if (StatusError() is { } error)
        {
            return error;
        }

        if (_unsupported.TryGetValue(directory, out var reason))
        {
            return NativeError.Unsupported(Capability, reason);
        }

        if (!Enum.IsDefined(directory))
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, $"Unknown app directory '{directory}'.");
        }

        try
        {
            var path = Path.Combine(RootDirectory, directory.ToString());
            Directory.CreateDirectory(path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return NativeError.FromException(Capability, ex);
        }
    }

    /// <summary>Makes <see cref="GetDirectory"/> return <see cref="NativeResultStatus.Unsupported"/> for one directory.</summary>
    public FakeAppStorage SimulateUnsupportedDirectory(AppDirectory directory, string? reason = null)
    {
        _unsupported[directory] = reason ?? $"The '{directory}' directory is not available on this (simulated) host.";
        return this;
    }

    /// <summary>Deletes the temporary root folder when the fake created it.</summary>
    public void Dispose()
    {
        if (!_ownsRoot)
        {
            return;
        }

        try
        {
            Directory.Delete(RootDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the folder lives under the system temp path.
        }
    }
}

/// <summary>
/// An in-memory <see cref="IWorkflowCheckpointStore"/> with the same semantics as the durable Core store: replace on save,
/// expiry against <see cref="TimeProvider"/> (expired checkpoints are deleted when read) and pending checkpoints listed
/// most recent first. Unlike Core's store over an in-memory state store, it reports itself as available.
/// </summary>
/// <remarks>Operations are not affected by <see cref="FakeCapability.Status"/>; it only changes what <see cref="FakeCapability.GetStatusAsync"/> reports.</remarks>
public sealed class InMemoryWorkflowCheckpointStore : FakeCapability, IWorkflowCheckpointStore
{
    private readonly ConcurrentDictionary<string, WorkflowCheckpoint> _checkpoints = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    /// <summary>Creates the store.</summary>
    /// <param name="timeProvider">The clock used to evaluate <see cref="WorkflowCheckpoint.ExpiresAt"/>; defaults to the system clock.</param>
    public InMemoryWorkflowCheckpointStore(TimeProvider? timeProvider = null)
        : base(NativeCapabilities.WorkflowCheckpoints, "durable")
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The stored checkpoints by workflow id, including expired ones that have not been read since they expired.</summary>
    public IReadOnlyDictionary<string, WorkflowCheckpoint> Checkpoints => _checkpoints;

    /// <inheritdoc />
    public ValueTask SaveAsync(WorkflowCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.WorkflowId);
        cancellationToken.ThrowIfCancellationRequested();
        Record(nameof(SaveAsync));
        _checkpoints[checkpoint.WorkflowId] = checkpoint;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<WorkflowCheckpoint?> GetAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        cancellationToken.ThrowIfCancellationRequested();
        Record(nameof(GetAsync));
        if (!_checkpoints.TryGetValue(workflowId, out var checkpoint))
        {
            return ValueTask.FromResult<WorkflowCheckpoint?>(null);
        }

        if (IsExpired(checkpoint))
        {
            _checkpoints.TryRemove(new KeyValuePair<string, WorkflowCheckpoint>(workflowId, checkpoint));
            return ValueTask.FromResult<WorkflowCheckpoint?>(null);
        }

        return ValueTask.FromResult<WorkflowCheckpoint?>(checkpoint);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<WorkflowCheckpoint>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Record(nameof(GetPendingAsync));
        var pending = new List<WorkflowCheckpoint>();
        foreach (var entry in _checkpoints)
        {
            if (IsExpired(entry.Value))
            {
                _checkpoints.TryRemove(entry);
            }
            else
            {
                pending.Add(entry.Value);
            }
        }

        pending.Sort(static (a, b) => b.SavedAt.CompareTo(a.SavedAt));
        return ValueTask.FromResult<IReadOnlyList<WorkflowCheckpoint>>(pending);
    }

    /// <inheritdoc />
    public ValueTask CompleteAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        cancellationToken.ThrowIfCancellationRequested();
        Record(nameof(CompleteAsync));
        _checkpoints.TryRemove(workflowId, out _);
        return ValueTask.CompletedTask;
    }

    private bool IsExpired(WorkflowCheckpoint checkpoint) => checkpoint.ExpiresAt is { } expires && expires <= _time.GetUtcNow();
}
