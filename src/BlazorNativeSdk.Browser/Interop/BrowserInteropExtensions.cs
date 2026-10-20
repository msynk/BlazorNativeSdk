using BlazorNativeSdk.Devices;
using Microsoft.JSInterop;

namespace BlazorNativeSdk.Browser.Interop;

/// <summary>Helpers shared by the browser capability services.</summary>
internal static class BrowserInteropExtensions
{
    /// <summary>Runs feature detection for one capability. Never prompts.</summary>
    public static async ValueTask<NativeResult<BrowserProbeDto>> ProbeAsync(this BrowserInterop interop, CapabilityId capability, string section, CancellationToken cancellationToken)
    {
        var result = await interop.InvokeAsync(capability, "probe", [section], cancellationToken).ConfigureAwait(false);
        return BrowserInterop.Read(capability, result, BrowserJsonContext.Default.BrowserProbeDto);
    }

    /// <summary>
    /// Converts a failed probe into a status. While prerendering the browser cannot be asked yet, which is reported as
    /// <see cref="CapabilityAvailability.Unknown"/> rather than as a failure.
    /// </summary>
    public static CapabilityStatus ToStatus(CapabilityId capability, NativeError error) =>
        BrowserInterop.IsHostNotReady(error)
            ? CapabilityStatus.Unknown(capability, error.Message, BrowserConstants.AdapterName)
            : CapabilityStatus.FromError(error, BrowserConstants.AdapterName) with { Capability = capability };

    /// <summary>
    /// Takes ownership of the Blobs produced by an interactive operation and wraps them as <see cref="BrowserNativeFile"/>.
    /// On failure or cancellation every file already taken is disposed and the remaining ones are released in the browser.
    /// </summary>
    public static async ValueTask<NativeResult<IReadOnlyList<NativeFile>>> TakeFilesAsync(
        this BrowserInterop interop,
        CapabilityId capability,
        string operationId,
        IReadOnlyList<BrowserFileDto> files,
        string fallbackName,
        CancellationToken cancellationToken)
    {
        var taken = new List<NativeFile>(files.Count);
        try
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reference = await interop.TakeFileAsync(file.Handle, cancellationToken).ConfigureAwait(false);
                taken.Add(new BrowserNativeFile(
                    interop,
                    reference,
                    string.IsNullOrWhiteSpace(file.Name) ? fallbackName : file.Name,
                    string.IsNullOrWhiteSpace(file.Type) ? null : file.Type,
                    file.Size,
                    BrowserMediaMapper.FromJsTimestamp(file.LastModified)));
            }

            return taken;
        }
        catch (Exception ex)
        {
            foreach (var file in taken)
            {
                await file.DisposeAsync().ConfigureAwait(false);
            }

            interop.Abort(operationId);
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException or ObjectDisposedException
                ? BrowserErrorMapper.MapInteropException(capability, ex)
                : NativeError.FromException(capability, ex);
        }
    }

    /// <summary>Creates a new operation id for <see cref="BrowserInterop.InvokeInteractiveAsync"/>.</summary>
    public static string NewOperationId() => Guid.NewGuid().ToString("N");
}
