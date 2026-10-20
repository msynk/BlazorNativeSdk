namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>Error codes reported by the Android adapter in addition to <see cref="NativeErrorCodes"/>.</summary>
public static class AndroidErrorCodes
{
    /// <summary>
    /// Google Play services is missing, disabled, updating or too old for the requested feature (for example the Google
    /// code scanner). Devices without Google Play services (some tablets, Huawei, Amazon Fire, AOSP builds) report this permanently.
    /// </summary>
    public const string PlayServicesUnavailable = "android.play-services-unavailable";

    /// <summary>
    /// An optional Google Play services module (such as the code scanner UI) is not installed yet. Google Play services
    /// downloads it in the background; retry later.
    /// </summary>
    public const string ModuleUnavailable = "android.module-unavailable";

    /// <summary>No installed app can handle the system intent the operation needs (for example a document provider).</summary>
    public const string NoHandler = "android.no-handler";
}
