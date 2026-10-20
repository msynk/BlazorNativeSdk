using Microsoft.JSInterop;

namespace BlazorNativeSdk.Browser.Interop;

/// <summary>
/// Maps errors reported by the browser (DOMException names) and by the JS interop layer to <see cref="NativeError"/> values.
/// The mapping is a pure function of its inputs so it can be unit tested.
/// </summary>
internal static class BrowserErrorMapper
{
    /// <summary>Raised by the module when an API needs a secure context (HTTPS or localhost).</summary>
    public const string InsecureContextError = "InsecureContextError";

    /// <summary>Raised by the module when an API needs transient user activation.</summary>
    public const string UserGestureRequiredError = "UserGestureRequiredError";

    /// <summary>The key of the DOMException name in <see cref="NativeError.Details"/>.</summary>
    public const string DomExceptionDetail = "dom-exception";

    /// <summary>Maps a browser error to a <see cref="NativeError"/>.</summary>
    /// <param name="capability">The capability that failed.</param>
    /// <param name="error">The error reported by JavaScript.</param>
    /// <param name="permission">
    /// The permission the operation needed, when any. Media operations pass <see cref="NativePermissions.Camera"/> or
    /// <see cref="NativePermissions.Microphone"/>, which also makes <c>NotFoundError</c> mean "no such device".
    /// </param>
    public static NativeError Map(CapabilityId capability, BrowserJsError error, NativePermission? permission = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        var message = string.IsNullOrWhiteSpace(error.Message) ? error.Name : error.Message;
        var isMedia = permission is { } p && (p == NativePermissions.Camera || p == NativePermissions.Microphone);

        var mapped = error.Name switch
        {
            "AbortError" => NativeError.Cancelled(capability, message),

            "NotAllowedError" when error.UserActivation == false =>
                NativeError.Unavailable(capability, NativeErrorCodes.UserGestureRequired,
                    $"The browser requires a user gesture for this operation; call it directly from a click or key handler. ({message})"),
            "NotAllowedError" => PermissionDenied(capability, permission, ToPermissionStatus(error.PermissionState, PermissionStatus.Denied), message),

            UserGestureRequiredError => NativeError.Unavailable(capability, NativeErrorCodes.UserGestureRequired,
                $"The browser requires a user gesture for this operation; call it directly from a click or key handler. ({message})"),

            InsecureContextError => NativeError.Unavailable(capability, NativeErrorCodes.InsecureContext,
                $"This browser API is only available in a secure context (HTTPS or localhost). ({message})"),

            // Permissions policy, sandboxed or cross-origin frames: restricted by policy, not by the user.
            "SecurityError" => PermissionDenied(capability, permission, PermissionStatus.Restricted,
                $"The browser blocked the operation for security reasons (permissions policy, sandboxed or cross-origin frame). ({message})"),

            "NotFoundError" when isMedia => NativeError.Unavailable(capability, NativeErrorCodes.HardwareUnavailable,
                $"No {(permission == NativePermissions.Microphone ? "microphone" : "camera")} was found. ({message})"),
            "NotFoundError" => NativeError.Failed(capability, NativeErrorCodes.NotFound, message),

            "NotReadableError" => NativeError.Unavailable(capability, NativeErrorCodes.HardwareUnavailable,
                $"The device could not be read; it may be in use by another application or blocked by the operating system. ({message})"),

            "OverconstrainedError" => NativeError.Unavailable(capability, NativeErrorCodes.HardwareUnavailable,
                $"No device satisfies the requested constraints (camera facing or resolution). ({message})"),

            "NotSupportedError" => NativeError.Unsupported(capability, $"The browser does not support this operation. ({message})"),

            "InvalidStateError" => NativeError.Failed(capability, NativeErrorCodes.Conflict,
                $"The browser is in a state that does not allow this operation (for example another share or capture is in progress). ({message})"),

            "TypeError" or "DataError" => NativeError.Failed(capability, NativeErrorCodes.InvalidArgument, message),

            "QuotaExceededError" => NativeError.Failed(capability, NativeErrorCodes.TooLarge, message),

            "TimeoutError" => NativeError.Failed(capability, NativeErrorCodes.Timeout, message),

            "OperationError" => NativeError.Failed(capability, NativeErrorCodes.PlatformError, message),

            _ => NativeError.Failed(capability, NativeErrorCodes.PlatformError, $"{error.Name}: {message}"),
        };

        return mapped with { Details = new Dictionary<string, string>(StringComparer.Ordinal) { [DomExceptionDetail] = error.Name } };
    }

    /// <summary>
    /// Maps an exception thrown by the JS interop infrastructure (not by the browser API itself, which reports structured
    /// errors). Callers rethrow <see cref="OperationCanceledException"/> themselves when their own token was cancelled;
    /// any other cancellation is the interop timeout.
    /// </summary>
    public static NativeError MapInteropException(CapabilityId capability, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            JSDisconnectedException => NativeError.Unavailable(capability, NativeErrorCodes.HostNotReady,
                "The browser is disconnected (the Blazor circuit was closed), so the operation cannot run in the user's browser.") with { Exception = exception },

            // Thrown while prerendering or during static server rendering, when no browser is attached yet.
            InvalidOperationException => NativeError.Unavailable(capability, NativeErrorCodes.HostNotReady,
                "JavaScript interop is not available yet (the component is prerendering or statically rendered). " +
                "Call browser capabilities from OnAfterRenderAsync or from an event handler.") with { Exception = exception },

            OperationCanceledException or TimeoutException => NativeError.Failed(capability, NativeErrorCodes.Timeout,
                "The browser did not respond within the configured interop timeout.", exception),

            JSException => NativeError.Failed(capability, NativeErrorCodes.PlatformError, exception.Message, exception),

            _ => NativeError.FromException(capability, exception),
        };
    }

    /// <summary>Maps a Permissions API state or a <c>Notification.permission</c> value.</summary>
    public static PermissionStatus ToPermissionStatus(string? state, PermissionStatus fallback = PermissionStatus.Unknown) =>
        state switch
        {
            "granted" => PermissionStatus.Granted,
            "denied" => PermissionStatus.Denied,
            "prompt" or "default" => PermissionStatus.NotDetermined,
            _ => fallback,
        };

    private static NativeError PermissionDenied(CapabilityId capability, NativePermission? permission, PermissionStatus status, string message)
    {
        if (permission is { } p)
        {
            return NativeError.PermissionDenied(capability, p, status, message);
        }

        return new NativeError(
            NativeResultStatus.PermissionDenied,
            status == PermissionStatus.Restricted ? NativeErrorCodes.PermissionRestricted : NativeErrorCodes.PermissionDenied,
            message)
        {
            Capability = capability,
            PermissionStatus = status,
        };
    }
}
