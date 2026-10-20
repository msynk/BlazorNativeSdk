using BlazorNativeSdk.Browser.Interop;
using BlazorNativeSdk.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace BlazorNativeSdk.Browser.Services;

/// <summary>
/// Barcode scanning with the Shape Detection API (<c>BarcodeDetector</c>): a live camera overlay that runs detection about
/// ten times per second, and detection in images.
/// </summary>
internal sealed partial class BrowserBarcodeScanner : IBarcodeScanner
{
    internal const string UnsupportedReason =
        "The Shape Detection API (BarcodeDetector) is not available in this browser. It ships in Chromium-based browsers on " +
        "Android, ChromeOS and macOS; Firefox, Safari and Chromium on Windows and Linux do not provide it. Register a custom " +
        "IBarcodeScanner (for example one backed by a JavaScript or .NET decoder) to support those browsers.";

    private readonly BrowserInterop _interop;
    private readonly ILogger<BrowserBarcodeScanner> _logger;

    public BrowserBarcodeScanner(BrowserInterop interop, ILogger<BrowserBarcodeScanner> logger)
    {
        _interop = interop;
        _logger = logger;
    }

    public CapabilityId Capability => NativeCapabilities.Barcode;

    public async ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var probe = await _interop.ProbeAsync(Capability, "barcode", cancellationToken).ConfigureAwait(false);
        if (!probe.IsSuccess)
        {
            return BrowserInteropExtensions.ToStatus(Capability, probe.Error);
        }

        var dto = probe.Value;
        if (!dto.BarcodeDetector)
        {
            return CapabilityStatus.Unsupported(Capability, UnsupportedReason, BrowserConstants.AdapterName);
        }

        if (BarcodeFormatMapper.FromDetectorNames(dto.BarcodeFormats) == BarcodeFormats.None)
        {
            return CapabilityStatus.Unsupported(Capability, "BarcodeDetector exists but reports no supported barcode formats on this device.", BrowserConstants.AdapterName);
        }

        if (!dto.UserMedia)
        {
            return CapabilityStatus.Available(Capability, BrowserConstants.AdapterName, BarcodeFeatures.ImageDetection);
        }

        string[] features = [BarcodeFeatures.LiveScan, BarcodeFeatures.ImageDetection, BarcodeFeatures.Timeout, BarcodeFeatures.CustomPrompt, BarcodeFeatures.CameraSelection];
        return dto.CameraPermission switch
        {
            "denied" => CapabilityStatus.PermissionDenied(Capability, "Camera access was denied for this site; only image detection is possible.", BrowserConstants.AdapterName),
            "granted" when dto.VideoInputs == 0 => CapabilityStatus.Unavailable(Capability, NativeErrorCodes.HardwareUnavailable, "No camera is connected; only image detection is possible.", BrowserConstants.AdapterName),
            "granted" => CapabilityStatus.Available(Capability, BrowserConstants.AdapterName, features),
            _ => CapabilityStatus.RequiresPermission(Capability, BrowserConstants.AdapterName, features),
        };
    }

    public ValueTask<NativeResult<Barcode>> ScanAsync(BarcodeScanOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new BarcodeScanOptions();
        if (options.Timeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            return ValueTask.FromResult(NativeResult.Failure<Barcode>(
                NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "Timeout must be positive.")));
        }

        return NativeOperation.RunAsync<Barcode>(Capability, async ct =>
        {
            var formats = await ResolveFormatsAsync(options.Formats, ct).ConfigureAwait(false);
            if (!formats.IsSuccess)
            {
                return formats.Error;
            }

            var request = new BarcodeScanRequestDto
            {
                Formats = formats.Value,
                Facing = BrowserMediaMapper.ToFacingMode(options.Camera),
                TimeoutMs = options.Timeout?.TotalMilliseconds,
                Prompt = options.Prompt,
            };

            var result = await _interop.InvokeInteractiveAsync(
                Capability, "scanBarcode", BrowserInteropExtensions.NewOperationId(),
                [BrowserInterop.Serialize(request, BrowserJsonContext.Default.BarcodeScanRequestDto)], ct, NativePermissions.Camera).ConfigureAwait(false);
            var barcode = BrowserInterop.Read(Capability, result, BrowserJsonContext.Default.BarcodeDto);
            if (!barcode.IsSuccess)
            {
                LogFailed(_logger, "scan", barcode.Error.Code);
                return barcode.Error;
            }

            return BarcodeFormatMapper.ToBarcode(barcode.Value) is { } mapped
                ? mapped
                : NativeError.Failed(Capability, NativeErrorCodes.PlatformError, $"The browser reported an unknown barcode format '{barcode.Value.Format}'.");
        }, cancellationToken);
    }

    public ValueTask<NativeResult<IReadOnlyList<Barcode>>> DetectAsync(NativeFile image, BarcodeFormats formats = BarcodeFormats.All, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        return NativeOperation.RunAsync<IReadOnlyList<Barcode>>(Capability, async ct =>
        {
            var names = await ResolveFormatsAsync(formats, ct).ConfigureAwait(false);
            if (!names.IsSuccess)
            {
                return names.Error;
            }

            var request = BrowserInterop.Serialize(
                new BarcodeDetectRequestDto { Formats = names.Value, ContentType = image.ContentType },
                BrowserJsonContext.Default.BarcodeDetectRequestDto);

            NativeResult<System.Text.Json.JsonElement> result;
            if (image is BrowserNativeFile browserFile)
            {
                result = await _interop.InvokeInteractiveAsync(
                    Capability, "detectBarcodes", BrowserInteropExtensions.NewOperationId(), [request, browserFile.JSReference], ct).ConfigureAwait(false);
            }
            else
            {
                var stream = await image.OpenReadAsync(NativeFile.DefaultMaxReadSize, ct).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    using var streamReference = new DotNetStreamReference(stream, leaveOpen: true);
                    result = await _interop.InvokeInteractiveAsync(
                        Capability, "detectBarcodes", BrowserInteropExtensions.NewOperationId(), [request, streamReference], ct).ConfigureAwait(false);
                }
            }

            var barcodes = BrowserInterop.Read(Capability, result, BrowserJsonContext.Default.BarcodeDtoArray);
            if (!barcodes.IsSuccess)
            {
                LogFailed(_logger, "detect", barcodes.Error.Code);
                return barcodes.Error;
            }

            IReadOnlyList<Barcode> mapped = [.. barcodes.Value.Select(BarcodeFormatMapper.ToBarcode).OfType<Barcode>()];
            return NativeResult.Success(mapped);
        }, cancellationToken);
    }

    private async ValueTask<NativeResult<IReadOnlyList<string>>> ResolveFormatsAsync(BarcodeFormats formats, CancellationToken cancellationToken)
    {
        if (BarcodeFormatMapper.ToDetectorNames(formats).Count == 0)
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "At least one barcode format must be requested.");
        }

        var probe = await _interop.ProbeAsync(Capability, "barcode", cancellationToken).ConfigureAwait(false);
        if (!probe.IsSuccess)
        {
            return probe.Error;
        }

        if (!probe.Value.BarcodeDetector)
        {
            return NativeError.Unsupported(Capability, UnsupportedReason);
        }

        var names = BarcodeFormatMapper.Intersect(formats, probe.Value.BarcodeFormats);
        return names.Count == 0
            ? NativeError.Unsupported(Capability, $"None of the requested barcode formats ({formats}) is supported by this browser.")
            : NativeResult.Success(names);
    }

    [LoggerMessage(EventId = 3201, Level = LogLevel.Debug, Message = "Browser barcode {Operation} did not complete: {Code}.")]
    private static partial void LogFailed(ILogger logger, string operation, string code);
}
