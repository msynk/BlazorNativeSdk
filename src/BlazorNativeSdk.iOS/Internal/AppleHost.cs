using ObjCRuntime;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>Facts about the current Apple platform.</summary>
internal static class AppleHost
{
    /// <summary>The adapter name reported in statuses and activations.</summary>
    public static string AdapterName => OperatingSystem.IsMacCatalyst() ? "maccatalyst" : "ios";

    /// <summary>The platform.</summary>
    public static NativePlatform Platform => OperatingSystem.IsMacCatalyst() ? NativePlatform.MacCatalyst : NativePlatform.iOS;

    /// <summary><see langword="true"/> in the iOS simulator.</summary>
#if IOS
    public static bool IsSimulator => Runtime.Arch == Arch.SIMULATOR;
#else
    public static bool IsSimulator => false;
#endif

    /// <summary><see langword="true"/> for an iPhone/iPad app running on an Apple silicon Mac ("Designed for iPad").</summary>
    public static bool IsiOSAppOnMac => !OperatingSystem.IsMacCatalyst() && NSProcessInfo.ProcessInfo.IsiOSApplicationOnMac;

    /// <summary>The operating system version (the macOS version on Mac Catalyst).</summary>
    public static Version OperatingSystemVersion
    {
        get
        {
            var version = NSProcessInfo.ProcessInfo.OperatingSystemVersion;
            return new Version((int)version.Major, (int)version.Minor, (int)version.PatchVersion);
        }
    }

    /// <summary>Creates the host description declared by <c>AddApple()</c>.</summary>
    public static NativeHostInfo Create(NativeHostKind hostKind)
    {
        var platform = Platform;
        var version = OperatingSystemVersion;
        var platformName = platform == NativePlatform.MacCatalyst ? "Mac Catalyst (macOS)" : "iOS";
        return new NativeHostInfo
        {
            Platform = platform,
            Host = hostKind,
            FormFactor = DetectFormFactor(),
            OperatingSystemVersion = version,
            Description = $"{hostKind} on {platformName} {version}{(IsSimulator ? " (simulator)" : string.Empty)}",
        };
    }

    private static DeviceFormFactor DetectFormFactor()
    {
        if (OperatingSystem.IsMacCatalyst() || IsiOSAppOnMac)
        {
            return DeviceFormFactor.Desktop;
        }

        // UIDevice is a UIKit object; only touch it on the main thread. Registration normally runs on the main thread
        // (MauiProgram.CreateMauiApp is called from FinishedLaunching).
        if (!NSThread.IsMain)
        {
            return DeviceFormFactor.Unknown;
        }

        return UIDevice.CurrentDevice.UserInterfaceIdiom switch
        {
            UIUserInterfaceIdiom.Phone => DeviceFormFactor.Phone,
            UIUserInterfaceIdiom.Pad => DeviceFormFactor.Tablet,
            UIUserInterfaceIdiom.Mac => DeviceFormFactor.Desktop,
            UIUserInterfaceIdiom.TV => DeviceFormFactor.TV,
            _ => DeviceFormFactor.Unknown,
        };
    }
}
