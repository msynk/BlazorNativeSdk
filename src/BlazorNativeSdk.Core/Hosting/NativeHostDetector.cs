namespace BlazorNativeSdk;

/// <summary>Best-effort detection of the current host when no adapter declared it.</summary>
public static class NativeHostDetector
{
    /// <summary>Detects the platform from the runtime.</summary>
    public static NativePlatform DetectPlatform()
    {
        if (OperatingSystem.IsBrowser())
        {
            return NativePlatform.Browser;
        }

        if (OperatingSystem.IsAndroid())
        {
            return NativePlatform.Android;
        }

        if (OperatingSystem.IsMacCatalyst())
        {
            return NativePlatform.MacCatalyst;
        }

        if (OperatingSystem.IsIOS())
        {
            return NativePlatform.iOS;
        }

        if (OperatingSystem.IsWindows())
        {
            return NativePlatform.Windows;
        }

        if (OperatingSystem.IsMacOS())
        {
            return NativePlatform.MacOS;
        }

        return OperatingSystem.IsLinux() ? NativePlatform.Linux : NativePlatform.Unknown;
    }

    /// <summary>Creates host information from the runtime.</summary>
    public static NativeHostInfo Detect(NativeHostKind host = NativeHostKind.Unknown)
    {
        var platform = DetectPlatform();
        if (host == NativeHostKind.Unknown && platform == NativePlatform.Browser)
        {
            host = NativeHostKind.WebAssembly;
        }

        return new NativeHostInfo
        {
            Platform = platform,
            Host = host,
            FormFactor = platform switch
            {
                NativePlatform.Windows or NativePlatform.MacOS or NativePlatform.Linux or NativePlatform.MacCatalyst => DeviceFormFactor.Desktop,
                _ => DeviceFormFactor.Unknown,
            },
            OperatingSystemVersion = platform == NativePlatform.Browser ? null : Environment.OSVersion.Version,
        };
    }
}
