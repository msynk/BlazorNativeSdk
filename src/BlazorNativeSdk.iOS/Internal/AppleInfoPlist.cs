using System.Collections.Concurrent;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>
/// Reads the app's Info.plist. iOS terminates an app that calls a privacy-sensitive API without the matching usage
/// description, so every such call is guarded by <see cref="RequireUsageDescription"/>.
/// </summary>
internal static class AppleInfoPlist
{
    public const string CameraUsage = "NSCameraUsageDescription";
    public const string MicrophoneUsage = "NSMicrophoneUsageDescription";
    public const string PhotoLibraryUsage = "NSPhotoLibraryUsageDescription";
    public const string PhotoLibraryAddUsage = "NSPhotoLibraryAddUsageDescription";
    public const string FaceIdUsage = "NSFaceIDUsageDescription";
    public const string BackgroundTaskIdentifiers = "BGTaskSchedulerPermittedIdentifiers";
    public const string BackgroundModes = "UIBackgroundModes";

    private static readonly ConcurrentDictionary<string, bool> s_usageDescriptions = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string[]> s_arrays = new(StringComparer.Ordinal);

    /// <summary>The bundle identifier, or a placeholder when it cannot be read.</summary>
    public static string BundleIdentifier => NSBundle.MainBundle.BundleIdentifier ?? "app";

    /// <summary>Returns whether a non-empty usage description is declared.</summary>
    public static bool HasUsageDescription(string key) =>
        s_usageDescriptions.GetOrAdd(key, static k =>
            NSBundle.MainBundle.ObjectForInfoDictionary(k) is NSString value && value.Length > 0);

    /// <summary>Returns an error naming the missing key, or <see langword="null"/> when the key is declared.</summary>
    public static NativeError? RequireUsageDescription(CapabilityId capability, string key, string purpose) =>
        HasUsageDescription(key) ? null : AppleInfoPlistErrors.MissingUsageDescription(capability, key, purpose);

    /// <summary>Returns the string entries of an array-valued key (empty when absent).</summary>
    public static IReadOnlyList<string> GetStringArray(string key) =>
        s_arrays.GetOrAdd(key, static k =>
        {
            if (NSBundle.MainBundle.ObjectForInfoDictionary(k) is not NSArray array)
            {
                return [];
            }

            var values = new List<string>((int)array.Count);
            for (nuint i = 0; i < array.Count; i++)
            {
                if (array.GetItem<NSObject>(i) is NSString value)
                {
                    values.Add(value.ToString());
                }
            }

            return [.. values];
        });

    /// <summary>Returns whether an array-valued key contains <paramref name="value"/>.</summary>
    public static bool ArrayContains(string key, string value) =>
        GetStringArray(key).Contains(value, StringComparer.Ordinal);
}
