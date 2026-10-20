namespace BlazorNativeSdk.WindowsPlatform.Interop;

/// <summary>Detects whether the process runs with MSIX package identity.</summary>
internal static unsafe class PackageInfo
{
    private static readonly Lazy<bool> s_isPackaged = new(DetectPackaged);

    /// <summary><see langword="true"/> when the process has package identity (MSIX, sparse packages).</summary>
    public static bool IsPackaged => s_isPackaged.Value;

    private static bool DetectPackaged()
    {
        uint length = 0;
        var result = NativeMethods.GetCurrentPackageFullName(&length, null);
        return result != Win32.APPMODEL_ERROR_NO_PACKAGE;
    }
}
