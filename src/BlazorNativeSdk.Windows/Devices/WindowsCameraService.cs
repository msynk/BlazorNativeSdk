using BlazorNativeSdk.Devices;
using Windows.Storage.Pickers;

namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>
/// <see cref="ICameraService"/> for Windows desktop hosts: picking existing photos and videos through the file picker.
/// Capture is unsupported because Windows offers no system camera-capture UI to desktop apps.
/// </summary>
internal sealed class WindowsCameraService : ICameraService
{
    internal const string CaptureUnsupportedReason =
        "Windows has no system camera capture UI for desktop apps. Capture in the WebView with getUserMedia " +
        "(for example through the BlazorNativeSdk.Browser adapter's camera overlay) or build a custom capture page.";

    private readonly WindowsFilePickerService _picker;

    public WindowsCameraService(WindowsFilePickerService picker) => _picker = picker;

    public CapabilityId Capability => NativeCapabilities.Camera;

    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(WindowsFilePickerService.IsElevated
            ? CapabilityStatus.Unavailable(Capability, WindowsAdapter.ElevatedProcessCode, "Media cannot be picked from an elevated (administrator) process.", WindowsAdapter.Name)
            : CapabilityStatus.Available(Capability, WindowsAdapter.Name, CameraFeatures.Library, CameraFeatures.MultipleSelection));

    public ValueTask<NativeResult<NativeFile>> CapturePhotoAsync(PhotoCaptureOptions? options = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<NativeResult<NativeFile>>(NativeError.Unsupported(Capability, CaptureUnsupportedReason));

    public ValueTask<NativeResult<NativeFile>> CaptureVideoAsync(VideoCaptureOptions? options = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<NativeResult<NativeFile>>(NativeError.Unsupported(Capability, CaptureUnsupportedReason));

    public async ValueTask<NativeResult<IReadOnlyList<NativeFile>>> PickMediaAsync(MediaPickOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new MediaPickOptions();
        var filters = new List<FileTypeFilter>(2);
        if ((options.Kinds & MediaKinds.Image) != 0)
        {
            filters.Add(FileTypeFilter.Images);
        }

        if ((options.Kinds & MediaKinds.Video) != 0)
        {
            filters.Add(FileTypeFilter.Videos);
        }

        if (filters.Count == 0)
        {
            filters.Add(FileTypeFilter.Images);
        }

        var start = options.Kinds == MediaKinds.Video ? PickerLocationId.VideosLibrary : PickerLocationId.PicturesLibrary;
        var pickOptions = new FilePickOptions { AllowMultiple = options.AllowMultiple, FileTypes = filters, Title = options.Title };
        var result = await _picker.PickFilesCoreAsync(Capability, pickOptions, start, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || options.SelectionLimit is not { } limit || limit <= 0 || result.Value.Count <= limit)
        {
            return result;
        }

        // The picker cannot enforce a limit: keep the first items and release the rest.
        foreach (var extra in result.Value.Skip(limit))
        {
            await extra.DisposeAsync().ConfigureAwait(false);
        }

        return NativeResult.Success<IReadOnlyList<NativeFile>>(result.Value.Take(limit).ToArray());
    }
}

/// <summary>
/// <see cref="IBarcodeScanner"/> placeholder for Windows: there is no built-in barcode decoder for desktop apps.
/// Register your own scanner (for example one based on ZXing.Net) to replace it.
/// </summary>
internal sealed class WindowsBarcodeScanner : IBarcodeScanner
{
    internal const string Reason =
        "Windows has no built-in barcode decoder for desktop apps. Register an IBarcodeScanner implementation " +
        "(for example one based on ZXing.Net) with builder.AddCapability<IBarcodeScanner, YourScanner>(NativeCapabilities.Barcode).";

    public CapabilityId Capability => NativeCapabilities.Barcode;

    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Unsupported(Capability, Reason, WindowsAdapter.Name));

    public ValueTask<NativeResult<Barcode>> ScanAsync(BarcodeScanOptions? options = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<NativeResult<Barcode>>(NativeError.Unsupported(Capability, Reason));

    public ValueTask<NativeResult<IReadOnlyList<Barcode>>> DetectAsync(NativeFile image, BarcodeFormats formats = BarcodeFormats.All, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<NativeResult<IReadOnlyList<Barcode>>>(NativeError.Unsupported(Capability, Reason));
}
