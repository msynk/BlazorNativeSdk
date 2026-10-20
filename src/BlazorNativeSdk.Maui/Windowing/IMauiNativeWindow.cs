using BlazorNativeSdk.Desktop;

namespace BlazorNativeSdk.Maui;

/// <summary>Internal surface of the platform <see cref="INativeWindow"/> implementations used by the window manager.</summary>
internal interface IMauiNativeWindow : INativeWindow
{
    /// <summary>The tracked MAUI window.</summary>
    TrackedWindow Tracked { get; }

    /// <summary>Raised on the UI thread right before a close that no handler cancelled (the native window still exists).</summary>
    event EventHandler? CloseApproved;

    /// <summary>Captures the placement to persist, or <see langword="null"/> when it cannot be determined. UI thread.</summary>
    WindowPlacement? CapturePlacement();

    /// <summary>Raises <see cref="INativeWindow.StateChanged"/>. UI thread.</summary>
    void NotifyStateChanged();

    /// <summary>Raises <see cref="INativeWindow.BoundsChanged"/>. UI thread.</summary>
    void NotifyBoundsChanged();

    /// <summary>Raises <see cref="INativeWindow.FocusChanged"/>. UI thread.</summary>
    void NotifyFocusChanged();

    /// <summary>Raises <see cref="INativeWindow.VisibilityChanged"/> when <see cref="INativeWindow.IsVisible"/> differs from the last reported value. UI thread.</summary>
    void NotifyVisibilityChanged();

    /// <summary>Marks the window closed and raises <see cref="INativeWindow.Closed"/>. UI thread.</summary>
    void NotifyClosed();
}
