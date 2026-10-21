namespace BlazorNativeSdk.ApplePlatform;

/// <summary>Errors for missing Info.plist declarations (see <see cref="AppleInfoPlist"/>). Pure.</summary>
internal static class AppleInfoPlistErrors
{
    /// <summary>
    /// The error reported instead of calling a privacy-sensitive API whose usage description <paramref name="key"/> is missing:
    /// iOS would terminate the app.
    /// </summary>
    /// <param name="capability">The capability that needs the declaration.</param>
    /// <param name="key">The Info.plist key, for example <c>NSCameraUsageDescription</c>.</param>
    /// <param name="purpose">What the app needs, for the message (for example "the camera").</param>
    public static NativeError MissingUsageDescription(CapabilityId capability, string key, string purpose) =>
        NativeError.Unavailable(
            capability,
            NativeErrorCodes.MissingDeclaration,
            $"Add '{key}' to Info.plist with a user-facing explanation of why the app needs {purpose}. " +
            "iOS terminates apps that use this API without it, so the operation was not attempted.");
}
