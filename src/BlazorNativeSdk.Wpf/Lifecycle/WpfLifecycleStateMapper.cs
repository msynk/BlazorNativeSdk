using BlazorNativeSdk.Lifecycle;

namespace BlazorNativeSdk.Wpf;

/// <summary>The inputs that determine the lifecycle state of a WPF app.</summary>
/// <param name="IsTerminating">The application is exiting or the Windows session is ending.</param>
/// <param name="IsApplicationActive">One of the application's windows is the foreground window (<c>Application.Activated</c>).</param>
/// <param name="HasBeenActivated">The application has been activated at least once.</param>
/// <param name="HasShownWindow">A window tracked by the window manager has been visible at least once.</param>
/// <param name="ShownWindowCount">The number of open tracked windows that are visible and not minimized.</param>
internal readonly record struct WpfLifecycleSnapshot(
    bool IsTerminating,
    bool IsApplicationActive,
    bool HasBeenActivated,
    bool HasShownWindow,
    int ShownWindowCount);

/// <summary>Maps WPF application and window state to <see cref="AppLifecycleState"/>. Pure, so it can be unit tested.</summary>
internal static class WpfLifecycleStateMapper
{
    /// <summary>
    /// Returns the lifecycle state for <paramref name="snapshot"/>, or <see langword="null"/> while the app is still
    /// launching (no tracked window shown yet and never activated).
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item>Exit or session end → <see cref="AppLifecycleState.Terminating"/>.</item>
    /// <item>Every tracked window minimized or hidden, or all of them closed while the process keeps running (a tray app)
    /// → <see cref="AppLifecycleState.Background"/>. Apps that never show a tracked window are driven by activation alone.</item>
    /// <item>An application window is the foreground window → <see cref="AppLifecycleState.Active"/>.</item>
    /// <item>Otherwise (visible while another app has the focus) → <see cref="AppLifecycleState.Inactive"/>.</item>
    /// </list>
    /// </remarks>
    public static AppLifecycleState? Map(WpfLifecycleSnapshot snapshot)
    {
        if (snapshot.IsTerminating)
        {
            return AppLifecycleState.Terminating;
        }

        if (!snapshot.HasShownWindow && !snapshot.HasBeenActivated)
        {
            return null;
        }

        if (snapshot.HasShownWindow && snapshot.ShownWindowCount == 0)
        {
            return AppLifecycleState.Background;
        }

        return snapshot.IsApplicationActive ? AppLifecycleState.Active : AppLifecycleState.Inactive;
    }
}
