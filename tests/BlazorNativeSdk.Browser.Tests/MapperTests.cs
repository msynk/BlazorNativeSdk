using BlazorNativeSdk.Browser;
using BlazorNativeSdk.Browser.Interop;
using BlazorNativeSdk.Browser.Services;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;
using Microsoft.JSInterop;

namespace BlazorNativeSdk.Browser.Tests;

public sealed class BrowserErrorMapperTests
{
    private static readonly CapabilityId s_capability = NativeCapabilities.Camera;

    [Theory]
    [InlineData("AbortError", NativeResultStatus.Cancelled, NativeErrorCodes.Cancelled)]
    [InlineData("NotAllowedError", NativeResultStatus.PermissionDenied, NativeErrorCodes.PermissionDenied)]
    [InlineData("SecurityError", NativeResultStatus.PermissionDenied, NativeErrorCodes.PermissionRestricted)]
    [InlineData("NotFoundError", NativeResultStatus.Failed, NativeErrorCodes.NotFound)]
    [InlineData("NotSupportedError", NativeResultStatus.Unsupported, NativeErrorCodes.NotSupported)]
    [InlineData("NotReadableError", NativeResultStatus.Unavailable, NativeErrorCodes.HardwareUnavailable)]
    [InlineData("OverconstrainedError", NativeResultStatus.Unavailable, NativeErrorCodes.HardwareUnavailable)]
    [InlineData("InvalidStateError", NativeResultStatus.Failed, NativeErrorCodes.Conflict)]
    [InlineData("TypeError", NativeResultStatus.Failed, NativeErrorCodes.InvalidArgument)]
    [InlineData("DataError", NativeResultStatus.Failed, NativeErrorCodes.InvalidArgument)]
    [InlineData("QuotaExceededError", NativeResultStatus.Failed, NativeErrorCodes.TooLarge)]
    [InlineData("TimeoutError", NativeResultStatus.Failed, NativeErrorCodes.Timeout)]
    [InlineData("InsecureContextError", NativeResultStatus.Unavailable, NativeErrorCodes.InsecureContext)]
    [InlineData("UserGestureRequiredError", NativeResultStatus.Unavailable, NativeErrorCodes.UserGestureRequired)]
    [InlineData("SomethingElse", NativeResultStatus.Failed, NativeErrorCodes.PlatformError)]
    public void Maps_dom_exception_names(string name, NativeResultStatus status, string code)
    {
        var error = BrowserErrorMapper.Map(s_capability, new BrowserJsError { Name = name, Message = "m" });

        Assert.Equal(status, error.Status);
        Assert.Equal(code, error.Code);
        Assert.Equal(s_capability, error.Capability);
        Assert.Equal(name, error.Details![BrowserErrorMapper.DomExceptionDetail]);
    }

    [Fact]
    public void Mapping_is_deterministic()
    {
        var input = new BrowserJsError { Name = "NotAllowedError", Message = "x", PermissionState = "denied" };
        Assert.Equal(
            BrowserErrorMapper.Map(s_capability, input, NativePermissions.Camera).ToString(),
            BrowserErrorMapper.Map(s_capability, input, NativePermissions.Camera).ToString());
    }

    [Fact]
    public void NotAllowed_without_user_activation_requires_a_gesture()
    {
        var error = BrowserErrorMapper.Map(NativeCapabilities.Share, new BrowserJsError { Name = "NotAllowedError", UserActivation = false });

        Assert.Equal(NativeResultStatus.Unavailable, error.Status);
        Assert.Equal(NativeErrorCodes.UserGestureRequired, error.Code);
    }

    [Fact]
    public void NotAllowed_with_permission_reports_permission_and_state()
    {
        var denied = BrowserErrorMapper.Map(s_capability, new BrowserJsError { Name = "NotAllowedError", PermissionState = "denied" }, NativePermissions.Camera);
        var dismissed = BrowserErrorMapper.Map(s_capability, new BrowserJsError { Name = "NotAllowedError", PermissionState = "prompt" }, NativePermissions.Camera);
        var unknown = BrowserErrorMapper.Map(s_capability, new BrowserJsError { Name = "NotAllowedError", UserActivation = true }, NativePermissions.Camera);

        Assert.Equal(NativePermissions.Camera, denied.Permission);
        Assert.Equal(PermissionStatus.Denied, denied.PermissionStatus);
        Assert.Equal(PermissionStatus.NotDetermined, dismissed.PermissionStatus);
        Assert.Equal(PermissionStatus.Denied, unknown.PermissionStatus);
    }

    [Fact]
    public void NotFound_for_media_means_no_device()
    {
        var error = BrowserErrorMapper.Map(s_capability, new BrowserJsError { Name = "NotFoundError" }, NativePermissions.Camera);

        Assert.Equal(NativeResultStatus.Unavailable, error.Status);
        Assert.Equal(NativeErrorCodes.HardwareUnavailable, error.Code);
    }

    [Fact]
    public void SecurityError_is_restricted_by_policy()
    {
        var error = BrowserErrorMapper.Map(s_capability, new BrowserJsError { Name = "SecurityError" }, NativePermissions.Camera);

        Assert.Equal(PermissionStatus.Restricted, error.PermissionStatus);
        Assert.Equal(NativeErrorCodes.PermissionRestricted, error.Code);
    }

    [Fact]
    public void Maps_interop_infrastructure_exceptions()
    {
        Assert.Equal(NativeErrorCodes.HostNotReady, BrowserErrorMapper.MapInteropException(s_capability, new JSDisconnectedException("gone")).Code);
        Assert.Equal(NativeErrorCodes.HostNotReady, BrowserErrorMapper.MapInteropException(s_capability, new InvalidOperationException("prerendering")).Code);
        Assert.Equal(NativeErrorCodes.Timeout, BrowserErrorMapper.MapInteropException(s_capability, new TaskCanceledException()).Code);
        Assert.Equal(NativeErrorCodes.PlatformError, BrowserErrorMapper.MapInteropException(s_capability, new JSException("boom")).Code);
        Assert.Equal(NativeResultStatus.Unavailable, BrowserErrorMapper.MapInteropException(s_capability, new JSDisconnectedException("gone")).Status);
    }

    [Theory]
    [InlineData("granted", PermissionStatus.Granted)]
    [InlineData("denied", PermissionStatus.Denied)]
    [InlineData("prompt", PermissionStatus.NotDetermined)]
    [InlineData("default", PermissionStatus.NotDetermined)]
    [InlineData(null, PermissionStatus.Unknown)]
    [InlineData("weird", PermissionStatus.Unknown)]
    public void Maps_permission_states(string? state, PermissionStatus expected) =>
        Assert.Equal(expected, BrowserErrorMapper.ToPermissionStatus(state));
}

public sealed class BarcodeFormatMapperTests
{
    [Fact]
    public void Maps_requested_formats_to_detector_names()
    {
        Assert.Equal(["qr_code", "ean_13"], BarcodeFormatMapper.ToDetectorNames(BarcodeFormats.QrCode | BarcodeFormats.Ean13));
        Assert.Equal(13, BarcodeFormatMapper.ToDetectorNames(BarcodeFormats.All).Count);
        Assert.Empty(BarcodeFormatMapper.ToDetectorNames(BarcodeFormats.None));
    }

    [Theory]
    [InlineData(BarcodeFormats.QrCode, "qr_code")]
    [InlineData(BarcodeFormats.Aztec, "aztec")]
    [InlineData(BarcodeFormats.DataMatrix, "data_matrix")]
    [InlineData(BarcodeFormats.Pdf417, "pdf417")]
    [InlineData(BarcodeFormats.Code128, "code_128")]
    [InlineData(BarcodeFormats.Code39, "code_39")]
    [InlineData(BarcodeFormats.Code93, "code_93")]
    [InlineData(BarcodeFormats.Codabar, "codabar")]
    [InlineData(BarcodeFormats.Ean13, "ean_13")]
    [InlineData(BarcodeFormats.Ean8, "ean_8")]
    [InlineData(BarcodeFormats.Itf, "itf")]
    [InlineData(BarcodeFormats.UpcA, "upc_a")]
    [InlineData(BarcodeFormats.UpcE, "upc_e")]
    public void Round_trips_every_format(BarcodeFormats format, string name)
    {
        Assert.Equal([name], BarcodeFormatMapper.ToDetectorNames(format));
        Assert.Equal(format, BarcodeFormatMapper.FromDetectorName(name));
    }

    [Fact]
    public void Intersects_with_supported_formats()
    {
        var names = BarcodeFormatMapper.Intersect(BarcodeFormats.TwoDimensional, ["qr_code", "ean_13", "aztec"]);

        Assert.Equal(["qr_code", "aztec"], names);
        Assert.Empty(BarcodeFormatMapper.Intersect(BarcodeFormats.Linear, ["qr_code"]));
    }

    [Fact]
    public void Unknown_formats_are_ignored()
    {
        Assert.Equal(BarcodeFormats.None, BarcodeFormatMapper.FromDetectorName("unknown"));
        Assert.Null(BarcodeFormatMapper.ToBarcode(new BarcodeDto { Format = "unknown", RawValue = "x" }));
        Assert.Equal(BarcodeFormats.QrCode | BarcodeFormats.UpcA, BarcodeFormatMapper.FromDetectorNames(["qr_code", "upc_a", "unknown"]));
    }

    [Fact]
    public void Maps_bounding_boxes()
    {
        var barcode = BarcodeFormatMapper.ToBarcode(new BarcodeDto { Format = "qr_code", RawValue = "hello", X = 1, Y = 2, Width = 3, Height = 4 });

        Assert.NotNull(barcode);
        Assert.Equal("hello", barcode.RawValue);
        Assert.Equal(new BarcodeBounds(1, 2, 3, 4), barcode.Bounds);
        Assert.Null(BarcodeFormatMapper.ToBarcode(new BarcodeDto { Format = "qr_code" })!.Bounds);
    }
}

public sealed class OtherMapperTests
{
    [Theory]
    [InlineData("wifi", ConnectionType.WiFi)]
    [InlineData("cellular", ConnectionType.Cellular)]
    [InlineData("ethernet", ConnectionType.Ethernet)]
    [InlineData("bluetooth", ConnectionType.Bluetooth)]
    [InlineData("wimax", ConnectionType.Other)]
    public void Maps_connection_types(string type, ConnectionType expected) =>
        Assert.Equal(expected, ConnectivityMapper.ToConnectionType(type));

    [Fact]
    public void Maps_connectivity_snapshots()
    {
        var now = DateTimeOffset.UnixEpoch;
        var online = ConnectivityMapper.ToSnapshot(new ConnectivityDto { OnLine = true, Type = "cellular", SaveData = true }, now);
        var offline = ConnectivityMapper.ToSnapshot(new ConnectivityDto { OnLine = false, Type = "none" }, now);
        var unknownType = ConnectivityMapper.ToSnapshot(new ConnectivityDto { OnLine = true }, now);

        Assert.Equal(NetworkAccess.Internet, online.Access);
        Assert.Equal([ConnectionType.Cellular], online.ConnectionTypes);
        Assert.True(online.IsMetered);
        Assert.True(online.IsDataSaverEnabled);
        Assert.Equal(NetworkAccess.None, offline.Access);
        Assert.Empty(offline.ConnectionTypes);
        Assert.Empty(unknownType.ConnectionTypes);
        Assert.Null(unknownType.IsMetered);
        Assert.Null(unknownType.IsDataSaverEnabled);
    }

    [Theory]
    [InlineData("active", AppLifecycleState.Active)]
    [InlineData("inactive", AppLifecycleState.Inactive)]
    [InlineData("background", AppLifecycleState.Background)]
    [InlineData("terminating", AppLifecycleState.Terminating)]
    public void Maps_lifecycle_states(string state, AppLifecycleState expected) =>
        Assert.Equal(expected, LifecycleStateMapper.ToState(state));

    [Fact]
    public void Maps_media_options()
    {
        Assert.Equal("user", BrowserMediaMapper.ToFacingMode(CameraFacing.Front));
        Assert.Equal("environment", BrowserMediaMapper.ToFacingMode(CameraFacing.Rear));
        Assert.Null(BrowserMediaMapper.ToFacingMode(CameraFacing.Default));
        Assert.Equal("image/*", BrowserMediaMapper.ToAccept(MediaKinds.Image));
        Assert.Equal("image/*,video/*", BrowserMediaMapper.ToAccept(MediaKinds.Any));
        Assert.Equal(1.0, BrowserMediaMapper.ToCanvasQuality(250));
        Assert.Equal(0.01, BrowserMediaMapper.ToCanvasQuality(0));
        Assert.Equal("image/*,.png,.jpg,application/pdf,.pdf", BrowserMediaMapper.ToAccept([new FileTypeFilter("Images", ["image/*"], [".png", ".jpg"]), FileTypeFilter.Pdf]));
        Assert.Null(BrowserMediaMapper.FromJsTimestamp(double.NaN));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000), BrowserMediaMapper.FromJsTimestamp(1_700_000_000_000));
    }

    [Theory]
    [InlineData("web+myapp", true)]
    [InlineData("mailto", true)]
    [InlineData("web+", false)]
    [InlineData("web+My", false)]
    [InlineData("web+my-app", false)]
    [InlineData("https", false)]
    [InlineData("myapp", false)]
    public void Validates_protocol_handler_schemes(string scheme, bool valid) =>
        Assert.Equal(valid, BrowserProtocolHandlers.IsValidScheme(scheme));

    [Fact]
    public void Maps_activations()
    {
        var now = DateTimeOffset.UnixEpoch;
        var link = BrowserActivationMapper.FromProtocolLink("web+myapp://orders/42", now);
        var click = BrowserActivationMapper.FromNotificationClick(
            new NotificationClickDto { Id = "n1", Action = "open", Route = "/orders/42", Data = new() { ["k"] = "v" } }, isColdStart: false, now);
        var launch = BrowserActivationMapper.FromLaunchTarget(new LaunchTarget("https://app.example/orders/7?x=1", "/orders/7?x=1", true), now);

        Assert.NotNull(link);
        Assert.Equal(ActivationKind.DeepLink, link.Kind);
        Assert.Equal("web+myapp", link.Uri!.Scheme);
        Assert.True(link.IsColdStart);
        Assert.Null(BrowserActivationMapper.FromProtocolLink("not a url", now));

        Assert.Equal(ActivationKind.Notification, click.Kind);
        Assert.Equal("n1", click.NotificationId);
        Assert.Equal("open", click.ActionId);
        Assert.Equal("/orders/42", click.Route);
        Assert.Equal("v", click.Data["k"]);

        Assert.NotNull(launch);
        Assert.Equal("/orders/7?x=1", launch.Route);
        Assert.True(launch.IsColdStart);
    }
}
