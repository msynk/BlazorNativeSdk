using System.Runtime.Versioning;
using BlazorNativeSdk.Devices;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk.Storage;

/// <summary>
/// <see cref="IAppStorage"/> for desktop hosts, based on the user's profile folders:
/// <c>%LOCALAPPDATA%\{AppId}</c> on Windows, <c>~/Library/Application Support/{AppId}</c> on macOS and the XDG directories on Linux.
/// </summary>
[UnsupportedOSPlatform("browser")]
public sealed class PhysicalAppStorage : IAppStorage
{
    private readonly string _appId;

    /// <summary>Creates the storage provider.</summary>
    public PhysicalAppStorage(IOptions<NativeSdkOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _appId = SanitizeFolderName(options.Value.AppId);
    }

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.AppStorage;

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Available(Capability, "physical"));

    /// <inheritdoc />
    public NativeResult<string> GetDirectory(AppDirectory directory)
    {
        try
        {
            var path = directory switch
            {
                AppDirectory.AppData => Path.Combine(GetDataRoot(), _appId),
                AppDirectory.Cache => Path.Combine(GetCacheRoot(), _appId),
                AppDirectory.Temp => Path.Combine(Path.GetTempPath(), _appId),
                AppDirectory.Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.Create),
                AppDirectory.Downloads => GetDownloadsFolder(),
                _ => throw new ArgumentOutOfRangeException(nameof(directory), directory, null),
            };

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

    internal static string SanitizeFolderName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    private static string GetDataRoot()
    {
        if (OperatingSystem.IsLinux())
        {
            return Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
    }

    private static string GetCacheRoot()
    {
        if (OperatingSystem.IsLinux())
        {
            return Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "Cache");
    }

    private static string GetDownloadsFolder()
    {
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("XDG_DOWNLOAD_DIR") is { Length: > 0 } xdg)
        {
            return xdg;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}
