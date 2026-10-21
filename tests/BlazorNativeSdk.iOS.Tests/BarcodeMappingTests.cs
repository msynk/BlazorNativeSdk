using AVFoundation;
using BlazorNativeSdk.Devices;
using Vision;

namespace BlazorNativeSdk.ApplePlatform.Tests;

/// <summary>
/// Symbology mapping and the UPC-A convention from docs/adapters/ios.md: "UPC-A is reported as EAN-13 by Apple and normalized
/// when UPC-A is requested".
/// </summary>
public sealed class BarcodeMappingTests
{
    private static readonly BarcodeFormats[] s_singleFormats =
        [.. Enum.GetValues<BarcodeFormats>().Where(f => f != BarcodeFormats.None && (f & (f - 1)) == 0)];

    public static TheoryData<BarcodeFormats> SingleFormats => [.. s_singleFormats];

    [Theory]
    [MemberData(nameof(SingleFormats))]
    public void Every_requested_format_has_Vision_symbologies_that_map_back(BarcodeFormats format)
    {
        var symbologies = AppleBarcodeMapping.ToVisionSymbologies(format);

        Assert.NotEmpty(symbologies);
        // Apple has no UPC-A symbology: it is detected as EAN-13 and normalized afterwards.
        var expected = format == BarcodeFormats.UpcA ? BarcodeFormats.Ean13 : format;
        Assert.All(symbologies, symbology => Assert.Equal(expected, AppleBarcodeMapping.FromVision(symbology)));
    }

    [Fact]
    public void Symbology_families_are_requested_together()
    {
        Assert.Equal(
            [VNBarcodeSymbology.Code39, VNBarcodeSymbology.Code39Checksum, VNBarcodeSymbology.Code39FullAscii, VNBarcodeSymbology.Code39FullAsciiChecksum],
            AppleBarcodeMapping.ToVisionSymbologies(BarcodeFormats.Code39));
        Assert.Equal([VNBarcodeSymbology.Code93, VNBarcodeSymbology.Code93i], AppleBarcodeMapping.ToVisionSymbologies(BarcodeFormats.Code93));
        Assert.Equal(
            [VNBarcodeSymbology.I2OF5, VNBarcodeSymbology.I2OF5Checksum, VNBarcodeSymbology.Itf14],
            AppleBarcodeMapping.ToVisionSymbologies(BarcodeFormats.Itf));
    }

    [Fact]
    public void EAN_13_is_requested_once_for_EAN_13_and_UPC_A() =>
        Assert.Equal([VNBarcodeSymbology.Ean13], AppleBarcodeMapping.ToVisionSymbologies(BarcodeFormats.Ean13 | BarcodeFormats.UpcA));

    [Fact]
    public void All_formats_request_distinct_symbologies()
    {
        var symbologies = AppleBarcodeMapping.ToVisionSymbologies(BarcodeFormats.All);

        Assert.Equal(symbologies.Length, symbologies.Distinct().Count());
        Assert.Equal(18, symbologies.Length);
        Assert.Empty(AppleBarcodeMapping.ToVisionSymbologies(BarcodeFormats.None));
    }

    [Theory]
    [InlineData(VNBarcodeSymbology.MicroQR, BarcodeFormats.QrCode)]
    [InlineData(VNBarcodeSymbology.MicroPdf417, BarcodeFormats.Pdf417)]
    [InlineData(VNBarcodeSymbology.GS1DataBar, BarcodeFormats.None)]
    [InlineData(VNBarcodeSymbology.GS1DataBarExpanded, BarcodeFormats.None)]
    [InlineData(VNBarcodeSymbology.GS1DataBarLimited, BarcodeFormats.None)]
    [InlineData(VNBarcodeSymbology.MsiPlessey, BarcodeFormats.None)]
    public void Maps_detected_Vision_symbologies_without_a_request_counterpart(VNBarcodeSymbology symbology, BarcodeFormats expected) =>
        Assert.Equal(expected, AppleBarcodeMapping.FromVision(symbology));

    [Theory]
    [MemberData(nameof(SingleFormats))]
    public void Every_requested_format_has_metadata_types_that_map_back(BarcodeFormats format)
    {
        var types = AppleBarcodeMapping.ToMetadataTypes(format);
        if (format == BarcodeFormats.Codabar)
        {
            // AVCaptureMetadataOutput recognizes Codabar only from iOS / Mac Catalyst 15.4; never requested elsewhere.
            Assert.Equal(AVMetadataObjectType.None, types);
            return;
        }

        var expected = format == BarcodeFormats.UpcA ? BarcodeFormats.Ean13 : format;
        var flags = Enum.GetValues<AVMetadataObjectType>().Where(t => t != AVMetadataObjectType.None && types.HasFlag(t)).ToArray();
        Assert.NotEmpty(flags);
        Assert.All(flags, type => Assert.Equal(expected, AppleBarcodeMapping.FromMetadataType(type)));
    }

    [Fact]
    public void Metadata_type_families_are_requested_together()
    {
        Assert.Equal(AVMetadataObjectType.Code39Code | AVMetadataObjectType.Code39Mod43Code, AppleBarcodeMapping.ToMetadataTypes(BarcodeFormats.Code39));
        Assert.Equal(AVMetadataObjectType.Interleaved2of5Code | AVMetadataObjectType.ITF14Code, AppleBarcodeMapping.ToMetadataTypes(BarcodeFormats.Itf));
        Assert.Equal(AVMetadataObjectType.EAN13Code, AppleBarcodeMapping.ToMetadataTypes(BarcodeFormats.UpcA));
        Assert.Equal(AVMetadataObjectType.None, AppleBarcodeMapping.ToMetadataTypes(BarcodeFormats.None));
    }

    [Theory]
    [InlineData(AVMetadataObjectType.Face)]
    [InlineData(AVMetadataObjectType.MicroQRCode)]
    [InlineData(AVMetadataObjectType.GS1DataBarCode)]
    [InlineData(AVMetadataObjectType.CodabarCode)]    // only below iOS / Mac Catalyst 15.4, which this host is
    [InlineData(AVMetadataObjectType.None)]
    public void Unsupported_metadata_types_map_to_none(AVMetadataObjectType type) =>
        Assert.Equal(BarcodeFormats.None, AppleBarcodeMapping.FromMetadataType(type));

    [Theory]
    [InlineData(BarcodeFormats.UpcA)]
    [InlineData(BarcodeFormats.UpcA | BarcodeFormats.Ean13)]
    [InlineData(BarcodeFormats.All)]
    [InlineData(BarcodeFormats.Linear)]
    public void EAN_13_codes_with_a_leading_zero_are_UPC_A_when_UPC_A_is_requested(BarcodeFormats requested) =>
        Assert.Equal((BarcodeFormats.UpcA, "036000291452"), AppleBarcodeMapping.Normalize(BarcodeFormats.Ean13, "0036000291452", requested));

    [Fact]
    public void EAN_13_codes_stay_EAN_13_when_UPC_A_is_not_requested() =>
        Assert.Equal((BarcodeFormats.Ean13, "0036000291452"), AppleBarcodeMapping.Normalize(BarcodeFormats.Ean13, "0036000291452", BarcodeFormats.Ean13));

    [Fact]
    public void Real_EAN_13_codes_are_filtered_out_of_UPC_A_only_requests()
    {
        Assert.Null(AppleBarcodeMapping.Normalize(BarcodeFormats.Ean13, "4006381333931", BarcodeFormats.UpcA));
        Assert.Equal(
            (BarcodeFormats.Ean13, "4006381333931"),
            AppleBarcodeMapping.Normalize(BarcodeFormats.Ean13, "4006381333931", BarcodeFormats.UpcA | BarcodeFormats.Ean13));
    }

    [Theory]
    [InlineData("036000291452")]       // 12 digits
    [InlineData("00036000291452")]     // 14 digits
    [InlineData("0A36000291452")]      // not all digits
    [InlineData("０036000291452")]     // full-width zero is not an ASCII digit
    [InlineData(null)]
    public void Only_13_ASCII_digit_values_are_normalized(string? value) =>
        Assert.Equal((BarcodeFormats.Ean13, value), AppleBarcodeMapping.Normalize(BarcodeFormats.Ean13, value, BarcodeFormats.Ean13 | BarcodeFormats.UpcA));

    [Fact]
    public void Codes_that_were_not_requested_or_are_unknown_are_dropped()
    {
        Assert.Null(AppleBarcodeMapping.Normalize(BarcodeFormats.QrCode, "x", BarcodeFormats.Ean13));
        Assert.Null(AppleBarcodeMapping.Normalize(BarcodeFormats.None, "x", BarcodeFormats.All));
        Assert.Equal((BarcodeFormats.QrCode, "x"), AppleBarcodeMapping.Normalize(BarcodeFormats.QrCode, "x", BarcodeFormats.TwoDimensional));
    }

    [Fact]
    public void Vision_bounding_boxes_are_flipped_to_a_top_left_origin()
    {
        // Normalized box at (0.1, 0.2) with size 0.3 x 0.4, origin bottom-left, in a 1000 x 500 image.
        var bounds = AppleBarcodeMapping.ToPixelBounds(0.1, 0.2, 0.3, 0.4, 1000, 500);

        Assert.Equal(100, bounds.X, 9);
        Assert.Equal(200, bounds.Y, 9);
        Assert.Equal(300, bounds.Width, 9);
        Assert.Equal(200, bounds.Height, 9);
    }

    [Fact]
    public void A_full_image_box_covers_the_image()
    {
        var bounds = AppleBarcodeMapping.ToPixelBounds(0, 0, 1, 1, 640, 480);

        Assert.Equal(new BarcodeBounds(0, 0, 640, 480), bounds);
    }
}
