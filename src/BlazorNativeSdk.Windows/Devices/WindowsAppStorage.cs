using System.Runtime.InteropServices;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.WindowsPlatform.Interop;
using Microsoft.Extensions.Options;
using Windows.Storage;

namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>
/// <see cref="IAppStorage"/> for Windows. MSIX-packaged apps use their package folders (<c>ApplicationData.Current</c>
/// local, local-cache and temporary folders); unpackaged apps use <c>%LOCALAPPDATA%\{AppId}</c>, its <c>Cache</c>
/// subfolder and <c>%TEMP%\{AppId}</c>. Documents and Downloads resolve the user's known folders (including redirection).
/// </summary>
internal sealed class WindowsAppStorage : IAppStorage
{
    private static readonly Guid s_documentsFolderId = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
    private static readonly Guid s_downloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");
    private readonly string _appFolder;

    public WindowsAppStorage(IOptions<NativeSdkOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _appFolder = WindowsAdapter.ToFolderName(options.Value.AppId);
    }

    public CapabilityId Capability => NativeCapabilities.AppStorage;

    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Available(Capability, WindowsAdapter.Name));

    public NativeResult<string> GetDirectory(AppDirectory directory)
    {
        try
        {
            var path = directory switch
            {
                AppDirectory.AppData => GetAppDataRoot(_appFolder),
                AppDirectory.Cache => PackageInfo.IsPackaged
                    ? ApplicationData.Current.LocalCacheFolder.Path
                    : Path.Combine(GetAppDataRoot(_appFolder), "Cache"),
                AppDirectory.Temp => PackageInfo.IsPackaged
                    ? ApplicationData.Current.TemporaryFolder.Path
                    : Path.Combine(Path.GetTempPath(), _appFolder),
                AppDirectory.Documents => GetKnownFolder(s_documentsFolderId),
                AppDirectory.Downloads => GetKnownFolder(s_downloadsFolderId),
                _ => null,
            };

            if (path is null)
            {
                return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, $"Unknown app directory '{directory}'.");
            }

            Directory.CreateDirectory(path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            return NativeError.FromException(Capability, ex);
        }
    }

    /// <summary>The persistent app data folder: the package's local folder, or <c>%LOCALAPPDATA%\{appFolder}</c>.</summary>
    internal static string GetAppDataRoot(string appFolder) =>
        PackageInfo.IsPackaged
            ? ApplicationData.Current.LocalFolder.Path
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), appFolder);

    private static unsafe string GetKnownFolder(Guid folderId)
    {
        char* path = null;
        try
        {
            var hr = NativeMethods.SHGetKnownFolderPath(&folderId, Win32.KF_FLAG_CREATE, 0, &path);
            Marshal.ThrowExceptionForHR(hr);
            return new string(path);
        }
        finally
        {
            Marshal.FreeCoTaskMem((nint)path);
        }
    }
}
