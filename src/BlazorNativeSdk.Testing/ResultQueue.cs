using System.Collections.Concurrent;

namespace BlazorNativeSdk.Testing;

/// <summary>
/// A queue of scripted results. Each call dequeues the next result; when the queue is empty the
/// <see cref="Default"/> result is returned.
/// </summary>
/// <typeparam name="T">The result value type.</typeparam>
public sealed class ResultQueue<T>
{
    private readonly ConcurrentQueue<NativeResult<T>> _results = new();

    /// <summary>Creates a queue whose default result is <paramref name="defaultResult"/>.</summary>
    public ResultQueue(NativeResult<T> defaultResult) => Default = defaultResult;

    /// <summary>The result returned when no scripted result is queued.</summary>
    public NativeResult<T> Default { get; set; }

    /// <summary>The number of queued results.</summary>
    public int Count => _results.Count;

    /// <summary>Queues a result.</summary>
    public ResultQueue<T> Enqueue(NativeResult<T> result)
    {
        _results.Enqueue(result);
        return this;
    }

    /// <summary>Queues a successful result.</summary>
    public ResultQueue<T> EnqueueSuccess(T value) => Enqueue(value);

    /// <summary>Queues a failure.</summary>
    public ResultQueue<T> EnqueueFailure(NativeError error) => Enqueue(error);

    /// <summary>Dequeues the next result.</summary>
    public NativeResult<T> Next() => _results.TryDequeue(out var result) ? result : Default;
}

/// <summary>A scripted queue for operations that return a non-generic <see cref="NativeResult"/>.</summary>
public sealed class ResultQueue
{
    private readonly ConcurrentQueue<NativeResult> _results = new();

    /// <summary>The result returned when no scripted result is queued. Defaults to success.</summary>
    public NativeResult Default { get; set; } = NativeResult.Ok;

    /// <summary>Queues a result.</summary>
    public ResultQueue Enqueue(NativeResult result)
    {
        _results.Enqueue(result);
        return this;
    }

    /// <summary>Queues a failure.</summary>
    public ResultQueue EnqueueFailure(NativeError error) => Enqueue(error);

    /// <summary>Dequeues the next result.</summary>
    public NativeResult Next() => _results.TryDequeue(out var result) ? result : Default;
}
