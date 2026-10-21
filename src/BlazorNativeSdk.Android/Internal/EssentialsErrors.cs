using Microsoft.Maui.ApplicationModel;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>Maps exceptions thrown by Microsoft.Maui.Essentials to <see cref="NativeError"/> values.</summary>
internal static class EssentialsErrors
{
    /// <summary>Maps an exception thrown by an Essentials API.</summary>
    public static NativeError Map(CapabilityId capability, Exception exception, NativePermission? permission = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        switch (exception)
        {
            case NativeCapabilityException nce:
                return nce.Error;

            case PermissionException pe:
                return AndroidPermissionErrors.Map(capability, pe, permission);

            case Android.Content.ActivityNotFoundException anf:
                return NativeError.Unavailable(capability, AndroidErrorCodes.NoHandler,
                    "No installed app can handle the request: " + anf.Message) with { Exception = anf };

            default:
                return NativeError.FromException(capability, exception);
        }
    }
}
