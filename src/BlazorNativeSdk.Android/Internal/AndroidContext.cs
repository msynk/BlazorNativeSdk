using Android.App;
using Android.Content;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>Access to the Android application context, the current activity and common directories.</summary>
internal static class AndroidContext
{
    /// <summary>The application context. Always available once the process started.</summary>
    public static Context App => Application.Context;

    /// <summary>The package name.</summary>
    public static string PackageName => App.PackageName ?? string.Empty;

    /// <summary>The API level of the device.</summary>
    public static int ApiLevel => (int)Android.OS.Build.VERSION.SdkInt;

    /// <summary>
    /// The activity tracked by Microsoft.Maui.Essentials, or <see langword="null"/> when none is alive (background process,
    /// or Essentials was not initialized with <c>Platform.Init</c>).
    /// </summary>
    public static Activity? CurrentActivity
    {
        get
        {
            var activity = Platform.CurrentActivity;
            return activity is null || activity.IsFinishing || activity.IsDestroyed ? null : activity;
        }
    }

    /// <summary>The app cache directory.</summary>
    public static string CacheDirectory => FileSystem.CacheDirectory;

    /// <summary>Creates the error returned when an operation needs a foreground activity and none is available.</summary>
    public static NativeError NoActivity(CapabilityId capability) =>
        NativeError.Unavailable(capability, NativeErrorCodes.HostNotReady,
            "No foreground Android activity is available. Call this while the app is in the foreground; non-MAUI apps must call " +
            "Microsoft.Maui.ApplicationModel.Platform.Init(activity, savedInstanceState) in their activity's OnCreate.");

    /// <summary>Returns whether <paramref name="path"/> is inside the app cache directory (files the adapter may delete).</summary>
    public static bool IsInCacheDirectory(string path)
    {
        try
        {
            var cache = Path.GetFullPath(CacheDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(cache, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}
