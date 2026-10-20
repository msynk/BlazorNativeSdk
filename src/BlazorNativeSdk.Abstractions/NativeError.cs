using System.Diagnostics.CodeAnalysis;

namespace BlazorNativeSdk;

/// <summary>
/// Describes why a native operation did not succeed. Errors are values: they flow through
/// <see cref="NativeResult"/> and <see cref="NativeResult{T}"/> and can be converted to an exception
/// with <see cref="ToException"/> when exception-style control flow is preferred.
/// </summary>
public sealed record NativeError
{
    private static readonly NativeError s_uninitialized =
        new(NativeResultStatus.Failed, NativeErrorCodes.Uninitialized, "The result was not initialized. A default(NativeResult<T>) value was used.");

    /// <summary>Creates a new error.</summary>
    /// <param name="status">The failure status. Must not be <see cref="NativeResultStatus.Success"/>.</param>
    /// <param name="code">A stable machine-readable code, see <see cref="NativeErrorCodes"/>.</param>
    /// <param name="message">A developer-facing explanation. Not localized; do not show it to end users verbatim.</param>
    public NativeError(NativeResultStatus status, string code, string message)
    {
        if (status == NativeResultStatus.Success)
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "An error cannot have the Success status.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Status = status;
        Code = code;
        Message = message ?? string.Empty;
    }

    /// <summary>The failure category.</summary>
    public NativeResultStatus Status { get; }

    /// <summary>A stable machine-readable code (see <see cref="NativeErrorCodes"/>).</summary>
    public string Code { get; }

    /// <summary>A developer-facing explanation of the failure.</summary>
    public string Message { get; }

    /// <summary>The capability that produced the error, when known.</summary>
    public CapabilityId? Capability { get; init; }

    /// <summary>The permission involved when <see cref="Status"/> is <see cref="NativeResultStatus.PermissionDenied"/>.</summary>
    public NativePermission? Permission { get; init; }

    /// <summary>The permission state observed when the operation failed.</summary>
    public PermissionStatus? PermissionStatus { get; init; }

    /// <summary>The underlying exception, when the error was produced from one.</summary>
    public Exception? Exception { get; init; }

    /// <summary>Optional adapter-specific diagnostic details (for example a native error domain and code).</summary>
    public IReadOnlyDictionary<string, string>? Details { get; init; }

    internal static NativeError Uninitialized => s_uninitialized;

    /// <summary>Creates an <see cref="NativeResultStatus.Unsupported"/> error.</summary>
    public static NativeError Unsupported(CapabilityId capability, string? reason = null, string code = NativeErrorCodes.NotSupported) =>
        new(NativeResultStatus.Unsupported, code, reason ?? $"'{capability}' is not supported on this platform or host.")
        {
            Capability = capability,
        };

    /// <summary>Creates an error that reports that no adapter has been registered for a capability.</summary>
    public static NativeError NoAdapter(CapabilityId capability, string? hostDescription = null) =>
        new(NativeResultStatus.Unsupported, NativeErrorCodes.NoAdapter,
            $"No adapter provides '{capability}'{(hostDescription is null ? string.Empty : $" on {hostDescription}")}. " +
            "Register an adapter package (for example sdk.AddBrowser(), sdk.AddAndroid(), sdk.AddApple(), sdk.AddWindows(), " +
            "sdk.AddDesktop(), sdk.AddWpf(), or builder.UseBlazorNativeSdk() in a .NET MAUI app) " +
            "or a custom implementation of the capability interface.")
        {
            Capability = capability,
        };

    /// <summary>Creates a <see cref="NativeResultStatus.PermissionDenied"/> error.</summary>
    public static NativeError PermissionDenied(CapabilityId capability, NativePermission permission, PermissionStatus status, string? message = null) =>
        new(NativeResultStatus.PermissionDenied,
            status == BlazorNativeSdk.PermissionStatus.Restricted ? NativeErrorCodes.PermissionRestricted : NativeErrorCodes.PermissionDenied,
            message ?? $"Permission '{permission}' is {status}.")
        {
            Capability = capability,
            Permission = permission,
            PermissionStatus = status,
        };

    /// <summary>Creates a <see cref="NativeResultStatus.Cancelled"/> error.</summary>
    public static NativeError Cancelled(CapabilityId capability, string? message = null) =>
        new(NativeResultStatus.Cancelled, NativeErrorCodes.Cancelled, message ?? "The user cancelled the operation.")
        {
            Capability = capability,
        };

    /// <summary>Creates an <see cref="NativeResultStatus.Unavailable"/> error.</summary>
    public static NativeError Unavailable(CapabilityId capability, string code, string message) =>
        new(NativeResultStatus.Unavailable, code, message)
        {
            Capability = capability,
        };

    /// <summary>Creates a <see cref="NativeResultStatus.Failed"/> error.</summary>
    public static NativeError Failed(CapabilityId capability, string code, string message, Exception? exception = null) =>
        new(NativeResultStatus.Failed, code, message)
        {
            Capability = capability,
            Exception = exception,
        };

    /// <summary>
    /// Maps an exception to an error using conventional .NET semantics:
    /// <see cref="NativeCapabilityException"/> carries its own error, <see cref="PlatformNotSupportedException"/>
    /// and <see cref="NotSupportedException"/> map to <see cref="NativeResultStatus.Unsupported"/>,
    /// <see cref="UnauthorizedAccessException"/> to <see cref="NativeResultStatus.PermissionDenied"/>,
    /// <see cref="OperationCanceledException"/> to <see cref="NativeResultStatus.Cancelled"/>,
    /// <see cref="TimeoutException"/> to a timeout failure and anything else to <see cref="NativeResultStatus.Failed"/>.
    /// </summary>
    public static NativeError FromException(CapabilityId capability, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            NativeCapabilityException nce => nce.Error,
            PlatformNotSupportedException or NotSupportedException => new NativeError(NativeResultStatus.Unsupported, NativeErrorCodes.NotSupported, exception.Message)
            {
                Capability = capability,
                Exception = exception,
            },
            UnauthorizedAccessException => new NativeError(NativeResultStatus.PermissionDenied, NativeErrorCodes.PermissionDenied, exception.Message)
            {
                Capability = capability,
                Exception = exception,
                PermissionStatus = BlazorNativeSdk.PermissionStatus.Denied,
            },
            OperationCanceledException => new NativeError(NativeResultStatus.Cancelled, NativeErrorCodes.Cancelled, exception.Message)
            {
                Capability = capability,
                Exception = exception,
            },
            TimeoutException => Failed(capability, NativeErrorCodes.Timeout, exception.Message, exception),
            _ => Failed(capability, NativeErrorCodes.PlatformError, exception.Message, exception),
        };
    }

    /// <summary>Converts the error into a <see cref="NativeCapabilityException"/>.</summary>
    public NativeCapabilityException ToException() => new(this);

    /// <summary>Throws this error as a <see cref="NativeCapabilityException"/>.</summary>
    [DoesNotReturn]
    public void Throw() => throw ToException();

    /// <inheritdoc />
    public override string ToString() =>
        Capability is { } capability
            ? $"{Status} [{Code}] ({capability}): {Message}"
            : $"{Status} [{Code}]: {Message}";
}
