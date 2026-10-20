namespace BlazorNativeSdk;

/// <summary>
/// Exception form of a <see cref="NativeError"/>. Thrown by <see cref="NativeResult{T}.GetValueOrThrow"/>
/// and <see cref="NativeResult.ThrowIfFailed"/>; adapters may also throw it internally to short-circuit
/// with a specific error.
/// </summary>
public sealed class NativeCapabilityException : Exception
{
    /// <summary>Creates a new exception for the given error.</summary>
    public NativeCapabilityException(NativeError error)
        : base(error?.ToString(), error?.Exception)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    /// <summary>The error that describes the failure.</summary>
    public NativeError Error { get; }

    /// <summary>Shortcut for <see cref="NativeError.Status"/>.</summary>
    public NativeResultStatus Status => Error.Status;
}
