namespace BlazorNativeSdk.Browser.Interop;

/// <summary>
/// Runs a start operation once. A successful start is remembered; a failed start (for example while prerendering, when
/// JavaScript is not available yet) is retried on the next call. Thread-safe.
/// </summary>
internal sealed class BrowserStartGate
{
    private readonly Func<CancellationToken, ValueTask<NativeResult>> _start;
    private readonly Lock _gate = new();
    private Task<NativeResult>? _task;

    public BrowserStartGate(Func<CancellationToken, ValueTask<NativeResult>> start) => _start = start;

    /// <summary><see langword="true"/> once the start operation succeeded.</summary>
    public bool IsStarted
    {
        get
        {
            lock (_gate)
            {
                return _task is { IsCompletedSuccessfully: true, Result.IsSuccess: true };
            }
        }
    }

    /// <summary>Starts once, or returns the result of the running or successful start.</summary>
    public Task<NativeResult> EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        Task<NativeResult> task;
        lock (_gate)
        {
            if (_task is null || (_task.IsCompleted && (!_task.IsCompletedSuccessfully || !_task.Result.IsSuccess)))
            {
                _task = RunAsync();
            }

            task = _task;
        }

        return task.WaitAsync(cancellationToken);
    }

    private async Task<NativeResult> RunAsync()
    {
        try
        {
            // Not linked to a caller token: one caller giving up must not fail the start for everyone else.
            return await _start(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return NativeError.FromException(NativeCapabilities.Lifecycle, ex);
        }
    }
}
