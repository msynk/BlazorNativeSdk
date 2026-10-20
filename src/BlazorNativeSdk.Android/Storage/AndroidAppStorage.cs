using BlazorNativeSdk.Devices;
using Microsoft.Maui.Storage;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>
/// <see cref="IAppStorage"/> for Android: app-private directories from Microsoft.Maui.Essentials <see cref="FileSystem"/>.
/// </summary>
/// <remarks>
/// <list type="table">
/// <item><term><see cref="AppDirectory.AppData"/></term><description><c>Context.getFilesDir()</c>; included in Auto Backup unless excluded.</description></item>
/// <item><term><see cref="AppDirectory.Cache"/></term><description><c>Context.getCacheDir()</c>; the OS may clear it under storage pressure.</description></item>
/// <item><term><see cref="AppDirectory.Temp"/></term><description><c>{cache}/tmp</c>.</description></item>
/// <item><term><see cref="AppDirectory.Documents"/>, <see cref="AppDirectory.Downloads"/></term><description>Unsupported: scoped storage
/// (Android 10+) gives apps no writable path to shared folders. Save user-visible files with
/// <see cref="IFilePickerService.SaveFileAsync"/> (Storage Access Framework) or MediaStore.</description></item>
/// </list>
/// </remarks>
public sealed class AndroidAppStorage : IAppStorage
{
    private const string AdapterName = "android";

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.AppStorage;

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Available(Capability, AdapterName));

    /// <inheritdoc />
    public NativeResult<string> GetDirectory(AppDirectory directory)
    {
        string path;
        switch (directory)
        {
            case AppDirectory.AppData:
                path = FileSystem.AppDataDirectory;
                break;
            case AppDirectory.Cache:
                path = FileSystem.CacheDirectory;
                break;
            case AppDirectory.Temp:
                path = Path.Combine(FileSystem.CacheDirectory, "tmp");
                break;
            case AppDirectory.Documents:
            case AppDirectory.Downloads:
                return NativeError.Unsupported(Capability,
                    $"Android has no app-writable '{directory}' path: scoped storage (Android 10+) restricts shared folders. " +
                    "Use IFilePickerService.SaveFileAsync (Storage Access Framework) or MediaStore to create user-visible files, " +
                    "and AppDirectory.AppData for private files.");
            default:
                return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, $"Unknown directory '{directory}'.");
        }

        try
        {
            Directory.CreateDirectory(path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return NativeError.FromException(Capability, ex);
        }
    }
}
