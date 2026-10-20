using System.Text.Json;
using BlazorNativeSdk.Browser.Interop;
using BlazorNativeSdk.Devices;
using Microsoft.Extensions.Logging;

namespace BlazorNativeSdk.Browser.Services;

/// <summary>
/// Text clipboard through the async Clipboard API, with a <c>document.execCommand('copy')</c> fallback for writing.
/// Browsers do not report clipboard changes, so <see cref="ContentChanged"/> is never raised.
/// </summary>
internal sealed partial class BrowserClipboardService : IClipboardService
{
    private readonly BrowserInterop _interop;
    private readonly ILogger<BrowserClipboardService> _logger;

    public BrowserClipboardService(BrowserInterop interop, ILogger<BrowserClipboardService> logger)
    {
        _interop = interop;
        _logger = logger;
    }

    public CapabilityId Capability => NativeCapabilities.Clipboard;

    /// <summary>Never raised: browsers provide no clipboard change notification.</summary>
    public event EventHandler? ContentChanged
    {
        add { }
        remove { }
    }

    public async ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var probe = await _interop.ProbeAsync(Capability, "clipboard", cancellationToken).ConfigureAwait(false);
        if (!probe.IsSuccess)
        {
            return BrowserInteropExtensions.ToStatus(Capability, probe.Error);
        }

        var features = new List<string>();
        if (probe.Value.ClipboardRead)
        {
            features.Add(ClipboardFeatures.ReadText);
        }

        if (probe.Value.ClipboardWrite || probe.Value.ExecCommandCopy)
        {
            features.Add(ClipboardFeatures.WriteText);
        }

        if (features.Count == 0)
        {
            return probe.Value.SecureContext
                ? CapabilityStatus.Unsupported(Capability, "This browser exposes no clipboard API.", BrowserConstants.AdapterName)
                : CapabilityStatus.Unavailable(Capability, NativeErrorCodes.InsecureContext,
                    "The Clipboard API is only available in a secure context (HTTPS or localhost).", BrowserConstants.AdapterName);
        }

        return CapabilityStatus.Available(Capability, BrowserConstants.AdapterName, features);
    }

    public async ValueTask<NativeResult<string?>> GetTextAsync(CancellationToken cancellationToken = default)
    {
        // Reading can show a permission prompt (Chromium) or a paste button (Firefox, Safari), so it is not time-limited.
        var result = await _interop.InvokeInteractiveAsync(
            Capability, "clipboardRead", BrowserInteropExtensions.NewOperationId(), [], cancellationToken, NativePermissions.ClipboardRead).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            LogFailed(_logger, "read", result.Error.Code);
            return result.Error;
        }

        // The content is returned to the caller only; it is never logged.
        return result.Value.ValueKind == JsonValueKind.String && result.Value.GetString() is { Length: > 0 } text
            ? NativeResult.Success<string?>(text)
            : NativeResult.Success<string?>(null);
    }

    public async ValueTask<NativeResult> SetTextAsync(string? text, CancellationToken cancellationToken = default)
    {
        var result = await _interop.InvokeAsync(Capability, "clipboardWrite", [text ?? string.Empty], cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            LogFailed(_logger, "write", result.Error.Code);
        }

        return result.WithoutValue();
    }

    [LoggerMessage(EventId = 3601, Level = LogLevel.Debug, Message = "Browser clipboard {Operation} failed: {Code}.")]
    private static partial void LogFailed(ILogger logger, string operation, string code);
}
