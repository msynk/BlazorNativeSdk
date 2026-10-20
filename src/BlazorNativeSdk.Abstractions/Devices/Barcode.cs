namespace BlazorNativeSdk.Devices;

/// <summary>Barcode symbologies.</summary>
[Flags]
public enum BarcodeFormats
{
    /// <summary>No format.</summary>
    None = 0,

    /// <summary>QR Code.</summary>
    QrCode = 1 << 0,

    /// <summary>Aztec.</summary>
    Aztec = 1 << 1,

    /// <summary>Data Matrix.</summary>
    DataMatrix = 1 << 2,

    /// <summary>PDF417.</summary>
    Pdf417 = 1 << 3,

    /// <summary>Code 128.</summary>
    Code128 = 1 << 4,

    /// <summary>Code 39.</summary>
    Code39 = 1 << 5,

    /// <summary>Code 93.</summary>
    Code93 = 1 << 6,

    /// <summary>Codabar.</summary>
    Codabar = 1 << 7,

    /// <summary>EAN-13.</summary>
    Ean13 = 1 << 8,

    /// <summary>EAN-8.</summary>
    Ean8 = 1 << 9,

    /// <summary>ITF (Interleaved 2 of 5).</summary>
    Itf = 1 << 10,

    /// <summary>UPC-A.</summary>
    UpcA = 1 << 11,

    /// <summary>UPC-E.</summary>
    UpcE = 1 << 12,

    /// <summary>Two-dimensional formats.</summary>
    TwoDimensional = QrCode | Aztec | DataMatrix | Pdf417,

    /// <summary>One-dimensional (linear) formats.</summary>
    Linear = Code128 | Code39 | Code93 | Codabar | Ean13 | Ean8 | Itf | UpcA | UpcE,

    /// <summary>All formats.</summary>
    All = TwoDimensional | Linear,
}

/// <summary>The location of a barcode within the analyzed image, in image pixels.</summary>
/// <param name="X">Left coordinate.</param>
/// <param name="Y">Top coordinate.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public readonly record struct BarcodeBounds(double X, double Y, double Width, double Height);

/// <summary>A detected barcode.</summary>
public sealed record Barcode
{
    /// <summary>The decoded text, when the payload is textual.</summary>
    public string? RawValue { get; init; }

    /// <summary>The symbology.</summary>
    public required BarcodeFormats Format { get; init; }

    /// <summary>The raw payload bytes, when the platform exposes them.</summary>
    public ReadOnlyMemory<byte>? RawBytes { get; init; }

    /// <summary>The location in the analyzed image, when reported.</summary>
    public BarcodeBounds? Bounds { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"{Format}: {RawValue}";
}

/// <summary>Options for <see cref="IBarcodeScanner.ScanAsync"/>.</summary>
public sealed record BarcodeScanOptions
{
    /// <summary>Formats to look for. Restricting formats speeds up detection and avoids false positives.</summary>
    public BarcodeFormats Formats { get; init; } = BarcodeFormats.All;

    /// <summary>A hint shown in the scanner UI, such as "Point the camera at the ticket".</summary>
    public string? Prompt { get; init; }

    /// <summary>The preferred camera.</summary>
    public CameraFacing Camera { get; init; } = CameraFacing.Rear;

    /// <summary>Stop scanning after this duration and return <see cref="NativeErrorCodes.Timeout"/>.</summary>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>Optional features reported by <see cref="IBarcodeScanner"/>.</summary>
public static class BarcodeFeatures
{
    /// <summary>Live scanning with a camera UI.</summary>
    public const string LiveScan = "live-scan";

    /// <summary>Detecting barcodes in an existing image.</summary>
    public const string ImageDetection = "image-detection";

    /// <summary>Live scanning that does not require the app to hold the camera permission (for example Google Code Scanner).</summary>
    public const string PermissionlessScan = "permissionless-scan";

    /// <summary><see cref="BarcodeScanOptions.Timeout"/> is honored.</summary>
    public const string Timeout = "timeout";

    /// <summary><see cref="BarcodeScanOptions.Prompt"/> is displayed.</summary>
    public const string CustomPrompt = "custom-prompt";

    /// <summary><see cref="BarcodeScanOptions.Camera"/> is honored.</summary>
    public const string CameraSelection = "camera-selection";
}

/// <summary>Scans barcodes and QR codes with the camera or from images.</summary>
/// <remarks>
/// Adapters rely on platform detectors: the Shape Detection API (<c>BarcodeDetector</c>) in browsers,
/// Google Code Scanner / ML Kit on Android and Vision/VisionKit on Apple platforms. Where no detector exists
/// the capability reports <see cref="CapabilityAvailability.Unsupported"/>; register your own
/// <see cref="IBarcodeScanner"/> (for example backed by ZXing.Net) to fill the gap.
/// </remarks>
public interface IBarcodeScanner : INativeCapability
{
    /// <summary>Shows a camera UI and returns the first barcode detected.</summary>
    ValueTask<NativeResult<Barcode>> ScanAsync(BarcodeScanOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Detects barcodes in an image.</summary>
    /// <returns>Detected barcodes; an empty list when none were found.</returns>
    ValueTask<NativeResult<IReadOnlyList<Barcode>>> DetectAsync(NativeFile image, BarcodeFormats formats = BarcodeFormats.All, CancellationToken cancellationToken = default);
}
