using BlazorNativeSdk.Devices;

namespace BlazorNativeSdk.AndroidPlatform.Tests;

public sealed class BarcodeMapperTests
{
    // Every single BarcodeFormats flag and its ML Kit Barcode.FORMAT_* value.
    private static readonly (BarcodeFormats Format, int MlKit)[] s_singleFormats =
    [
        (BarcodeFormats.Code128, 1),
        (BarcodeFormats.Code39, 2),
        (BarcodeFormats.Code93, 4),
        (BarcodeFormats.Codabar, 8),
        (BarcodeFormats.DataMatrix, 16),
        (BarcodeFormats.Ean13, 32),
        (BarcodeFormats.Ean8, 64),
        (BarcodeFormats.Itf, 128),
        (BarcodeFormats.QrCode, 256),
        (BarcodeFormats.UpcA, 512),
        (BarcodeFormats.UpcE, 1024),
        (BarcodeFormats.Pdf417, 2048),
        (BarcodeFormats.Aztec, 4096),
    ];

    public static TheoryData<BarcodeFormats, int> SingleFormats
    {
        get
        {
            var data = new TheoryData<BarcodeFormats, int>();
            foreach (var (format, mlKit) in s_singleFormats)
            {
                data.Add(format, mlKit);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(SingleFormats))]
    public void Every_format_round_trips(BarcodeFormats format, int mlKit)
    {
        Assert.Equal([mlKit], AndroidBarcodeMapper.ToMlKit(format));
        Assert.Equal(format, AndroidBarcodeMapper.FromMlKit(mlKit));
    }

    [Fact]
    public void Every_single_format_is_mapped()
    {
        var singleFlags = Enum.GetValues<BarcodeFormats>().Where(f => f != BarcodeFormats.None && (f & (f - 1)) == 0);

        Assert.Equal(singleFlags.Order(), s_singleFormats.Select(f => f.Format).Order());
    }

    [Theory]
    [InlineData(0)]          // FORMAT_ALL_FORMATS is a request value, never a detected format
    [InlineData(-1)]         // FORMAT_UNKNOWN
    [InlineData(3)]
    [InlineData(8192)]
    public void Unknown_ML_Kit_formats_map_to_none(int mlKit) =>
        Assert.Equal(BarcodeFormats.None, AndroidBarcodeMapper.FromMlKit(mlKit));

    [Theory]
    [InlineData(BarcodeFormats.None)]
    [InlineData(BarcodeFormats.All)]
    public void No_filter_and_all_formats_request_every_format(BarcodeFormats formats) =>
        Assert.Equal([AndroidBarcodeMapper.MlKitAllFormats], AndroidBarcodeMapper.ToMlKit(formats));

    [Fact]
    public void Format_groups_map_to_their_members()
    {
        Assert.Equal([256, 4096, 16, 2048], AndroidBarcodeMapper.ToMlKit(BarcodeFormats.TwoDimensional));
        Assert.Equal(
            [1, 2, 4, 8, 32, 64, 128, 512, 1024],
            AndroidBarcodeMapper.ToMlKit(BarcodeFormats.Linear).Order());
        Assert.Equal([256, 32], AndroidBarcodeMapper.ToMlKit(BarcodeFormats.QrCode | BarcodeFormats.Ean13));
    }

    [Fact]
    public void Undefined_flags_alone_still_request_every_format() =>
        Assert.Equal([AndroidBarcodeMapper.MlKitAllFormats], AndroidBarcodeMapper.ToMlKit((BarcodeFormats)(1 << 20)));

    [Fact]
    public void The_scanner_cancellation_is_a_cancel()
    {
        var error = AndroidBarcodeMapper.MapScanError(AndroidBarcodeMapper.MlKitCodeScannerCancelled, null);

        Assert.Equal(NativeResultStatus.Cancelled, error.Status);
        Assert.Equal(NativeErrorCodes.Cancelled, error.Code);
        Assert.Equal(NativeCapabilities.Barcode, error.Capability);
    }

    [Theory]
    [InlineData(200, NativeResultStatus.Unavailable, AndroidErrorCodes.ModuleUnavailable)]         // CODE_SCANNER_UNAVAILABLE
    [InlineData(14, NativeResultStatus.Unavailable, AndroidErrorCodes.ModuleUnavailable)]          // UNAVAILABLE
    [InlineData(202, NativeResultStatus.PermissionDenied, NativeErrorCodes.PermissionDenied)]      // CODE_SCANNER_CAMERA_PERMISSION_NOT_GRANTED
    [InlineData(204, NativeResultStatus.Failed, NativeErrorCodes.Conflict)]                        // CODE_SCANNER_TASK_IN_PROGRESS
    [InlineData(207, NativeResultStatus.Unavailable, AndroidErrorCodes.PlayServicesUnavailable)]   // CODE_SCANNER_GOOGLE_PLAY_SERVICES_VERSION_TOO_OLD
    [InlineData(203, NativeResultStatus.Failed, NativeErrorCodes.PlatformError)]                   // CODE_SCANNER_APP_NAME_UNAVAILABLE
    [InlineData(13, NativeResultStatus.Failed, NativeErrorCodes.PlatformError)]                    // INTERNAL
    public void Maps_scanner_errors(int code, NativeResultStatus status, string errorCode)
    {
        var error = AndroidBarcodeMapper.MapScanError(code, "message");

        Assert.Equal(status, error.Status);
        Assert.Equal(errorCode, error.Code);
        Assert.Equal(NativeCapabilities.Barcode, error.Capability);
        Assert.Equal(code.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Details!["mlkit.code"]);
    }

    [Fact]
    public void A_denied_scanner_camera_reports_the_camera_permission()
    {
        var error = AndroidBarcodeMapper.MapScanError(AndroidBarcodeMapper.MlKitCodeScannerCameraPermissionNotGranted, null);

        Assert.Equal(NativePermissions.Camera, error.Permission);
        Assert.Equal(PermissionStatus.Denied, error.PermissionStatus);
    }

    [Fact]
    public void A_missing_module_explains_the_install_time_download()
    {
        var error = AndroidBarcodeMapper.MapScanError(AndroidBarcodeMapper.MlKitCodeScannerUnavailable, null);

        Assert.Contains("barcode_ui", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Scanner exploded", "Scanner exploded")]
    [InlineData(null, "The Google code scanner failed with code 99.")]
    [InlineData("  ", "The Google code scanner failed with code 99.")]
    public void Unknown_errors_keep_the_platform_message(string? message, string expected) =>
        Assert.Equal(expected, AndroidBarcodeMapper.MapScanError(99, message).Message);

    [Fact]
    public void Bounding_boxes_are_converted_to_position_and_size() =>
        Assert.Equal(new BarcodeBounds(10, 20, 90, 30), AndroidBarcodeMapper.ToBounds(10, 20, 100, 50));

    [Theory]
    [InlineData(10, 20, 10, 50)]
    [InlineData(10, 20, 100, 20)]
    [InlineData(100, 20, 10, 50)]
    public void Empty_or_inverted_boxes_have_no_bounds(int left, int top, int right, int bottom) =>
        Assert.Null(AndroidBarcodeMapper.ToBounds(left, top, right, bottom));
}
