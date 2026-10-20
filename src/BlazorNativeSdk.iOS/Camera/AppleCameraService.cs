using AVFoundation;
using BlazorNativeSdk.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;
using PhotosUI;
using UniformTypeIdentifiers;
using PermissionStatus = BlazorNativeSdk.PermissionStatus;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary><see cref="ICameraService"/> based on the .NET MAUI Essentials media picker and <c>PHPickerViewController</c>.</summary>
/// <remarks>
/// <para>
/// Capture uses the system camera UI (<c>UIImagePickerController</c>) and requires <c>NSCameraUsageDescription</c>; video capture
/// also requires <c>NSMicrophoneUsageDescription</c> because the system records sound. Picking from the library uses
/// <c>PHPickerViewController</c>, which runs out of process and needs no photo library permission.
/// </para>
/// <para>
/// Mac Catalyst has no camera capture UI: capture returns <see cref="NativeErrorCodes.HardwareUnavailable"/> and only library
/// picking is offered. <see cref="PhotoCaptureOptions.Camera"/>, <see cref="VideoCaptureOptions.Camera"/> and
/// <see cref="VideoCaptureOptions.MaxDuration"/> are not honored by the system camera UI (the user switches cameras).
/// If the caller cancels while a picker is visible, the picker is dismissed and <see cref="OperationCanceledException"/> is thrown.
/// </para>
/// </remarks>
public sealed partial class AppleCameraService : ICameraService
{
    private static readonly string[] s_libraryFeatures = [CameraFeatures.Library, CameraFeatures.MultipleSelection];
    private readonly ILogger<AppleCameraService> _logger;

    /// <summary>Creates the service.</summary>
    public AppleCameraService(ILogger<AppleCameraService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.Camera;

    /// <inheritdoc />
    public async ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var adapter = AppleHost.AdapterName;
        if (!await IsCaptureSupportedAsync().WaitAsync(cancellationToken).ConfigureAwait(false))
        {
            return CapabilityStatus.Available(Capability, adapter, s_libraryFeatures) with
            {
                ReasonCode = NativeErrorCodes.HardwareUnavailable,
                Reason = OperatingSystem.IsMacCatalyst()
                    ? "Mac Catalyst has no camera capture UI; only picking from the photo library is available."
                    : "No camera is available; only picking from the photo library is available.",
            };
        }

        if (AppleInfoPlist.RequireUsageDescription(Capability, AppleInfoPlist.CameraUsage, "the camera") is { } missing)
        {
            return CapabilityStatus.Available(Capability, adapter, s_libraryFeatures) with { ReasonCode = missing.Code, Reason = missing.Message };
        }

        var captureFeatures = new List<string>(s_libraryFeatures) { CameraFeatures.Photo };
        if (AppleInfoPlist.HasUsageDescription(AppleInfoPlist.MicrophoneUsage))
        {
            captureFeatures.Add(CameraFeatures.Video);
        }

        return AppleStatusMapping.FromCapture(AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Video)) switch
        {
            PermissionStatus.Granted => CapabilityStatus.Available(Capability, adapter, captureFeatures),
            PermissionStatus.NotDetermined => CapabilityStatus.RequiresPermission(Capability, adapter, captureFeatures),
            var denied => CapabilityStatus.PermissionDenied(Capability, $"Camera access is {denied}; only picking from the photo library is available.", adapter)
                .WithFeatures(s_libraryFeatures),
        };
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<NativeFile>> CapturePhotoAsync(PhotoCaptureOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new PhotoCaptureOptions();
        return NativeOperation.RunAsync<NativeFile>(Capability, async token =>
        {
            if (await PrepareCaptureAsync(video: false, token).ConfigureAwait(false) is { } error)
            {
                return error;
            }

            var pickerOptions = new MediaPickerOptions
            {
                Title = options.Title,
                CompressionQuality = Math.Clamp(options.Quality, 1, 100),
                MaximumWidth = options.MaxDimension,
                MaximumHeight = options.MaxDimension,
            };
            return await CaptureAsync(() => MediaPicker.Default.CapturePhotoAsync(pickerOptions), token).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<NativeFile>> CaptureVideoAsync(VideoCaptureOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new VideoCaptureOptions();
        return NativeOperation.RunAsync<NativeFile>(Capability, async token =>
        {
            if (await PrepareCaptureAsync(video: true, token).ConfigureAwait(false) is { } error)
            {
                return error;
            }

            var pickerOptions = new MediaPickerOptions { Title = options.Title };
            return await CaptureAsync(() => MediaPicker.Default.CaptureVideoAsync(pickerOptions), token).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<IReadOnlyList<NativeFile>>> PickMediaAsync(MediaPickOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new MediaPickOptions();
        return NativeOperation.RunAsync<IReadOnlyList<NativeFile>>(Capability, async token =>
        {
            var kinds = options.Kinds & MediaKinds.Any;
            if (kinds == 0)
            {
                return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "MediaPickOptions.Kinds must include Image and/or Video.");
            }

            var limit = options.AllowMultiple ? Math.Max(0, options.SelectionLimit ?? 0) : 1;
            if (kinds == MediaKinds.Any)
            {
                // Essentials picks photos or videos, not both; use PHPicker directly for mixed selection.
                return await PickMixedAsync(limit, token).ConfigureAwait(false);
            }

            var pickerOptions = new MediaPickerOptions { Title = options.Title, SelectionLimit = limit };
            var results = await AwaitPickerAsync<List<FileResult>, PHPickerViewController>(
                AppleUI.OnMainThreadAsync(() => kinds == MediaKinds.Video
                    ? MediaPicker.Default.PickVideosAsync(pickerOptions)
                    : MediaPicker.Default.PickPhotosAsync(pickerOptions)),
                token).ConfigureAwait(false);

            if (results is not { Count: > 0 })
            {
                return NativeError.Cancelled(Capability);
            }

            var files = new List<NativeFile>(results.Count);
            try
            {
                foreach (var result in results)
                {
                    files.Add(await AppleFiles.FromFileResultAsync(result, token).ConfigureAwait(false));
                }
            }
            catch
            {
                await DisposeAllAsync(files).ConfigureAwait(false);
                throw;
            }

            return files;
        }, cancellationToken);
    }

    private static Task<bool> IsCaptureSupportedAsync() =>
        AppleUI.OnMainThreadAsync(() => MediaPicker.Default.IsCaptureSupported);

    private async Task<NativeError?> PrepareCaptureAsync(bool video, CancellationToken cancellationToken)
    {
        if (!await IsCaptureSupportedAsync().WaitAsync(cancellationToken).ConfigureAwait(false))
        {
            return NativeError.Unavailable(Capability, NativeErrorCodes.HardwareUnavailable,
                OperatingSystem.IsMacCatalyst() ? "Camera capture is not available on Mac Catalyst." : "No camera is available on this device.");
        }

        if (video && AppleInfoPlist.RequireUsageDescription(Capability, AppleInfoPlist.MicrophoneUsage, "the microphone (video is recorded with sound)") is { } missing)
        {
            return missing;
        }

        return await AppleCaptureAccess.EnsureGrantedAsync(Capability, AVAuthorizationMediaType.Video, cancellationToken).ConfigureAwait(false);
    }

    private async Task<NativeResult<NativeFile>> CaptureAsync(Func<Task<FileResult?>> capture, CancellationToken cancellationToken)
    {
        FileResult? result;
        try
        {
            result = await AwaitPickerAsync<FileResult?, UIImagePickerController>(AppleUI.OnMainThreadAsync(capture), cancellationToken).ConfigureAwait(false);
        }
        catch (PermissionException ex)
        {
            return NativeError.PermissionDenied(Capability, NativePermissions.Camera, PermissionStatus.Denied, ex.Message);
        }

        if (result is null)
        {
            return NativeError.Cancelled(Capability);
        }

        return await AppleFiles.FromFileResultAsync(result, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> AwaitPickerAsync<T, TController>(Task<T> picker, CancellationToken cancellationToken)
        where TController : UIViewController
    {
        try
        {
            return await picker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await AppleUI.DismissTopIfAsync<TController>().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<NativeResult<IReadOnlyList<NativeFile>>> PickMixedAsync(int limit, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<PHPickerResult[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pickerDelegate = new PickerDelegate(completion);
        var presented = await AppleUI.OnMainThreadAsync(async () =>
        {
            if (AppleUI.GetTopViewController() is not { } presenter)
            {
                return false;
            }

            var configuration = new PHPickerConfiguration
            {
                SelectionLimit = limit,
                Filter = PHPickerFilter.GetAnyFilterMatchingSubfilters([PHPickerFilter.ImagesFilter, PHPickerFilter.VideosFilter]),
            };
            var picker = new PHPickerViewController(configuration) { Delegate = pickerDelegate };
            await presenter.PresentViewControllerAsync(picker, true).ConfigureAwait(true);
            return true;
        }).ConfigureAwait(false);

        if (!presented)
        {
            return AppleUI.NoPresenter(Capability);
        }

        // The picker references its delegate weakly; the local keeps it alive until the user finishes.
        var results = await AwaitPickerAsync<PHPickerResult[], PHPickerViewController>(completion.Task, cancellationToken).ConfigureAwait(false);
        GC.KeepAlive(pickerDelegate);
        if (results.Length == 0)
        {
            return NativeError.Cancelled(Capability);
        }

        var files = new List<NativeFile>(results.Length);
        try
        {
            foreach (var result in results)
            {
                if (await LoadAsync(result.ItemProvider, cancellationToken).ConfigureAwait(false) is { } file)
                {
                    files.Add(file);
                }
            }
        }
        catch
        {
            await DisposeAllAsync(files).ConfigureAwait(false);
            throw;
        }

        if (files.Count == 0)
        {
            return NativeError.Failed(Capability, NativeErrorCodes.PlatformError, "None of the selected items could be loaded (for example iCloud items that failed to download).");
        }

        return files;
    }

    private async Task<NativeFile?> LoadAsync(NSItemProvider provider, CancellationToken cancellationToken)
    {
        var typeIdentifier = provider.RegisteredTypeIdentifiers?
            .Select(id => (Id: id, Type: UTType.CreateFromIdentifier(id)))
            .FirstOrDefault(t => t.Type is not null && (t.Type.ConformsTo(UTTypes.Movie) || t.Type.ConformsTo(UTTypes.Image)))
            .Id;
        if (typeIdentifier is null)
        {
            LogItemSkipped(_logger, "no image or movie representation");
            return null;
        }

        var completion = new TaskCompletionSource<NativeFile?>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.LoadFileRepresentation(typeIdentifier, (url, error) =>
        {
            // The system deletes the file when this handler returns, so copy it synchronously.
            try
            {
                if (url?.Path is not { } source)
                {
                    LogItemSkipped(_logger, error?.LocalizedDescription ?? "no file");
                    completion.TrySetResult(null);
                    return;
                }

                var target = AppleFiles.CreateTempFilePath(url.LastPathComponent ?? provider.SuggestedName);
                File.Copy(source, target);
                var mime = UTType.CreateFromIdentifier(typeIdentifier)?.PreferredMimeType;
                completion.TrySetResult(new PhysicalNativeFile(target, mime, deleteOnDispose: true));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DisposeAllAsync(List<NativeFile> files)
    {
        foreach (var file in files)
        {
            await file.DisposeAsync().ConfigureAwait(false);
        }
    }

    [LoggerMessage(EventId = 3201, Level = LogLevel.Debug, Message = "A picked library item was skipped: {Reason}.")]
    private static partial void LogItemSkipped(ILogger logger, string reason);

    private sealed class PickerDelegate(TaskCompletionSource<PHPickerResult[]> completion) : PHPickerViewControllerDelegate
    {
        public override void DidFinishPicking(PHPickerViewController picker, PHPickerResult[] results)
        {
            picker.DismissViewController(true, null);
            completion.TrySetResult(results ?? []);
        }
    }
}
