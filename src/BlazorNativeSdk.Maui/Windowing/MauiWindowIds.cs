using System.Globalization;
using BlazorNativeSdk.Desktop;

namespace BlazorNativeSdk.Maui;

/// <summary>Window id rules shared by the tracker and the window manager. Pure.</summary>
internal static class MauiWindowIds
{
    /// <summary>The maximum id length (ids are used as state-store keys).</summary>
    public const int MaxLength = 128;

    /// <summary>Returns whether <paramref name="id"/> is a usable window id.</summary>
    public static bool IsValid(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= MaxLength && !id.Any(char.IsControl);

    /// <summary>Creates an id for a window opened without <see cref="WindowOptions.Id"/>.</summary>
    public static string CreateRandom() => "window-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>Creates an id for a window the app opened directly through MAUI.</summary>
    public static string CreateSequential(int sequence) => "window-" + sequence.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Optional <see cref="CapabilityStatus.Features"/> reported by the MAUI window manager.</summary>
public static class MauiWindowFeatures
{
    /// <summary>Additional windows can be opened with <see cref="IWindowManager.OpenAsync"/>.</summary>
    public const string MultipleWindows = "multiple-windows";

    /// <summary>Window size, position and state are restored and saved (<see cref="WindowOptions.PersistPlacement"/>).</summary>
    public const string PlacementPersistence = "placement-persistence";

    /// <summary>Windows can be moved and resized programmatically.</summary>
    public const string MoveAndResize = "move-resize";

    /// <summary>Windows can be minimized, maximized and switched to full screen.</summary>
    public const string WindowStates = "window-states";

    /// <summary>Windows can stay above other windows (<see cref="WindowOptions.AlwaysOnTop"/>).</summary>
    public const string AlwaysOnTop = "always-on-top";

    /// <summary>Windows can be owned by another window (<see cref="WindowOptions.OwnerId"/>).</summary>
    public const string OwnedWindows = "owned-windows";

    /// <summary>Closing can be cancelled by <see cref="INativeWindow.Closing"/> handlers, including closes started by the user.</summary>
    public const string CancelableClose = "cancelable-close";
}
