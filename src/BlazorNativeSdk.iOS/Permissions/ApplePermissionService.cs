using AVFoundation;
using Microsoft.Extensions.Logging;
using Photos;
using UserNotifications;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary><see cref="IPermissionService"/> for iOS and Mac Catalyst.</summary>
/// <remarks>
/// <list type="table">
/// <listheader><term>Permission</term><description>Platform mapping</description></listheader>
/// <item><term>camera / microphone</term><description><c>AVCaptureDevice</c> authorization; requires <c>NSCameraUsageDescription</c> / <c>NSMicrophoneUsageDescription</c>.</description></item>
/// <item><term>photos / photos-add-only</term><description><c>PHPhotoLibrary</c> read-write / add-only access; requires <c>NSPhotoLibraryUsageDescription</c> / <c>NSPhotoLibraryAddUsageDescription</c>. "Selected photos" access is <see cref="PermissionStatus.Limited"/>.</description></item>
/// <item><term>notifications</term><description><c>UNUserNotificationCenter</c> (alert, sound, badge). Provisional and ephemeral authorization are <see cref="PermissionStatus.Limited"/>.</description></item>
/// <item><term>biometrics</term><description>Derived from LocalAuthentication: <see cref="PermissionStatus.Denied"/> when the user denied Face ID, otherwise <see cref="PermissionStatus.NotRequired"/> (iOS asks on first use and offers no request API).</description></item>
/// <item><term>storage-read, storage-write, exact-alarms, clipboard-read</term><description><see cref="PermissionStatus.NotRequired"/>: iOS does not gate these operations behind a runtime permission (iOS 16+ shows a per-paste prompt instead).</description></item>
/// </list>
/// Requests never call a privacy-sensitive API whose usage description is missing; they return
/// <see cref="NativeResultStatus.Unavailable"/> with <see cref="NativeErrorCodes.MissingDeclaration"/> naming the key instead.
/// </remarks>
public sealed partial class ApplePermissionService : IPermissionService
{
    private const UNAuthorizationOptions RequestedNotificationOptions = UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound | UNAuthorizationOptions.Badge;
    private readonly ILogger<ApplePermissionService> _logger;

    /// <summary>Creates the service.</summary>
    public ApplePermissionService(ILogger<ApplePermissionService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.Permissions;

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Available(Capability, AppleHost.AdapterName, "open-settings"));

    /// <inheritdoc />
    public ValueTask<NativeResult<PermissionStatus>> CheckAsync(NativePermission permission, CancellationToken cancellationToken = default) =>
        NativeOperation.RunAsync<PermissionStatus>(Capability, async token =>
        {
            if (permission == NativePermissions.Camera)
            {
                return AppleCaptureAccess.Check(Capability, AVAuthorizationMediaType.Video);
            }

            if (permission == NativePermissions.Microphone)
            {
                return AppleCaptureAccess.Check(Capability, AVAuthorizationMediaType.Audio);
            }

            if (permission == NativePermissions.Photos || permission == NativePermissions.PhotosAddOnly)
            {
                var (level, key) = PhotoAccess(permission);
                return AppleInfoPlist.RequireUsageDescription(Capability, key, "the photo library") is { } missing
                    ? missing
                    : AppleStatusMapping.FromPhotos(PHPhotoLibrary.GetAuthorizationStatus(level));
            }

            if (permission == NativePermissions.Notifications)
            {
                var settings = await UNUserNotificationCenter.Current.GetNotificationSettingsAsync().WaitAsync(token).ConfigureAwait(false);
                return AppleStatusMapping.FromNotifications(settings.AuthorizationStatus);
            }

            if (permission == NativePermissions.Biometrics)
            {
                return CheckBiometrics();
            }

            return CheckOther(permission);
        }, cancellationToken);

    /// <inheritdoc />
    public ValueTask<NativeResult<PermissionStatus>> RequestAsync(NativePermission permission, CancellationToken cancellationToken = default) =>
        NativeOperation.RunAsync<PermissionStatus>(Capability, async token =>
        {
            if (permission == NativePermissions.Camera)
            {
                return await AppleCaptureAccess.RequestAsync(Capability, AVAuthorizationMediaType.Video, token).ConfigureAwait(false);
            }

            if (permission == NativePermissions.Microphone)
            {
                return await AppleCaptureAccess.RequestAsync(Capability, AVAuthorizationMediaType.Audio, token).ConfigureAwait(false);
            }

            if (permission == NativePermissions.Photos || permission == NativePermissions.PhotosAddOnly)
            {
                var (level, key) = PhotoAccess(permission);
                if (AppleInfoPlist.RequireUsageDescription(Capability, key, "the photo library") is { } missing)
                {
                    return missing;
                }

                var current = PHPhotoLibrary.GetAuthorizationStatus(level);
                if (current != PHAuthorizationStatus.NotDetermined)
                {
                    return AppleStatusMapping.FromPhotos(current);
                }

                var result = await PHPhotoLibrary.RequestAuthorizationAsync(level).WaitAsync(token).ConfigureAwait(false);
                return AppleStatusMapping.FromPhotos(result);
            }

            if (permission == NativePermissions.Notifications)
            {
                return await RequestNotificationsAsync(token).ConfigureAwait(false);
            }

            if (permission == NativePermissions.Biometrics)
            {
                return CheckBiometrics();
            }

            return CheckOther(permission);
        }, cancellationToken);

    /// <inheritdoc />
    /// <remarks>Always <see langword="false"/>: iOS shows each system prompt only once and has no rationale concept.</remarks>
    public ValueTask<NativeResult<bool>> ShouldShowRationaleAsync(NativePermission permission, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(NativeResult.Success(false));

    /// <inheritdoc />
    public ValueTask<NativeResult> OpenAppSettingsAsync(CancellationToken cancellationToken = default) =>
        NativeOperation.RunAsync(Capability, async token =>
        {
            var opened = await AppleUI.OnMainThreadAsync(async () =>
            {
                using var url = new NSUrl(UIApplication.OpenSettingsUrlString);
                return await UIApplication.SharedApplication.OpenUrlAsync(url, new UIApplicationOpenUrlOptions()).ConfigureAwait(true);
            }).WaitAsync(token).ConfigureAwait(false);

            return opened
                ? NativeResult.Ok
                : NativeError.Failed(Capability, NativeErrorCodes.PlatformError, "The system refused to open the app's Settings page.");
        }, cancellationToken);

    /// <summary>Requests notification authorization (alert, sound, badge) and returns the resulting status.</summary>
    internal static async Task<NativeResult<PermissionStatus>> RequestNotificationsAsync(CancellationToken cancellationToken)
    {
        var center = UNUserNotificationCenter.Current;
        var settings = await center.GetNotificationSettingsAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        if (settings.AuthorizationStatus != UNAuthorizationStatus.NotDetermined)
        {
            return AppleStatusMapping.FromNotifications(settings.AuthorizationStatus);
        }

        var (_, error) = await center.RequestAuthorizationAsync(RequestedNotificationOptions).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return NativeError.Failed(NativeCapabilities.Notifications, NativeErrorCodes.PlatformError, error.LocalizedDescription) with
            {
                Details = new Dictionary<string, string>(StringComparer.Ordinal) { ["domain"] = error.Domain, ["code"] = error.Code.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            };
        }

        settings = await center.GetNotificationSettingsAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        return AppleStatusMapping.FromNotifications(settings.AuthorizationStatus);
    }

    private static (PHAccessLevel Level, string Key) PhotoAccess(NativePermission permission) =>
        permission == NativePermissions.PhotosAddOnly
            ? (PHAccessLevel.AddOnly, AppleInfoPlist.PhotoLibraryAddUsage)
            : (PHAccessLevel.ReadWrite, AppleInfoPlist.PhotoLibraryUsage);

    private NativeResult<PermissionStatus> CheckBiometrics()
    {
        var probe = AppleBiometricProbe.Run();
        if (probe.HasFaceId && AppleInfoPlist.RequireUsageDescription(Capability, AppleInfoPlist.FaceIdUsage, "Face ID") is { } missing)
        {
            return missing;
        }

        return probe.IsFaceIdUsageDenied ? PermissionStatus.Denied : PermissionStatus.NotRequired;
    }

    private NativeResult<PermissionStatus> CheckOther(NativePermission permission)
    {
        if (permission == NativePermissions.StorageRead || permission == NativePermissions.StorageWrite ||
            permission == NativePermissions.ExactAlarms || permission == NativePermissions.ClipboardRead)
        {
            return PermissionStatus.NotRequired;
        }

        LogUnknownPermission(_logger, permission.Name);
        return NativeError.Unsupported(Capability, $"Permission '{permission}' does not exist on {(OperatingSystem.IsMacCatalyst() ? "Mac Catalyst" : "iOS")}.");
    }

    [LoggerMessage(EventId = 3101, Level = LogLevel.Debug, Message = "Permission '{Permission}' is not supported on Apple platforms.")]
    private static partial void LogUnknownPermission(ILogger logger, string permission);
}
