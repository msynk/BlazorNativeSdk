using AVFoundation;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>Camera and microphone authorization shared by the permission, camera and barcode services.</summary>
internal static class AppleCaptureAccess
{
    /// <summary>The Info.plist key for a media type.</summary>
    public static string UsageKey(AVAuthorizationMediaType mediaType) =>
        mediaType == AVAuthorizationMediaType.Audio ? AppleInfoPlist.MicrophoneUsage : AppleInfoPlist.CameraUsage;

    /// <summary>The permission for a media type.</summary>
    public static NativePermission Permission(AVAuthorizationMediaType mediaType) =>
        mediaType == AVAuthorizationMediaType.Audio ? NativePermissions.Microphone : NativePermissions.Camera;

    /// <summary>Returns the current status without prompting, or a missing-declaration error.</summary>
    public static NativeResult<PermissionStatus> Check(CapabilityId capability, AVAuthorizationMediaType mediaType)
    {
        if (AppleInfoPlist.RequireUsageDescription(capability, UsageKey(mediaType), Purpose(mediaType)) is { } missing)
        {
            return missing;
        }

        return AppleStatusMapping.FromCapture(AVCaptureDevice.GetAuthorizationStatus(mediaType));
    }

    /// <summary>Prompts when the user has not decided yet and returns the resulting status.</summary>
    public static async Task<NativeResult<PermissionStatus>> RequestAsync(CapabilityId capability, AVAuthorizationMediaType mediaType, CancellationToken cancellationToken)
    {
        var current = Check(capability, mediaType);
        if (!current.IsSuccess || current.Value != PermissionStatus.NotDetermined)
        {
            return current;
        }

        var granted = await AVCaptureDevice.RequestAccessForMediaTypeAsync(mediaType).WaitAsync(cancellationToken).ConfigureAwait(false);
        return granted ? PermissionStatus.Granted : AppleStatusMapping.FromCapture(AVCaptureDevice.GetAuthorizationStatus(mediaType));
    }

    /// <summary>Ensures access is granted, prompting when needed. Returns <see langword="null"/> when access is granted.</summary>
    public static async Task<NativeError?> EnsureGrantedAsync(CapabilityId capability, AVAuthorizationMediaType mediaType, CancellationToken cancellationToken)
    {
        var status = await RequestAsync(capability, mediaType, cancellationToken).ConfigureAwait(false);
        if (!status.IsSuccess)
        {
            return status.Error;
        }

        return status.Value == PermissionStatus.Granted
            ? null
            : NativeError.PermissionDenied(capability, Permission(mediaType), status.Value,
                status.Value == PermissionStatus.Restricted
                    ? $"Access to the {Noun(mediaType)} is restricted by device policy (Screen Time or MDM)."
                    : $"The user denied access to the {Noun(mediaType)}. Direct the user to the app's page in Settings (IPermissionService.OpenAppSettingsAsync).");
    }

    private static string Purpose(AVAuthorizationMediaType mediaType) =>
        mediaType == AVAuthorizationMediaType.Audio ? "the microphone (required to record video with sound)" : "the camera";

    private static string Noun(AVAuthorizationMediaType mediaType) =>
        mediaType == AVAuthorizationMediaType.Audio ? "microphone" : "camera";
}
