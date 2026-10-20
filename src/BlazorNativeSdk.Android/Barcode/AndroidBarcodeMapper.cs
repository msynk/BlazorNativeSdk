using BlazorNativeSdk.Devices;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>
/// Maps between <see cref="BarcodeFormats"/> and ML Kit / Google code scanner values. Pure: the ML Kit constants are
/// duplicated as literals so the mapping can be unit tested without the Android bindings.
/// </summary>
internal static class AndroidBarcodeMapper
{
    // com.google.mlkit.vision.barcode.common.Barcode.FORMAT_*
    public const int MlKitAllFormats = 0;
    public const int MlKitCode128 = 1;
    public const int MlKitCode39 = 2;
    public const int MlKitCode93 = 4;
    public const int MlKitCodabar = 8;
    public const int MlKitDataMatrix = 16;
    public const int MlKitEan13 = 32;
    public const int MlKitEan8 = 64;
    public const int MlKitItf = 128;
    public const int MlKitQrCode = 256;
    public const int MlKitUpcA = 512;
    public const int MlKitUpcE = 1024;
    public const int MlKitPdf417 = 2048;
    public const int MlKitAztec = 4096;

    // com.google.mlkit.common.MlKitException codes used by the code scanner.
    public const int MlKitUnavailable = 14;
    public const int MlKitCodeScannerUnavailable = 200;
    public const int MlKitCodeScannerCancelled = 201;
    public const int MlKitCodeScannerCameraPermissionNotGranted = 202;
    public const int MlKitCodeScannerAppNameUnavailable = 203;
    public const int MlKitCodeScannerTaskInProgress = 204;
    public const int MlKitCodeScannerPlayServicesTooOld = 207;

    private static readonly (BarcodeFormats Format, int MlKit)[] s_formats =
    [
        (BarcodeFormats.QrCode, MlKitQrCode),
        (BarcodeFormats.Aztec, MlKitAztec),
        (BarcodeFormats.DataMatrix, MlKitDataMatrix),
        (BarcodeFormats.Pdf417, MlKitPdf417),
        (BarcodeFormats.Code128, MlKitCode128),
        (BarcodeFormats.Code39, MlKitCode39),
        (BarcodeFormats.Code93, MlKitCode93),
        (BarcodeFormats.Codabar, MlKitCodabar),
        (BarcodeFormats.Ean13, MlKitEan13),
        (BarcodeFormats.Ean8, MlKitEan8),
        (BarcodeFormats.Itf, MlKitItf),
        (BarcodeFormats.UpcA, MlKitUpcA),
        (BarcodeFormats.UpcE, MlKitUpcE),
    ];

    /// <summary>Maps an ML Kit format to <see cref="BarcodeFormats"/>; unknown formats map to <see cref="BarcodeFormats.None"/>.</summary>
    public static BarcodeFormats FromMlKit(int format)
    {
        foreach (var (contract, mlKit) in s_formats)
        {
            if (mlKit == format)
            {
                return contract;
            }
        }

        return BarcodeFormats.None;
    }

    /// <summary>
    /// Maps requested formats to the ML Kit format list (never empty). <see cref="BarcodeFormats.None"/> and
    /// <see cref="BarcodeFormats.All"/> map to <c>FORMAT_ALL_FORMATS</c>.
    /// </summary>
    public static int[] ToMlKit(BarcodeFormats formats)
    {
        if (formats == BarcodeFormats.None || (formats & BarcodeFormats.All) == BarcodeFormats.All)
        {
            return [MlKitAllFormats];
        }

        var result = s_formats.Where(f => (formats & f.Format) != 0).Select(f => f.MlKit).ToArray();
        return result.Length == 0 ? [MlKitAllFormats] : result;
    }

    /// <summary>Maps an <c>MlKitException</c> error code raised by the code scanner.</summary>
    public static NativeError MapScanError(int code, string? message)
    {
        var capability = NativeCapabilities.Barcode;
        var details = new Dictionary<string, string> { ["mlkit.code"] = code.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        var text = string.IsNullOrWhiteSpace(message) ? $"The Google code scanner failed with code {code}." : message;
        return code switch
        {
            MlKitCodeScannerCancelled => NativeError.Cancelled(capability),
            MlKitCodeScannerUnavailable or MlKitUnavailable => NativeError.Unavailable(capability, AndroidErrorCodes.ModuleUnavailable,
                "The Google code scanner module is not installed yet. Google Play services downloads it in the background; try again " +
                "shortly. Add the com.google.mlkit.vision.DEPENDENCIES meta-data (value barcode_ui) to download it at install time.") with { Details = details },
            MlKitCodeScannerCameraPermissionNotGranted => NativeError.PermissionDenied(capability, NativePermissions.Camera, PermissionStatus.Denied,
                "The user denied camera access to the Google code scanner.") with { Details = details },
            MlKitCodeScannerTaskInProgress => NativeError.Failed(capability, NativeErrorCodes.Conflict, "Another scan is already in progress.") with { Details = details },
            MlKitCodeScannerPlayServicesTooOld => NativeError.Unavailable(capability, AndroidErrorCodes.PlayServicesUnavailable,
                "Google Play services is too old for the code scanner; the user must update it.") with { Details = details },
            MlKitCodeScannerAppNameUnavailable => NativeError.Failed(capability, NativeErrorCodes.PlatformError,
                "The code scanner could not read the app name; set android:label on the <application> element.") with { Details = details },
            _ => NativeError.Failed(capability, NativeErrorCodes.PlatformError, text) with { Details = details },
        };
    }

    /// <summary>Converts a bounding box in image pixels.</summary>
    public static BarcodeBounds? ToBounds(int left, int top, int right, int bottom) =>
        right > left && bottom > top ? new BarcodeBounds(left, top, right - left, bottom - top) : null;
}
