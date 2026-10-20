using BlazorNativeSdk.Devices;
using Microsoft.Extensions.Logging;
using Windows.Foundation.Metadata;
using Windows.Security.Authorization.AppCapabilityAccess;
using Windows.System;

namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>Maps capability-access results to permission states (pure, unit-tested).</summary>
internal static class PermissionMapper
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.18362.0")]
    public static PermissionStatus Map(AppCapabilityAccessStatus status) => status switch
    {
        AppCapabilityAccessStatus.Allowed => PermissionStatus.Granted,
        AppCapabilityAccessStatus.DeniedByUser => PermissionStatus.Denied,
        AppCapabilityAccessStatus.DeniedBySystem => PermissionStatus.Restricted,
        AppCapabilityAccessStatus.NotDeclaredByApp or AppCapabilityAccessStatus.UserPromptRequired => PermissionStatus.NotDetermined,
        _ => PermissionStatus.Unknown,
    };

    /// <summary>The Settings page that controls a permission.</summary>
    public static Uri SettingsUri(string? permissionName) => permissionName switch
    {
        "camera" => new Uri("ms-settings:privacy-webcam"),
        "microphone" => new Uri("ms-settings:privacy-microphone"),
        "notifications" => new Uri("ms-settings:notifications"),
        _ => new Uri("ms-settings:privacy"),
    };
}

/// <summary>
/// <see cref="IPermissionService"/> for Windows. Camera and microphone use the privacy consent state
/// (<c>AppCapability.CheckAccess</c>, Windows 10 1903+); notifications use the toast setting; storage, photos, biometrics
/// and clipboard are not gated by runtime permissions on Windows.
/// </summary>
/// <remarks>
/// Desktop apps have no API to show a permission prompt: Windows asks for camera/microphone consent the first time a
/// capture device is opened, and toast permission is a Settings switch. <see cref="RequestAsync"/> therefore returns the
/// current state; use <see cref="OpenAppSettingsAsync"/> to send the user to the relevant Settings page (the page of the
/// most recently denied permission, else the privacy overview).
/// </remarks>
internal sealed partial class WindowsPermissionService : IPermissionService
{
    private readonly WindowsNotificationService _notifications;
    private readonly ILogger _logger;
    private volatile string? _lastDenied;

    public WindowsPermissionService(WindowsNotificationService notifications, ILogger<WindowsPermissionService> logger)
    {
        _notifications = notifications;
        _logger = logger;
    }

    public CapabilityId Capability => NativeCapabilities.Permissions;

    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Available(Capability, WindowsAdapter.Name));

    public ValueTask<NativeResult<PermissionStatus>> CheckAsync(NativePermission permission, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = Check(permission);
        if (result.IsSuccess && result.Value is PermissionStatus.Denied or PermissionStatus.Restricted)
        {
            _lastDenied = permission.Name;
        }

        return ValueTask.FromResult(result);
    }

    public ValueTask<NativeResult<PermissionStatus>> RequestAsync(NativePermission permission, CancellationToken cancellationToken = default) =>
        CheckAsync(permission, cancellationToken);

    public ValueTask<NativeResult<bool>> ShouldShowRationaleAsync(NativePermission permission, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(NativeResult.Success(false));

    public async ValueTask<NativeResult> OpenAppSettingsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = PermissionMapper.SettingsUri(_lastDenied);
        try
        {
            var launched = await Launcher.LaunchUriAsync(uri).AsTask(cancellationToken).ConfigureAwait(false);
            return launched
                ? NativeResult.Ok
                : NativeError.Failed(Capability, NativeErrorCodes.PlatformError, $"Windows could not open '{uri}'.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return NativeError.FromException(Capability, ex);
        }
    }

    private NativeResult<PermissionStatus> Check(NativePermission permission)
    {
        switch (permission.Name)
        {
            case "camera":
                return CheckCapability("webcam");
            case "microphone":
                return CheckCapability("microphone");
            case "notifications":
                return _notifications.GetPermissionStatus();
            case "photos":
            case "photos-add-only":
            case "biometrics":
            case "storage-read":
            case "storage-write":
            case "clipboard-read":
            case "exact-alarms":
                return PermissionStatus.NotRequired;
            default:
                return NativeError.Unsupported(Capability, $"Permission '{permission}' does not exist on Windows.");
        }
    }

    private NativeResult<PermissionStatus> CheckCapability(string capabilityName)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362) ||
            !ApiInformation.IsTypePresent("Windows.Security.Authorization.AppCapabilityAccess.AppCapability"))
        {
            // Before Windows 10 1903 there is no per-app consent for desktop apps.
            return PermissionStatus.NotRequired;
        }

        try
        {
            return PermissionMapper.Map(AppCapability.Create(capabilityName).CheckAccess());
        }
        catch (Exception ex)
        {
            LogCheckFailed(_logger, capabilityName, ex);
            return PermissionStatus.Unknown;
        }
    }

    [LoggerMessage(EventId = 3851, Level = LogLevel.Warning, Message = "The consent state of capability {Capability} could not be read.")]
    private static partial void LogCheckFailed(ILogger logger, string capability, Exception exception);
}
