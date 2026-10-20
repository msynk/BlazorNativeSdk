namespace BlazorNativeSdk.Devices;

/// <summary>Which camera to prefer.</summary>
public enum CameraFacing
{
    /// <summary>Let the platform decide.</summary>
    Default = 0,

    /// <summary>The rear (environment-facing) camera.</summary>
    Rear,

    /// <summary>The front (user-facing) camera.</summary>
    Front,
}

/// <summary>Media kinds that can be picked.</summary>
[Flags]
public enum MediaKinds
{
    /// <summary>Photos and images.</summary>
    Image = 1,

    /// <summary>Videos.</summary>
    Video = 2,

    /// <summary>Photos and videos.</summary>
    Any = Image | Video,
}

/// <summary>Options for capturing a photo.</summary>
public sealed record PhotoCaptureOptions
{
    /// <summary>The preferred camera. Platforms that cannot honor the preference ignore it.</summary>
    public CameraFacing Camera { get; init; } = CameraFacing.Rear;

    /// <summary>Downscale so that neither dimension exceeds this many pixels (preserving aspect ratio), when supported.</summary>
    public int? MaxDimension { get; init; }

    /// <summary>JPEG quality from 1 to 100 used when the adapter re-encodes the image.</summary>
    public int Quality { get; init; } = 90;

    /// <summary>A title for the capture UI where supported.</summary>
    public string? Title { get; init; }
}

/// <summary>Options for capturing a video.</summary>
public sealed record VideoCaptureOptions
{
    /// <summary>The preferred camera.</summary>
    public CameraFacing Camera { get; init; } = CameraFacing.Rear;

    /// <summary>The maximum duration where supported.</summary>
    public TimeSpan? MaxDuration { get; init; }

    /// <summary>A title for the capture UI where supported.</summary>
    public string? Title { get; init; }
}

/// <summary>Options for picking media from the user's library.</summary>
public sealed record MediaPickOptions
{
    /// <summary>The kinds of media to offer.</summary>
    public MediaKinds Kinds { get; init; } = MediaKinds.Image;

    /// <summary>Allow selecting several items.</summary>
    public bool AllowMultiple { get; init; }

    /// <summary>Maximum number of items when <see cref="AllowMultiple"/> is set; <see langword="null"/> uses the platform default.</summary>
    public int? SelectionLimit { get; init; }

    /// <summary>A title for the picker where supported.</summary>
    public string? Title { get; init; }
}

/// <summary>Optional features reported by <see cref="ICameraService"/>.</summary>
public static class CameraFeatures
{
    /// <summary>Photo capture.</summary>
    public const string Photo = "photo";

    /// <summary>Video capture.</summary>
    public const string Video = "video";

    /// <summary>Picking existing media from the library/gallery.</summary>
    public const string Library = "library";

    /// <summary>Choosing front or rear camera is honored.</summary>
    public const string CameraSelection = "camera-selection";

    /// <summary>Selecting multiple library items.</summary>
    public const string MultipleSelection = "multiple-selection";
}

/// <summary>Captures photos and videos with the device camera and picks existing media.</summary>
/// <remarks>
/// Hybrid hosts use the platform camera UI. Browser hosts use <c>getUserMedia</c> with a built-in capture overlay,
/// falling back to the file input <c>capture</c> attribute on mobile browsers.
/// </remarks>
public interface ICameraService : INativeCapability
{
    /// <summary>Captures a photo.</summary>
    /// <returns>The captured image, <see cref="NativeResultStatus.Cancelled"/>, <see cref="NativeResultStatus.PermissionDenied"/>, or <see cref="NativeResultStatus.Unavailable"/> when no camera exists.</returns>
    ValueTask<NativeResult<NativeFile>> CapturePhotoAsync(PhotoCaptureOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Records a video. Check the <see cref="CameraFeatures.Video"/> feature first.</summary>
    ValueTask<NativeResult<NativeFile>> CaptureVideoAsync(VideoCaptureOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Picks existing photos or videos from the user's library.</summary>
    ValueTask<NativeResult<IReadOnlyList<NativeFile>>> PickMediaAsync(MediaPickOptions? options = null, CancellationToken cancellationToken = default);
}
