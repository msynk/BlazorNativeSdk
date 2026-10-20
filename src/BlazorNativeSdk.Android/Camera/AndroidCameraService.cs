using Android.Content;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
using BlazorNativeSdk.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>
/// <see cref="ICameraService"/> for Android based on Microsoft.Maui.Essentials <see cref="MediaPicker"/> (system camera app
/// and the Android photo picker).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Capture uses the system camera app through <c>ACTION_IMAGE_CAPTURE</c>/<c>ACTION_VIDEO_CAPTURE</c>. The app
/// must declare the <c>CAMERA</c> permission and the <c>&lt;queries&gt;</c> entries for those actions (Android 11+ package
/// visibility). Front/rear selection and video duration limits cannot be enforced through these intents and are ignored.</item>
/// <item>Images are resized and re-encoded only when <see cref="PhotoCaptureOptions.MaxDimension"/> is set;
/// <see cref="PhotoCaptureOptions.Quality"/> applies to that re-encode.</item>
/// <item>Picking uses the system photo picker and needs no permission. Results are copies in the app cache; dispose the
/// returned files to delete them.</item>
/// </list>
/// </remarks>
public sealed partial class AndroidCameraService : ICameraService
{
    private const string AdapterName = "android";
    private const string CacheArea = "bns-media";
    private readonly AndroidPermissionService _permissions;
    private readonly ILogger<AndroidCameraService> _logger;

    /// <summary>Creates the service.</summary>
    public AndroidCameraService(AndroidPermissionService permissions, ILogger<AndroidCameraService> logger)
    {
        _permissions = permissions;
        _logger = logger;
    }

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.Camera;

    /// <inheritdoc />
    public async ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        string[] libraryFeatures = [CameraFeatures.Library, CameraFeatures.MultipleSelection];
        if (!IsCaptureSupported())
        {
            return CapabilityStatus.Available(Capability, AdapterName, libraryFeatures) with
            {
                Reason = "No camera (or no camera app visible to this app) is available; only library picking works. " +
                         "Declare <queries> for android.media.action.IMAGE_CAPTURE and VIDEO_CAPTURE on Android 11+.",
            };
        }

        string[] allFeatures = [CameraFeatures.Photo, CameraFeatures.Video, .. libraryFeatures];
        var permission = await AndroidPermissionService.CheckForAsync(Capability, NativePermissions.Camera, cancellationToken).ConfigureAwait(false);
        if (!permission.IsSuccess)
        {
            return CapabilityStatus.FromError(permission.Error, AdapterName).WithFeatures(libraryFeatures);
        }

        return permission.Value switch
        {
            PermissionStatus.Granted or PermissionStatus.Limited or PermissionStatus.NotRequired => CapabilityStatus.Available(Capability, AdapterName, allFeatures),
            PermissionStatus.NotDetermined or PermissionStatus.Unknown => CapabilityStatus.RequiresPermission(Capability, AdapterName, allFeatures),
            _ => CapabilityStatus.PermissionDenied(Capability,
                "The camera permission was denied; capture is unavailable but picking from the library still works.", AdapterName).WithFeatures(libraryFeatures),
        };
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<NativeFile>> CapturePhotoAsync(PhotoCaptureOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new PhotoCaptureOptions();
        var pickerOptions = new MediaPickerOptions { Title = options.Title };
        if (options.MaxDimension is > 0 and var max)
        {
            pickerOptions.MaximumWidth = max;
            pickerOptions.MaximumHeight = max;
            pickerOptions.CompressionQuality = Math.Clamp(options.Quality, 1, 100);
            pickerOptions.RotateImage = true;
        }

        return CaptureAsync(() => MediaPicker.Default.CapturePhotoAsync(pickerOptions), cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks><see cref="VideoCaptureOptions.MaxDuration"/> and <see cref="VideoCaptureOptions.Camera"/> are not enforced on Android.</remarks>
    public ValueTask<NativeResult<NativeFile>> CaptureVideoAsync(VideoCaptureOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new VideoCaptureOptions();
        var pickerOptions = new MediaPickerOptions { Title = options.Title };
        return CaptureAsync(() => MediaPicker.Default.CaptureVideoAsync(pickerOptions), cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Images or videos alone go through Essentials; <see cref="MediaKinds.Any"/> uses the AndroidX photo picker contract
    /// directly because Essentials has no combined picker. <see cref="MediaPickOptions.SelectionLimit"/> is a hint that
    /// some legacy picker UIs do not enforce.
    /// </remarks>
    public ValueTask<NativeResult<IReadOnlyList<NativeFile>>> PickMediaAsync(MediaPickOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new MediaPickOptions();
        return NativeOperation.RunAsync(Capability, async ct =>
        {
            try
            {
                if (options.Kinds is MediaKinds.Any)
                {
                    return await PickVisualMediaAsync(options, ct).ConfigureAwait(false);
                }

                var limit = options.AllowMultiple ? Math.Max(0, options.SelectionLimit ?? 0) : 1;
                var pickerOptions = new MediaPickerOptions { Title = options.Title, SelectionLimit = limit };
                var video = options.Kinds == MediaKinds.Video;
                var picked = await MainThread.InvokeOnMainThreadAsync(() => video
                        ? MediaPicker.Default.PickVideosAsync(pickerOptions)
                        : MediaPicker.Default.PickPhotosAsync(pickerOptions))
                    .WaitAsync(ct).ConfigureAwait(false);

                if (picked is null || picked.Count == 0)
                {
                    return NativeError.Cancelled(Capability);
                }

                var files = new List<NativeFile>(picked.Count);
                foreach (var file in picked)
                {
                    files.Add(await ContentUriFiles.ToNativeFileAsync(file, CacheArea, ct).ConfigureAwait(false));
                }

                return NativeResult.Success<IReadOnlyList<NativeFile>>(files);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return EssentialsErrors.Map(Capability, ex, NativePermissions.Photos);
            }
        }, cancellationToken);
    }

    private ValueTask<NativeResult<NativeFile>> CaptureAsync(Func<Task<FileResult?>> capture, CancellationToken cancellationToken) =>
        NativeOperation.RunAsync<NativeFile>(Capability, async ct =>
        {
            if (!IsCaptureSupported())
            {
                return NativeError.Unavailable(Capability, NativeErrorCodes.HardwareUnavailable,
                    "No camera, or no camera app is visible to this app. On Android 11+ declare <queries> for android.media.action.IMAGE_CAPTURE and VIDEO_CAPTURE.");
            }

            var permission = await _permissions.EnsureForAsync(Capability, NativePermissions.Camera, ct).ConfigureAwait(false);
            if (!permission.IsSuccess)
            {
                return permission.Error;
            }

            try
            {
                var file = await MainThread.InvokeOnMainThreadAsync(capture).WaitAsync(ct).ConfigureAwait(false);
                if (file is null)
                {
                    return NativeError.Cancelled(Capability);
                }

                return await ContentUriFiles.ToNativeFileAsync(file, CacheArea, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                LogCaptureFailed(_logger, ex);
                return EssentialsErrors.Map(Capability, ex, NativePermissions.Camera);
            }
        }, cancellationToken);

    private async Task<NativeResult<IReadOnlyList<NativeFile>>> PickVisualMediaAsync(MediaPickOptions options, CancellationToken cancellationToken)
    {
        var multiple = options.AllowMultiple && options.SelectionLimit is not 1;
        var systemLimit = GetPickImagesMaxLimit();
        var maxItems = multiple ? Math.Clamp(options.SelectionLimit ?? systemLimit, 2, systemLimit) : 1;
        using var builder = new PickVisualMediaRequest.Builder();
        builder.SetMediaType(ActivityResultContracts.PickVisualMedia.ImageAndVideo.Instance);
        if (multiple)
        {
            builder.SetMaxItems(maxItems);
        }

        using var request = builder.Build();
        var result = await ActivityResultBridge.LaunchAsync(
            Capability,
            multiple ? () => new ActivityResultContracts.PickMultipleVisualMedia(maxItems) : static () => new ActivityResultContracts.PickVisualMedia(),
            request,
            cancellationToken).ConfigureAwait(false);

        var uris = new List<Android.Net.Uri>();
        if (result is Android.Net.Uri single)
        {
            uris.Add(single);
        }
        else if (result is not null)
        {
            var list = Android.Runtime.JavaList<Android.Net.Uri>.FromJniHandle(result.Handle, Android.Runtime.JniHandleOwnership.DoNotTransfer);
            if (list is not null)
            {
                uris.AddRange(list.Where(u => u is not null).Cast<Android.Net.Uri>());
            }
        }

        if (uris.Count == 0)
        {
            return NativeError.Cancelled(Capability);
        }

        var files = new List<NativeFile>(uris.Count);
        try
        {
            foreach (var uri in uris)
            {
                files.Add(await ContentUriFiles.CopyToCacheAsync(uri, CacheArea, cancellationToken).ConfigureAwait(false));
            }
        }
        catch
        {
            foreach (var file in files)
            {
                await file.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }

        return files;
    }

    private static int GetPickImagesMaxLimit()
    {
        // MediaStore.getPickImagesMaxLimit() exists on Android 13+ (and via SDK extensions on 11+); 100 is a safe ceiling.
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            return Math.Max(2, Android.Provider.MediaStore.PickImagesMaxLimit);
        }

        return 100;
    }

    private static bool IsCaptureSupported()
    {
        try
        {
            return MediaPicker.Default.IsCaptureSupported;
        }
        catch (Exception ex) when (ex is FeatureNotSupportedException or Java.Lang.Exception)
        {
            return false;
        }
    }


    [LoggerMessage(EventId = 3201, Level = LogLevel.Warning, Message = "Camera capture failed.")]
    private static partial void LogCaptureFailed(ILogger logger, Exception exception);
}
