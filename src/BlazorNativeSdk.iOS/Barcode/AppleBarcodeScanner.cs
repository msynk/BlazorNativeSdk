using System.Globalization;
using AVFoundation;
using BlazorNativeSdk.Devices;
using CoreGraphics;
using ImageIO;
using Microsoft.Extensions.Logging;
using Vision;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>
/// <see cref="IBarcodeScanner"/> for iOS and Mac Catalyst: live scanning with <c>AVCaptureMetadataOutput</c> and image detection
/// with Vision (<c>VNDetectBarcodesRequest</c>).
/// </summary>
/// <remarks>
/// <para>
/// Live scanning presents a full-screen camera view and needs the camera permission and <c>NSCameraUsageDescription</c>. On
/// iPhones with several rear cameras the virtual multi-camera device is used so the system can switch lenses for close-up
/// codes. VisionKit's <c>DataScannerViewController</c> is a Swift-only API without a .NET binding, so it is not used.
/// </para>
/// <para>
/// Live results carry no <see cref="Barcode.Bounds"/>; image detection reports bounds in pixels of the (EXIF-oriented)
/// image. <see cref="Barcode.RawBytes"/> is reported by image detection on iOS / Mac Catalyst 17 and later.
/// </para>
/// </remarks>
public sealed partial class AppleBarcodeScanner : IBarcodeScanner
{
    private const long MaxImageBytes = 64L * 1024 * 1024;
    private readonly ILogger<AppleBarcodeScanner> _logger;

    /// <summary>Creates the scanner.</summary>
    public AppleBarcodeScanner(ILogger<AppleBarcodeScanner> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.Barcode;

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var adapter = AppleHost.AdapterName;
        string[] imageOnly = [BarcodeFeatures.ImageDetection];
        string[] all = [BarcodeFeatures.LiveScan, BarcodeFeatures.ImageDetection, BarcodeFeatures.Timeout, BarcodeFeatures.CustomPrompt, BarcodeFeatures.CameraSelection];

        using var camera = AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Video);
        if (camera is null)
        {
            return ValueTask.FromResult(CapabilityStatus.Available(Capability, adapter, imageOnly) with
            {
                ReasonCode = NativeErrorCodes.HardwareUnavailable,
                Reason = "No camera is available; only detection in existing images is supported.",
            });
        }

        if (AppleInfoPlist.RequireUsageDescription(Capability, AppleInfoPlist.CameraUsage, "the camera") is { } missing)
        {
            return ValueTask.FromResult(CapabilityStatus.Available(Capability, adapter, imageOnly) with { ReasonCode = missing.Code, Reason = missing.Message });
        }

        return ValueTask.FromResult(AppleStatusMapping.FromCapture(AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Video)) switch
        {
            PermissionStatus.Granted => CapabilityStatus.Available(Capability, adapter, all),
            PermissionStatus.NotDetermined => CapabilityStatus.RequiresPermission(Capability, adapter, all),
            var denied => CapabilityStatus.PermissionDenied(Capability, $"Camera access is {denied}; only detection in existing images is supported.", adapter)
                .WithFeatures(imageOnly),
        });
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<Barcode>> ScanAsync(BarcodeScanOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new BarcodeScanOptions();
        return NativeOperation.RunAsync<Barcode>(Capability, async token =>
        {
            var formats = options.Formats == BarcodeFormats.None ? BarcodeFormats.All : options.Formats;
            var metadataTypes = AppleBarcodeMapping.ToMetadataTypes(formats);
            if (metadataTypes == AVMetadataObjectType.None)
            {
                return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, $"None of the requested formats ({formats}) can be scanned live on this OS version.");
            }

            if (options.Timeout is { } timeout && timeout <= TimeSpan.Zero)
            {
                return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "BarcodeScanOptions.Timeout must be positive.");
            }

            if (await AppleCaptureAccess.EnsureGrantedAsync(Capability, AVAuthorizationMediaType.Video, token).ConfigureAwait(false) is { } accessError)
            {
                return accessError;
            }

            var device = SelectCamera(options.Camera);
            if (device is null)
            {
                return NativeError.Unavailable(Capability, NativeErrorCodes.HardwareUnavailable, "No camera is available on this device.");
            }

            var scanner = await AppleUI.OnMainThreadAsync(async () =>
            {
                if (AppleUI.GetTopViewController() is not { } presenter)
                {
                    return null;
                }

                var controller = new AppleBarcodeScannerViewController(device, metadataTypes, formats, options.Prompt);
                await presenter.PresentViewControllerAsync(controller, true).ConfigureAwait(true);
                return controller;
            }).ConfigureAwait(false);

            if (scanner is null)
            {
                device.Dispose();
                return AppleUI.NoPresenter(Capability);
            }

            using var timeoutSource = options.Timeout is { } limit ? new CancellationTokenSource(limit) : null;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutSource?.Token ?? CancellationToken.None);
            try
            {
                var result = await scanner.Completion.WaitAsync(linked.Token).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    LogScanned(_logger, result.Value.Format);
                }

                return result;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && timeoutSource?.IsCancellationRequested == true)
            {
                return NativeError.Failed(Capability, NativeErrorCodes.Timeout, $"No barcode was detected within {options.Timeout}.");
            }
            finally
            {
                // Close and release the controller on the main thread: UIKit objects must not be deallocated elsewhere.
                await AppleUI.OnMainThreadAsync(async () =>
                {
                    await scanner.CloseAsync().ConfigureAwait(true);
                    scanner.Dispose();
                }).ConfigureAwait(false);
                device.Dispose();
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<IReadOnlyList<Barcode>>> DetectAsync(NativeFile image, BarcodeFormats formats = BarcodeFormats.All, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        return NativeOperation.RunAsync<IReadOnlyList<Barcode>>(Capability, async token =>
        {
            if (formats == BarcodeFormats.None)
            {
                formats = BarcodeFormats.All;
            }

            var bytes = await image.ReadAllBytesAsync(MaxImageBytes, token).ConfigureAwait(false);
            return await Task.Run(() => Detect(bytes, formats), token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private static AVCaptureDevice? SelectCamera(CameraFacing facing)
    {
        if (!OperatingSystem.IsMacCatalyst())
        {
            var position = facing == CameraFacing.Front ? AVCaptureDevicePosition.Front : AVCaptureDevicePosition.Back;
            AVCaptureDeviceType[] types = position == AVCaptureDevicePosition.Back
                ? [AVCaptureDeviceType.BuiltInTripleCamera, AVCaptureDeviceType.BuiltInDualWideCamera, AVCaptureDeviceType.BuiltInDualCamera, AVCaptureDeviceType.BuiltInWideAngleCamera]
                : [AVCaptureDeviceType.BuiltInWideAngleCamera];
            using var discovery = AVCaptureDeviceDiscoverySession.Create(types, AVMediaTypes.Video, position);
            if (discovery.Devices.FirstOrDefault() is { } device)
            {
                return device;
            }
        }

        return AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Video);
    }

    private NativeResult<IReadOnlyList<Barcode>> Detect(byte[] bytes, BarcodeFormats formats)
    {
        using var data = NSData.FromArray(bytes);
        using var source = CGImageSource.FromData(data);
        using var cgImage = source is { ImageCount: > 0 } ? source.CreateImage(0, new CGImageOptions()) : null;
        if (source is null || cgImage is null)
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "The file is not an image format that iOS can decode.");
        }

        var orientation = source.GetProperties(0, new CGImageOptions())?.Orientation is { } exif && Enum.IsDefined((CGImagePropertyOrientation)(int)exif)
            ? (CGImagePropertyOrientation)(int)exif
            : CGImagePropertyOrientation.Up;
        double width = cgImage.Width;
        double height = cgImage.Height;
        if (orientation >= CGImagePropertyOrientation.LeftMirrored)
        {
            // Orientations 5-8 rotate the image by 90 degrees: Vision reports coordinates in the upright image.
            (width, height) = (height, width);
        }

        using var request = new VNDetectBarcodesRequest(null);
        if (formats != BarcodeFormats.All)
        {
            var supported = request.GetSupportedSymbologies(out _) ?? [];
            var symbologies = AppleBarcodeMapping.ToVisionSymbologies(formats).Where(s => supported.Length == 0 || supported.Contains(s)).ToArray();
            if (symbologies.Length == 0)
            {
                return NativeResult.Success<IReadOnlyList<Barcode>>([]);
            }

            request.Symbologies = symbologies;
        }

        using var handler = new VNImageRequestHandler(cgImage, orientation, new VNImageOptions());
        if (!handler.Perform([request], out var error))
        {
            LogDetectionFailed(_logger, error?.Code ?? 0);
            return NativeError.Failed(Capability, NativeErrorCodes.PlatformError, error?.LocalizedDescription ?? "Vision could not analyze the image.") with
            {
                Details = error is null ? null : new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["domain"] = error.Domain,
                    ["code"] = ((long)error.Code).ToString(CultureInfo.InvariantCulture),
                },
            };
        }

        var barcodes = new List<Barcode>();
        foreach (var observation in request.Results ?? [])
        {
            if (AppleBarcodeMapping.Normalize(AppleBarcodeMapping.FromVision(observation.Symbology), observation.PayloadStringValue, formats) is not { } normalized)
            {
                continue;
            }

            var box = observation.BoundingBox;
            barcodes.Add(new Barcode
            {
                RawValue = normalized.Value,
                Format = normalized.Format,
                RawBytes = GetPayloadBytes(observation),
                Bounds = AppleBarcodeMapping.ToPixelBounds(box.X, box.Y, box.Width, box.Height, width, height),
            });
        }

        return barcodes;
    }

    private static ReadOnlyMemory<byte>? GetPayloadBytes(VNBarcodeObservation observation)
    {
        if ((OperatingSystem.IsIOSVersionAtLeast(17) || OperatingSystem.IsMacCatalystVersionAtLeast(17)) && observation.PayloadData is { Length: > 0 } payload)
        {
            return payload.ToArray();
        }

        return null;
    }

    [LoggerMessage(EventId = 3401, Level = LogLevel.Debug, Message = "Scanned a {Format} barcode.")]
    private static partial void LogScanned(ILogger logger, BarcodeFormats format);

    [LoggerMessage(EventId = 3402, Level = LogLevel.Warning, Message = "Vision barcode detection failed with code {Code}.")]
    private static partial void LogDetectionFailed(ILogger logger, nint code);
}
