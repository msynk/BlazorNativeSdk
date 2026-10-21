using BlazorNativeSdk.Lifecycle;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>Pure lifecycle mapping used by <see cref="AndroidAppLifecycle"/>.</summary>
internal static class AndroidLifecycleMapper
{
    // ComponentCallbacks2.TRIM_MEMORY_*
    public const int TrimMemoryRunningLow = 10;
    public const int TrimMemoryUiHidden = 20;

    // ActivityManager.RunningAppProcessInfo.IMPORTANCE_FOREGROUND
    public const int ImportanceForeground = 100;

    /// <summary>Maps started/resumed activity counts to a state.</summary>
    public static AppLifecycleState MapCounts(int started, int resumed) =>
        resumed > 0 ? AppLifecycleState.Active : started > 0 ? AppLifecycleState.Inactive : AppLifecycleState.Background;

    /// <summary>
    /// Whether an <c>onTrimMemory</c> level signals memory pressure. <c>TRIM_MEMORY_UI_HIDDEN</c> only means the UI went to
    /// the background and is not reported.
    /// </summary>
    public static bool IsMemoryPressure(int level) => level >= TrimMemoryRunningLow && level != TrimMemoryUiHidden;
}
