using BlazorNativeSdk.Lifecycle;

namespace BlazorNativeSdk.Maui;

/// <summary>Maps the windows of a MAUI app to an application lifecycle state. Pure.</summary>
internal static class MauiLifecycleMapper
{
    /// <summary>
    /// Returns the application state for the given windows, or <see langword="null"/> when no window has been shown yet
    /// (windows that are still being created must not make a launching app look backgrounded).
    /// </summary>
    /// <param name="windows">For each open window: whether it was shown at least once, whether it is active, whether it is hidden or minimized.</param>
    public static AppLifecycleState? Map(IEnumerable<(bool HasBeenShown, bool IsActive, bool IsHidden)> windows)
    {
        var anyShown = false;
        var anyVisible = false;
        foreach (var (hasBeenShown, isActive, isHidden) in windows)
        {
            if (!hasBeenShown)
            {
                continue;
            }

            anyShown = true;
            if (isActive && !isHidden)
            {
                return AppLifecycleState.Active;
            }

            anyVisible |= !isHidden;
        }

        if (!anyShown)
        {
            return null;
        }

        return anyVisible ? AppLifecycleState.Inactive : AppLifecycleState.Background;
    }
}
