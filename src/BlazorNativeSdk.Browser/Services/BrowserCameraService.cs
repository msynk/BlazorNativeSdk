using BlazorNativeSdk.Browser.Interop;
using BlazorNativeSdk.Devices;
using Microsoft.Extensions.Logging;

namespace BlazorNativeSdk.Browser.Services;

/// <summary>
/// Camera capture through <c>getUserMedia</c> with a built-in fullscreen overlay (preview, shutter, cancel, switch camera),
/// video recording with <c>MediaRecorder</c>, and media picking with a file input. Without <c>getUserMedia</c> (insecure
/// context, old browsers) capture falls back to <c>&lt;input type="file" capture&gt;</c>, which opens the camera app on
/// mobile browsers and a file picker on desktop browsers.
/// </summary>
internal sealed partial class BrowserCameraService : ICameraService
{
    private readonly BrowserInterop _interop;
    private readonly ILogger<BrowserCameraService> _logger;

    public BrowserCameraService(BrowserInterop interop, ILogger<BrowserCameraService> logger)
    {
        _interop = interop;
        _logger = logger;
    }

    public CapabilityId Capability => NativeCapabilities.Camera;

    public async ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var probe = await _interop.ProbeAsync(Capability, "camera", cancellationToken).ConfigureAwait(false);
        if (!probe.IsSuccess)
        {
            return BrowserInteropExtensions.ToStatus(Capability, probe.Error);
        }

        var dto = probe.Value;
        var features = new List<string> { CameraFeatures.Photo, CameraFeatures.Library, CameraFeatures.MultipleSelection };
        if (!dto.UserMedia)
        {
            // File-input capture only: no live preview, facing hints are not reliable, no MediaRecorder overlay.
            return CapabilityStatus.Available(Capability, BrowserConstants.AdapterName, features);
        }

        features.Add(CameraFeatures.CameraSelection);
        if (dto.MediaRecorder)
        {
            features.Add(CameraFeatures.Video);
        }

        return dto.CameraPermission switch
        {
            "denied" => CapabilityStatus.PermissionDenied(Capability, "Camera access was denied for this site. The user can allow it in the browser's site settings.", BrowserConstants.AdapterName),
            "granted" when dto.VideoInputs == 0 => CapabilityStatus.Unavailable(Capability, NativeErrorCodes.HardwareUnavailable, "No camera is connected.", BrowserConstants.AdapterName),
            "granted" => CapabilityStatus.Available(Capability, BrowserConstants.AdapterName, features),
            _ => CapabilityStatus.RequiresPermission(Capability, BrowserConstants.AdapterName, features),
        };
    }

    public ValueTask<NativeResult<NativeFile>> CapturePhotoAsync(PhotoCaptureOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new PhotoCaptureOptions();
        if (options.MaxDimension is <= 0)
        {
            return ValueTask.FromResult(NativeResult.Failure<NativeFile>(
                NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "MaxDimension must be positive.")));
        }

        var request = new CameraRequestDto
        {
            Facing = BrowserMediaMapper.ToFacingMode(options.Camera),
            MaxDimension = options.MaxDimension,
            Quality = BrowserMediaMapper.ToCanvasQuality(options.Quality),
            Title = options.Title,
            Video = false,
            Accept = "image/*",
        };

        return CaptureAsync(request, "photo.jpg", cancellationToken);
    }

    public ValueTask<NativeResult<NativeFile>> CaptureVideoAsync(VideoCaptureOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new VideoCaptureOptions();
        if (options.MaxDuration is { } max && max <= TimeSpan.Zero)
        {
            return ValueTask.FromResult(NativeResult.Failure<NativeFile>(
                NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "MaxDuration must be positive.")));
        }

        var request = new CameraRequestDto
        {
            Facing = BrowserMediaMapper.ToFacingMode(options.Camera),
            Title = options.Title,
            Video = true,
            MaxDurationMs = options.MaxDuration?.TotalMilliseconds,
            Accept = "video/*",
        };

        return CaptureAsync(request, "video.webm", cancellationToken);
    }

    public ValueTask<NativeResult<IReadOnlyList<NativeFile>>> PickMediaAsync(MediaPickOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new MediaPickOptions();
        if (options.SelectionLimit is <= 0)
        {
            return ValueTask.FromResult(NativeResult.Failure<IReadOnlyList<NativeFile>>(
                NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "SelectionLimit must be positive.")));
        }

        return NativeOperation.RunAsync<IReadOnlyList<NativeFile>>(Capability, async ct =>
        {
            // The media library is the file picker filtered by MIME type; browsers do not expose the photo library itself.
            // Browsers cannot limit the number of selected files, so extra files are released after selection.
            var request = new FilePickRequestDto
            {
                Accept = BrowserMediaMapper.ToAccept(options.Kinds),
                Multiple = options.AllowMultiple,
                Types = [],
                UseFileSystemAccess = false,
            };
            int? limit = options.AllowMultiple ? options.SelectionLimit : 1;

            var operationId = BrowserInteropExtensions.NewOperationId();
            var result = await _interop.InvokeInteractiveAsync(
                Capability, "pickFiles", operationId, [BrowserInterop.Serialize(request, BrowserJsonContext.Default.FilePickRequestDto)], ct).ConfigureAwait(false);
            var files = BrowserInterop.Read(Capability, result, BrowserJsonContext.Default.BrowserFileDtoArray);
            if (!files.IsSuccess)
            {
                LogFailed(_logger, "pick", files.Error.Code);
                return files.Error;
            }

            if (files.Value.Length == 0)
            {
                return NativeError.Cancelled(Capability);
            }

            var selected = limit is { } max && files.Value.Length > max ? files.Value[..max] : files.Value;
            var taken = await _interop.TakeFilesAsync(Capability, operationId, selected, "media", ct).ConfigureAwait(false);
            if (selected.Length < files.Value.Length)
            {
                _interop.Abort(operationId); // Releases the files beyond the selection limit.
            }

            return taken;
        }, cancellationToken);
    }

    private ValueTask<NativeResult<NativeFile>> CaptureAsync(CameraRequestDto request, string fallbackName, CancellationToken cancellationToken) =>
        NativeOperation.RunAsync<NativeFile>(Capability, async ct =>
        {
            var operationId = BrowserInteropExtensions.NewOperationId();
            var result = await _interop.InvokeInteractiveAsync(
                Capability, "capture", operationId, [BrowserInterop.Serialize(request, BrowserJsonContext.Default.CameraRequestDto)], ct, NativePermissions.Camera).ConfigureAwait(false);
            var files = BrowserInterop.Read(Capability, result, BrowserJsonContext.Default.BrowserFileDtoArray);
            if (!files.IsSuccess)
            {
                LogFailed(_logger, request.Video ? "video" : "photo", files.Error.Code);
                return files.Error;
            }

            if (files.Value.Length == 0)
            {
                return NativeError.Cancelled(Capability);
            }

            var taken = await _interop.TakeFilesAsync(Capability, operationId, files.Value[..1], fallbackName, ct).ConfigureAwait(false);
            return taken.IsSuccess ? NativeResult.Success(taken.Value[0]) : taken.Error;
        }, cancellationToken);

    [LoggerMessage(EventId = 3101, Level = LogLevel.Debug, Message = "Browser camera {Operation} did not complete: {Code}.")]
    private static partial void LogFailed(ILogger logger, string operation, string code);
}
