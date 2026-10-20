using BlazorNativeSdk.Browser.Interop;
using BlazorNativeSdk.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace BlazorNativeSdk.Browser.Services;

/// <summary>The Web Share API (<c>navigator.share</c>, <c>navigator.canShare</c>).</summary>
internal sealed partial class BrowserShareService : IShareService
{
    internal const string UnsupportedReason =
        "The Web Share API (navigator.share) is not available in this browser. Safari, Chromium-based browsers on Android, " +
        "ChromeOS and Windows provide it; Firefox on desktop and Chromium on Linux do not.";

    private readonly BrowserInterop _interop;
    private readonly ILogger<BrowserShareService> _logger;

    public BrowserShareService(BrowserInterop interop, ILogger<BrowserShareService> logger)
    {
        _interop = interop;
        _logger = logger;
    }

    public CapabilityId Capability => NativeCapabilities.Share;

    public async ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var probe = await _interop.ProbeAsync(Capability, "share", cancellationToken).ConfigureAwait(false);
        if (!probe.IsSuccess)
        {
            return BrowserInteropExtensions.ToStatus(Capability, probe.Error);
        }

        if (!probe.Value.SecureContext)
        {
            return CapabilityStatus.Unavailable(Capability, NativeErrorCodes.InsecureContext,
                "The Web Share API is only available in a secure context (HTTPS or localhost).", BrowserConstants.AdapterName);
        }

        if (!probe.Value.Share)
        {
            return CapabilityStatus.Unsupported(Capability, UnsupportedReason, BrowserConstants.AdapterName);
        }

        var features = new List<string> { ShareFeatures.Text, ShareFeatures.Uri, ShareFeatures.CompletionReporting };
        if (probe.Value.CanShareFiles)
        {
            features.Add(ShareFeatures.Files);
        }

        return CapabilityStatus.Available(Capability, BrowserConstants.AdapterName, features);
    }

    public ValueTask<NativeResult> ShareAsync(ShareRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.Text) && request.Uri is null && request.Files.Count == 0)
        {
            return ValueTask.FromResult(NativeResult.Failure(NativeError.Failed(
                Capability, NativeErrorCodes.InvalidArgument, "A share request needs text, a link or at least one file.")));
        }

        if (request.Uri is { IsAbsoluteUri: false })
        {
            return ValueTask.FromResult(NativeResult.Failure(NativeError.Failed(
                Capability, NativeErrorCodes.InvalidArgument, "Shared links must be absolute URLs.")));
        }

        return NativeOperation.RunAsync(Capability, async ct =>
        {
            var arguments = new List<object?>(request.Files.Count + 1);
            var files = new List<ShareFileDto>(request.Files.Count);
            var owned = new List<IAsyncDisposable>();
            try
            {
                // Placeholder for the request JSON, which needs the file list built below.
                arguments.Add(null);
                foreach (var file in request.Files)
                {
                    if (file is BrowserNativeFile browserFile)
                    {
                        files.Add(new ShareFileDto { Kind = "blob", Name = file.Name, Type = file.ContentType });
                        arguments.Add(browserFile.JSReference);
                    }
                    else
                    {
                        var stream = await file.OpenReadAsync(NativeFile.DefaultMaxReadSize, ct).ConfigureAwait(false);
                        owned.Add(stream);
                        var reference = new DotNetStreamReference(stream, leaveOpen: true);
                        owned.Add(new DisposableAdapter(reference));
                        files.Add(new ShareFileDto { Kind = "stream", Name = file.Name, Type = file.ContentType });
                        arguments.Add(reference);
                    }
                }

                arguments[0] = BrowserInterop.Serialize(new ShareRequestDto
                {
                    Title = request.Title,
                    Text = request.Text,
                    Url = request.Uri?.AbsoluteUri,
                    Files = files,
                }, BrowserJsonContext.Default.ShareRequestDto);

                var result = await _interop.InvokeInteractiveAsync(
                    Capability, "share", BrowserInteropExtensions.NewOperationId(), [.. arguments], ct).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    LogFailed(_logger, result.Error.Code);
                }

                return result.WithoutValue();
            }
            finally
            {
                foreach (var disposable in owned)
                {
                    await disposable.DisposeAsync().ConfigureAwait(false);
                }
            }
        }, cancellationToken);
    }

    [LoggerMessage(EventId = 3501, Level = LogLevel.Debug, Message = "Browser share did not complete: {Code}.")]
    private static partial void LogFailed(ILogger logger, string code);

    private sealed class DisposableAdapter(IDisposable inner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            inner.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
