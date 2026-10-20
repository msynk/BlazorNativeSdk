using AVFoundation;
using BlazorNativeSdk.Devices;
using Vision;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>Maps <see cref="BarcodeFormats"/> to Vision symbologies and AVFoundation metadata types, and back.</summary>
/// <remarks>
/// Apple detectors report UPC-A as EAN-13 with a leading zero. When the caller asked for UPC-A, such codes are reported as
/// <see cref="BarcodeFormats.UpcA"/> with the 12-digit value.
/// </remarks>
internal static class AppleBarcodeMapping
{
    /// <summary>Vision symbologies for the requested formats.</summary>
    public static VNBarcodeSymbology[] ToVisionSymbologies(BarcodeFormats formats)
    {
        var result = new List<VNBarcodeSymbology>();
        void Add(BarcodeFormats format, params VNBarcodeSymbology[] symbologies)
        {
            if ((formats & format) != 0)
            {
                foreach (var symbology in symbologies)
                {
                    if (!result.Contains(symbology))
                    {
                        result.Add(symbology);
                    }
                }
            }
        }

        Add(BarcodeFormats.QrCode, VNBarcodeSymbology.QR);
        Add(BarcodeFormats.Aztec, VNBarcodeSymbology.Aztec);
        Add(BarcodeFormats.DataMatrix, VNBarcodeSymbology.DataMatrix);
        Add(BarcodeFormats.Pdf417, VNBarcodeSymbology.Pdf417);
        Add(BarcodeFormats.Code128, VNBarcodeSymbology.Code128);
        Add(BarcodeFormats.Code39, VNBarcodeSymbology.Code39, VNBarcodeSymbology.Code39Checksum, VNBarcodeSymbology.Code39FullAscii, VNBarcodeSymbology.Code39FullAsciiChecksum);
        Add(BarcodeFormats.Code93, VNBarcodeSymbology.Code93, VNBarcodeSymbology.Code93i);
        Add(BarcodeFormats.Codabar, VNBarcodeSymbology.Codabar);
        Add(BarcodeFormats.Ean13 | BarcodeFormats.UpcA, VNBarcodeSymbology.Ean13);
        Add(BarcodeFormats.Ean8, VNBarcodeSymbology.Ean8);
        Add(BarcodeFormats.Itf, VNBarcodeSymbology.I2OF5, VNBarcodeSymbology.I2OF5Checksum, VNBarcodeSymbology.Itf14);
        Add(BarcodeFormats.UpcE, VNBarcodeSymbology.Upce);
        return [.. result];
    }

    /// <summary>The format of a Vision symbology, or <see cref="BarcodeFormats.None"/> when it has no equivalent.</summary>
    public static BarcodeFormats FromVision(VNBarcodeSymbology symbology) => symbology switch
    {
        VNBarcodeSymbology.QR or VNBarcodeSymbology.MicroQR => BarcodeFormats.QrCode,
        VNBarcodeSymbology.Aztec => BarcodeFormats.Aztec,
        VNBarcodeSymbology.DataMatrix => BarcodeFormats.DataMatrix,
        VNBarcodeSymbology.Pdf417 or VNBarcodeSymbology.MicroPdf417 => BarcodeFormats.Pdf417,
        VNBarcodeSymbology.Code128 => BarcodeFormats.Code128,
        VNBarcodeSymbology.Code39 or VNBarcodeSymbology.Code39Checksum or VNBarcodeSymbology.Code39FullAscii or VNBarcodeSymbology.Code39FullAsciiChecksum => BarcodeFormats.Code39,
        VNBarcodeSymbology.Code93 or VNBarcodeSymbology.Code93i => BarcodeFormats.Code93,
        VNBarcodeSymbology.Codabar => BarcodeFormats.Codabar,
        VNBarcodeSymbology.Ean13 => BarcodeFormats.Ean13,
        VNBarcodeSymbology.Ean8 => BarcodeFormats.Ean8,
        VNBarcodeSymbology.I2OF5 or VNBarcodeSymbology.I2OF5Checksum or VNBarcodeSymbology.Itf14 => BarcodeFormats.Itf,
        VNBarcodeSymbology.Upce => BarcodeFormats.UpcE,
        _ => BarcodeFormats.None,
    };

    /// <summary>AVFoundation metadata object types for the requested formats.</summary>
    public static AVMetadataObjectType ToMetadataTypes(BarcodeFormats formats)
    {
        var types = AVMetadataObjectType.None;
        if ((formats & BarcodeFormats.QrCode) != 0)
        {
            types |= AVMetadataObjectType.QRCode;
        }

        if ((formats & BarcodeFormats.Aztec) != 0)
        {
            types |= AVMetadataObjectType.AztecCode;
        }

        if ((formats & BarcodeFormats.DataMatrix) != 0)
        {
            types |= AVMetadataObjectType.DataMatrixCode;
        }

        if ((formats & BarcodeFormats.Pdf417) != 0)
        {
            types |= AVMetadataObjectType.PDF417Code;
        }

        if ((formats & BarcodeFormats.Code128) != 0)
        {
            types |= AVMetadataObjectType.Code128Code;
        }

        if ((formats & BarcodeFormats.Code39) != 0)
        {
            types |= AVMetadataObjectType.Code39Code | AVMetadataObjectType.Code39Mod43Code;
        }

        if ((formats & BarcodeFormats.Code93) != 0)
        {
            types |= AVMetadataObjectType.Code93Code;
        }

        // Codabar is only recognized by AVCaptureMetadataOutput from iOS / Mac Catalyst 15.4.
        if ((formats & BarcodeFormats.Codabar) != 0 && (OperatingSystem.IsIOSVersionAtLeast(15, 4) || OperatingSystem.IsMacCatalystVersionAtLeast(15, 4)))
        {
            types |= AVMetadataObjectType.CodabarCode;
        }

        if ((formats & (BarcodeFormats.Ean13 | BarcodeFormats.UpcA)) != 0)
        {
            types |= AVMetadataObjectType.EAN13Code;
        }

        if ((formats & BarcodeFormats.Ean8) != 0)
        {
            types |= AVMetadataObjectType.EAN8Code;
        }

        if ((formats & BarcodeFormats.Itf) != 0)
        {
            types |= AVMetadataObjectType.Interleaved2of5Code | AVMetadataObjectType.ITF14Code;
        }

        if ((formats & BarcodeFormats.UpcE) != 0)
        {
            types |= AVMetadataObjectType.UPCECode;
        }

        return types;
    }

    /// <summary>The format of a single AVFoundation metadata type, or <see cref="BarcodeFormats.None"/>.</summary>
    public static BarcodeFormats FromMetadataType(AVMetadataObjectType type) => type switch
    {
        AVMetadataObjectType.QRCode => BarcodeFormats.QrCode,
        AVMetadataObjectType.AztecCode => BarcodeFormats.Aztec,
        AVMetadataObjectType.DataMatrixCode => BarcodeFormats.DataMatrix,
        AVMetadataObjectType.PDF417Code => BarcodeFormats.Pdf417,
        AVMetadataObjectType.Code128Code => BarcodeFormats.Code128,
        AVMetadataObjectType.Code39Code or AVMetadataObjectType.Code39Mod43Code => BarcodeFormats.Code39,
        AVMetadataObjectType.Code93Code => BarcodeFormats.Code93,
        AVMetadataObjectType.EAN13Code => BarcodeFormats.Ean13,
        AVMetadataObjectType.EAN8Code => BarcodeFormats.Ean8,
        AVMetadataObjectType.Interleaved2of5Code or AVMetadataObjectType.ITF14Code => BarcodeFormats.Itf,
        AVMetadataObjectType.UPCECode => BarcodeFormats.UpcE,
        _ when IsCodabar(type) => BarcodeFormats.Codabar,
        _ => BarcodeFormats.None,
    };

    /// <summary>
    /// Applies the UPC-A convention and the caller's format filter. Returns <see langword="null"/> when the code is not wanted.
    /// </summary>
    public static (BarcodeFormats Format, string? Value)? Normalize(BarcodeFormats detected, string? value, BarcodeFormats requested)
    {
        if (detected == BarcodeFormats.Ean13 && value is { Length: 13 } && value[0] == '0' && value.All(char.IsAsciiDigit) &&
            (requested & BarcodeFormats.UpcA) != 0)
        {
            return (BarcodeFormats.UpcA, value[1..]);
        }

        if (detected == BarcodeFormats.None || (requested & detected) == 0)
        {
            return null;
        }

        return (detected, value);
    }

    /// <summary>
    /// Converts a Vision bounding box (normalized, origin at the bottom-left) into pixel coordinates with the origin at the
    /// top-left of an image of <paramref name="width"/> x <paramref name="height"/> pixels.
    /// </summary>
    public static BarcodeBounds ToPixelBounds(double x, double y, double boxWidth, double boxHeight, double width, double height) =>
        new(x * width, (1 - y - boxHeight) * height, boxWidth * width, boxHeight * height);

    private static bool IsCodabar(AVMetadataObjectType type) =>
        (OperatingSystem.IsIOSVersionAtLeast(15, 4) || OperatingSystem.IsMacCatalystVersionAtLeast(15, 4)) && type == AVMetadataObjectType.CodabarCode;
}
