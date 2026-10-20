namespace BlazorNativeSdk;

/// <summary>
/// Helpers for adapter authors that turn exceptions thrown by platform APIs into <see cref="NativeResult"/> values
/// while preserving standard .NET cancellation semantics.
/// </summary>
/// <remarks>
/// Cancellation contract used across BlazorNativeSdk: when the <em>caller's</em> <see cref="CancellationToken"/>
/// is cancelled, operations throw <see cref="OperationCanceledException"/> like any other .NET API. When the
/// <em>user</em> dismisses native UI, operations return <see cref="NativeResultStatus.Cancelled"/>.
/// </remarks>
public static class NativeOperation
{
    /// <summary>Runs an operation and converts unexpected exceptions into a failed result.</summary>
    public static async ValueTask<NativeResult<T>> RunAsync<T>(
        CapabilityId capability,
        Func<CancellationToken, ValueTask<NativeResult<T>>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return NativeError.FromException(capability, ex);
        }
    }

    /// <summary>Runs an operation and converts unexpected exceptions into a failed result.</summary>
    public static async ValueTask<NativeResult> RunAsync(
        CapabilityId capability,
        Func<CancellationToken, ValueTask<NativeResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return NativeError.FromException(capability, ex);
        }
    }

    /// <summary>Runs a value-producing operation and wraps its value in a successful result.</summary>
    public static async ValueTask<NativeResult<T>> TryAsync<T>(
        CapabilityId capability,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return NativeError.FromException(capability, ex);
        }
    }

    /// <summary>Runs an operation and returns success when it completes without throwing.</summary>
    public static async ValueTask<NativeResult> TryAsync(
        CapabilityId capability,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
            return NativeResult.Ok;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return NativeError.FromException(capability, ex);
        }
    }
}
