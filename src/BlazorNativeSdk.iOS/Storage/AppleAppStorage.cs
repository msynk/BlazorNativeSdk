using BlazorNativeSdk.Devices;
using Microsoft.Maui.Storage;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary><see cref="IAppStorage"/> for the iOS / Mac Catalyst app container.</summary>
/// <remarks>
/// <list type="table">
/// <listheader><term>Directory</term><description>Location</description></listheader>
/// <item><term><see cref="AppDirectory.AppData"/></term><description><c>Library</c> (Essentials <c>FileSystem.AppDataDirectory</c>): private, backed up to iCloud/Finder.</description></item>
/// <item><term><see cref="AppDirectory.Cache"/></term><description><c>Library/Caches</c>: not backed up, purged under storage pressure.</description></item>
/// <item><term><see cref="AppDirectory.Temp"/></term><description><c>tmp</c> (<c>NSTemporaryDirectory()</c>): purged when the app is not running.</description></item>
/// <item><term><see cref="AppDirectory.Documents"/></term><description><c>Documents</c>: backed up; visible in the Files app when Info.plist sets <c>UIFileSharingEnabled</c> and <c>LSSupportsOpeningDocumentsInPlace</c>.</description></item>
/// <item><term><see cref="AppDirectory.Downloads"/></term><description>Unsupported: apps cannot address the user's Downloads folder. Save through <c>IFilePickerService.SaveFileAsync</c> instead.</description></item>
/// </list>
/// </remarks>
public sealed class AppleAppStorage : IAppStorage
{
    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.AppStorage;

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Available(Capability, AppleHost.AdapterName));

    /// <inheritdoc />
    public NativeResult<string> GetDirectory(AppDirectory directory)
    {
        try
        {
            string? path = directory switch
            {
                AppDirectory.AppData => FileSystem.AppDataDirectory,
                AppDirectory.Cache => FileSystem.CacheDirectory,
                AppDirectory.Temp => AppleFiles.TemporaryDirectory,
                AppDirectory.Documents => NSFileManager.DefaultManager.GetUrls(NSSearchPathDirectory.DocumentDirectory, NSSearchPathDomain.User).FirstOrDefault()?.Path,
                AppDirectory.Downloads => null,
                _ => throw new ArgumentOutOfRangeException(nameof(directory), directory, null),
            };

            if (directory == AppDirectory.Downloads)
            {
                return NativeError.Unsupported(Capability,
                    "iOS and Mac Catalyst apps cannot address the user's Downloads folder. Use IFilePickerService.SaveFileAsync to let the user choose a location.");
            }

            if (string.IsNullOrEmpty(path))
            {
                return NativeError.Failed(Capability, NativeErrorCodes.NotFound, $"The {directory} directory could not be resolved.");
            }

            Directory.CreateDirectory(path);
            return path;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, ex.Message, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return NativeError.FromException(Capability, ex);
        }
    }
}
