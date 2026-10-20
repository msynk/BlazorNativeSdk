namespace BlazorNativeSdk.Wpf;

/// <summary>Shared constants of the WPF host integration.</summary>
internal static class WpfAdapter
{
    /// <summary>The adapter name reported in <see cref="CapabilityStatus.Adapter"/>.</summary>
    public const string Name = "wpf";

    /// <summary>The host description, for example <c>WPF on Windows 10.0.26100</c>.</summary>
    public static NativeHostInfo CreateHostInfo()
    {
        var version = Environment.OSVersion.Version;
        return new NativeHostInfo
        {
            Platform = NativePlatform.Windows,
            Host = NativeHostKind.Wpf,
            FormFactor = DeviceFormFactor.Desktop,
            OperatingSystemVersion = version,
            Description = $"WPF on Windows {version}",
        };
    }
}
