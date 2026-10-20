using Microsoft.Maui.ApplicationModel;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>Kinds of <see cref="PermissionException"/> thrown by Microsoft.Maui.Essentials.</summary>
internal enum EssentialsPermissionFailure
{
    /// <summary>The permission was denied by the user.</summary>
    Denied,

    /// <summary>The permission is not declared in AndroidManifest.xml.</summary>
    MissingDeclaration,

    /// <summary>The request was not made on the main thread (an adapter bug).</summary>
    WrongThread,
}

/// <summary>Maps exceptions thrown by Microsoft.Maui.Essentials to <see cref="NativeError"/> values.</summary>
internal static class EssentialsErrors
{
    /// <summary>Classifies a <see cref="PermissionException"/> message. Pure; does not touch Android APIs.</summary>
    public static EssentialsPermissionFailure Classify(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return EssentialsPermissionFailure.Denied;
        }

        if (message.Contains("AndroidManifest", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("declare", StringComparison.OrdinalIgnoreCase))
        {
            return EssentialsPermissionFailure.MissingDeclaration;
        }

        return message.Contains("main thread", StringComparison.OrdinalIgnoreCase)
            ? EssentialsPermissionFailure.WrongThread
            : EssentialsPermissionFailure.Denied;
    }

    /// <summary>Extracts the Android permission named between backticks in an Essentials message, when present.</summary>
    public static string? ExtractPermissionName(string? message)
    {
        if (message is null)
        {
            return null;
        }

        var start = message.IndexOf('`', StringComparison.Ordinal);
        var end = start >= 0 ? message.IndexOf('`', start + 1) : -1;
        return end > start + 1 ? message[(start + 1)..end] : null;
    }

    /// <summary>Creates the error reported when <paramref name="androidPermission"/> is not declared in the manifest.</summary>
    public static NativeError MissingDeclaration(CapabilityId capability, string androidPermission) =>
        NativeError.Unavailable(capability, NativeErrorCodes.MissingDeclaration,
            $"The app must declare <uses-permission android:name=\"{androidPermission}\" /> in AndroidManifest.xml.");

    /// <summary>Maps an exception thrown by an Essentials API.</summary>
    public static NativeError Map(CapabilityId capability, Exception exception, NativePermission? permission = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        switch (exception)
        {
            case NativeCapabilityException nce:
                return nce.Error;

            case PermissionException pe:
                return Classify(pe.Message) switch
                {
                    EssentialsPermissionFailure.MissingDeclaration =>
                        ExtractPermissionName(pe.Message) is { } name
                            ? MissingDeclaration(capability, name) with { Exception = pe }
                            : NativeError.Unavailable(capability, NativeErrorCodes.MissingDeclaration, pe.Message) with { Exception = pe },
                    EssentialsPermissionFailure.WrongThread =>
                        NativeError.Failed(capability, NativeErrorCodes.PlatformError, pe.Message, pe),
                    _ => NativeError.PermissionDenied(capability, permission ?? new NativePermission("unknown"), BlazorNativeSdk.PermissionStatus.Denied, pe.Message)
                        with { Exception = pe },
                };

            case Android.Content.ActivityNotFoundException anf:
                return NativeError.Unavailable(capability, AndroidErrorCodes.NoHandler,
                    "No installed app can handle the request: " + anf.Message) with { Exception = anf };

            default:
                return NativeError.FromException(capability, exception);
        }
    }
}
