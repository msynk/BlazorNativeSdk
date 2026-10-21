namespace BlazorNativeSdk.Maui;

/// <summary>Route rules for windows opened by the window manager. Pure.</summary>
internal static class MauiWindowRoutes
{
    /// <summary>Returns whether <paramref name="route"/> is an app-relative path (no scheme, not protocol-relative).</summary>
    public static bool IsAppRoute(string route) =>
        route.StartsWith('/') &&
        !route.StartsWith("//", StringComparison.Ordinal) &&
        !route.StartsWith("/\\", StringComparison.Ordinal) &&
        !route.Contains("://", StringComparison.Ordinal) &&
        !route.Any(char.IsControl);
}
