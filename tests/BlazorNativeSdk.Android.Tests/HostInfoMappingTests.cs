namespace BlazorNativeSdk.AndroidPlatform.Tests;

public sealed class HostInfoMappingTests
{
    // Configuration.UI_MODE_TYPE_NORMAL = 1, _DESK = 2, _CAR = 3, _TELEVISION = 4, _APPLIANCE = 5, _WATCH = 6, _VR_HEADSET = 7.
    [Theory]
    [InlineData(4, 320, DeviceFormFactor.TV)]
    [InlineData(4, 960, DeviceFormFactor.TV)]
    [InlineData(6, 200, DeviceFormFactor.Watch)]
    [InlineData(1, 360, DeviceFormFactor.Phone)]
    [InlineData(1, 599, DeviceFormFactor.Phone)]
    [InlineData(1, 600, DeviceFormFactor.Tablet)]
    [InlineData(2, 800, DeviceFormFactor.Tablet)]
    [InlineData(3, 400, DeviceFormFactor.Phone)]
    [InlineData(0, 0, DeviceFormFactor.Phone)]
    public void Form_factor_comes_from_the_ui_mode_then_the_smallest_width(int uiModeType, int smallestWidthDp, DeviceFormFactor expected) =>
        Assert.Equal(expected, AndroidHostInfoMapping.MapFormFactor(uiModeType, smallestWidthDp));

    [Theory]
    [InlineData("14", "14.0")]
    [InlineData("8.1.0", "8.1.0")]
    [InlineData("7.0", "7.0")]
    [InlineData("15.0.1", "15.0.1")]
    public void Release_strings_parse_to_versions(string release, string expected) =>
        Assert.Equal(Version.Parse(expected), AndroidHostInfoMapping.ParseVersion(release));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("VanillaIceCream")]
    [InlineData("Baklava")]
    public void Preview_codenames_and_missing_values_have_no_version(string? release) =>
        Assert.Null(AndroidHostInfoMapping.ParseVersion(release));
}
