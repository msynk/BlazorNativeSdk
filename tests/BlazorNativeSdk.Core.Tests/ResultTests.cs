using BlazorNativeSdk.Devices;

namespace BlazorNativeSdk.Tests;

public sealed class ResultTests
{
    private static readonly CapabilityId Cap = NativeCapabilities.Camera;

    [Fact]
    public void Default_generic_result_is_a_failure_not_an_accidental_success()
    {
        NativeResult<int> result = default;

        Assert.False(result.IsSuccess);
        Assert.Equal(NativeResultStatus.Failed, result.Status);
        Assert.Equal(NativeErrorCodes.Uninitialized, result.Error!.Code);
    }

    [Fact]
    public void Default_non_generic_result_is_success()
    {
        NativeResult result = default;

        Assert.True(result.IsSuccess);
        Assert.Equal(NativeResultStatus.Success, result.Status);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Values_and_errors_convert_implicitly()
    {
        NativeResult<string> ok = "value";
        NativeResult<string> cancelled = NativeError.Cancelled(Cap);

        Assert.True(ok.IsSuccess);
        Assert.Equal("value", ok.Value);
        Assert.Equal(NativeResultStatus.Cancelled, cancelled.Status);
        Assert.Equal(Cap, cancelled.Error!.Capability);
    }

    [Fact]
    public void Map_bind_and_match_propagate_errors()
    {
        NativeResult<int> ok = 21;
        NativeResult<int> failed = NativeError.Unsupported(Cap, "nope");

        Assert.Equal(42, ok.Map(x => x * 2).Value);
        Assert.Equal(NativeResultStatus.Unsupported, failed.Map(x => x * 2).Status);
        Assert.Equal("42", ok.Bind(x => NativeResult.Success((x * 2).ToString(System.Globalization.CultureInfo.InvariantCulture))).Value);
        Assert.Equal("nope", failed.Match(_ => "ok", e => e.Message));
        Assert.Equal(-1, failed.GetValueOrDefault(-1));
    }

    [Fact]
    public void GetValueOrThrow_throws_a_typed_exception_carrying_the_error()
    {
        NativeResult<int> failed = NativeError.PermissionDenied(Cap, NativePermissions.Camera, PermissionStatus.Denied);

        var ex = Assert.Throws<NativeCapabilityException>(() => failed.GetValueOrThrow());

        Assert.Equal(NativeResultStatus.PermissionDenied, ex.Status);
        Assert.Equal(NativePermissions.Camera, ex.Error.Permission);
        Assert.Equal(PermissionStatus.Denied, ex.Error.PermissionStatus);
    }

    [Fact]
    public void Restricted_permission_uses_the_restricted_code()
    {
        var error = NativeError.PermissionDenied(Cap, NativePermissions.Camera, PermissionStatus.Restricted);

        Assert.Equal(NativeErrorCodes.PermissionRestricted, error.Code);
    }

    [Fact]
    public void Error_cannot_have_success_status()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeError(NativeResultStatus.Success, "x", "y"));
    }

    [Theory]
    [InlineData(typeof(PlatformNotSupportedException), NativeResultStatus.Unsupported)]
    [InlineData(typeof(NotSupportedException), NativeResultStatus.Unsupported)]
    [InlineData(typeof(UnauthorizedAccessException), NativeResultStatus.PermissionDenied)]
    [InlineData(typeof(OperationCanceledException), NativeResultStatus.Cancelled)]
    [InlineData(typeof(TimeoutException), NativeResultStatus.Failed)]
    [InlineData(typeof(InvalidOperationException), NativeResultStatus.Failed)]
    public void FromException_maps_conventional_exceptions(Type exceptionType, NativeResultStatus expected)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        var error = NativeError.FromException(Cap, exception);

        Assert.Equal(expected, error.Status);
        Assert.Same(exception, error.Exception);
    }

    [Fact]
    public void FromException_unwraps_capability_exceptions()
    {
        var original = NativeError.Unavailable(Cap, NativeErrorCodes.HardwareUnavailable, "no camera");

        Assert.Same(original, NativeError.FromException(Cap, original.ToException()));
    }

    [Fact]
    public async Task NativeOperation_converts_exceptions_but_rethrows_caller_cancellation()
    {
        var failed = await NativeOperation.TryAsync<int>(Cap, _ => throw new InvalidOperationException("boom"), TestContext.Current.CancellationToken);
        Assert.Equal(NativeResultStatus.Failed, failed.Status);
        Assert.Equal("boom", failed.Error!.Message);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await NativeOperation.TryAsync<int>(Cap, ct => Task.FromCanceled<int>(ct), cts.Token));
    }

    [Fact]
    public void Capability_ids_are_case_insensitive()
    {
        Assert.Equal(new CapabilityId("Camera"), NativeCapabilities.Camera);
        Assert.Equal("desktop.tray", NativeCapabilities.Tray.ToString());
    }

    [Fact]
    public void Capability_status_helpers()
    {
        var status = CapabilityStatus.Available(Cap, "test", "Video", CameraFeatures.Photo);

        Assert.True(status.IsAvailable);
        Assert.True(status.IsUsable);
        Assert.True(status.Supports("video"));
        Assert.False(status.Supports(CameraFeatures.Library));
        Assert.True(CapabilityStatus.RequiresPermission(Cap).IsUsable);
        Assert.False(CapabilityStatus.Unsupported(Cap).IsUsable);
        Assert.Equal(CapabilityAvailability.PermissionDenied,
            CapabilityStatus.FromError(NativeError.PermissionDenied(Cap, NativePermissions.Camera, PermissionStatus.Denied)).Availability);
    }

    [Fact]
    public async Task Native_files_enforce_size_limits()
    {
        await using var file = NativeFile.FromBytes("a.png", new byte[10]);

        Assert.Equal("image/png", file.ContentType);
        Assert.Equal(10, (await file.ReadAllBytesAsync(cancellationToken: TestContext.Current.CancellationToken)).Length);
        await Assert.ThrowsAsync<IOException>(async () => await file.OpenReadAsync(maxAllowedSize: 5, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void File_type_filter_normalizes_extensions_and_infers_mime_types()
    {
        var filter = FileTypeFilter.FromExtensions("Docs", "pdf", ".txt", ".unknown");

        Assert.Equal([".pdf", ".txt", ".unknown"], filter.Extensions);
        Assert.Equal(["application/pdf", "text/plain"], filter.MimeTypes);
    }
}
