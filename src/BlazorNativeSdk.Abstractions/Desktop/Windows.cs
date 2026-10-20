using System.Diagnostics.CodeAnalysis;

namespace BlazorNativeSdk.Desktop;

/// <summary>A rectangle in device-independent pixels (DIPs) in virtual-screen coordinates.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public readonly record struct WindowBounds(double X, double Y, double Width, double Height)
{
    /// <summary>The right edge.</summary>
    public double Right => X + Width;

    /// <summary>The bottom edge.</summary>
    public double Bottom => Y + Height;

    /// <summary>Returns the intersection area with another rectangle.</summary>
    public double IntersectionArea(WindowBounds other)
    {
        var width = Math.Min(Right, other.Right) - Math.Max(X, other.X);
        var height = Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y);
        return width > 0 && height > 0 ? width * height : 0;
    }
}

/// <summary>The window's display state.</summary>
public enum NativeWindowState
{
    /// <summary>Restored.</summary>
    Normal,

    /// <summary>Minimized.</summary>
    Minimized,

    /// <summary>Maximized.</summary>
    Maximized,

    /// <summary>Full screen.</summary>
    FullScreen,
}

/// <summary>Operations a window supports in the current host.</summary>
[Flags]
public enum WindowOperations
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Changing the title.</summary>
    SetTitle = 1,

    /// <summary>Moving the window.</summary>
    Move = 2,

    /// <summary>Resizing the window.</summary>
    Resize = 4,

    /// <summary>Minimizing.</summary>
    Minimize = 8,

    /// <summary>Maximizing.</summary>
    Maximize = 16,

    /// <summary>Full-screen mode.</summary>
    FullScreen = 32,

    /// <summary>Keeping the window above others.</summary>
    AlwaysOnTop = 64,

    /// <summary>Hiding and showing.</summary>
    Visibility = 128,

    /// <summary>Activating (focusing).</summary>
    Focus = 256,

    /// <summary>Closing.</summary>
    Close = 512,

    /// <summary>All operations.</summary>
    All = SetTitle | Move | Resize | Minimize | Maximize | FullScreen | AlwaysOnTop | Visibility | Focus | Close,
}

/// <summary>The persisted placement of a window.</summary>
public sealed record WindowPlacement
{
    /// <summary>The restored bounds (not the maximized bounds).</summary>
    public required WindowBounds Bounds { get; init; }

    /// <summary>The state.</summary>
    public NativeWindowState State { get; init; }

    /// <summary>The display the window was on, when known.</summary>
    public string? DisplayId { get; init; }
}

/// <summary>Options for opening a window.</summary>
public sealed record WindowOptions
{
    /// <summary>
    /// A stable id such as <c>settings</c>. Opening an id that is already open focuses the existing window. The id is also the
    /// key for placement persistence. A random id is generated when omitted.
    /// </summary>
    public string? Id { get; init; }

    /// <summary>The title.</summary>
    public string? Title { get; init; }

    /// <summary>The root Blazor component rendered in the new window.</summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
    public Type? RootComponent { get; init; }

    /// <summary>Parameters for <see cref="RootComponent"/>.</summary>
    public IReadOnlyDictionary<string, object?>? Parameters { get; init; }

    /// <summary>
    /// An app route to load instead of a dedicated root component (the host page's router renders it).
    /// Ignored when <see cref="RootComponent"/> is set.
    /// </summary>
    public string? Route { get; init; }

    /// <summary>The initial width in DIPs.</summary>
    public double? Width { get; init; }

    /// <summary>The initial height in DIPs.</summary>
    public double? Height { get; init; }

    /// <summary>The initial left position in DIPs. Centered when both coordinates are omitted.</summary>
    public double? X { get; init; }

    /// <summary>The initial top position in DIPs.</summary>
    public double? Y { get; init; }

    /// <summary>The minimum width.</summary>
    public double? MinWidth { get; init; }

    /// <summary>The minimum height.</summary>
    public double? MinHeight { get; init; }

    /// <summary>Whether the user can resize the window.</summary>
    public bool Resizable { get; init; } = true;

    /// <summary>Keep the window above other windows.</summary>
    public bool AlwaysOnTop { get; init; }

    /// <summary>The initial state.</summary>
    public NativeWindowState InitialState { get; init; }

    /// <summary>Restore and save placement under <see cref="Id"/>. Requires an explicit <see cref="Id"/>.</summary>
    public bool PersistPlacement { get; init; } = true;

    /// <summary>The id of the owner window (child windows stay above their owner and minimize with it).</summary>
    public string? OwnerId { get; init; }
}

/// <summary>Event data for <see cref="INativeWindow.Closing"/>.</summary>
public sealed class WindowClosingEventArgs : EventArgs
{
    /// <summary>Set to <see langword="true"/> to keep the window open (for example to confirm unsaved changes).</summary>
    public bool Cancel { get; set; }
}

/// <summary>A top-level native window that hosts Blazor content.</summary>
/// <remarks>Members may be called from any thread; implementations marshal to the UI thread.</remarks>
public interface INativeWindow
{
    /// <summary>The id.</summary>
    string Id { get; }

    /// <summary>The title.</summary>
    string Title { get; }

    /// <summary>The state.</summary>
    NativeWindowState State { get; }

    /// <summary>The bounds in DIPs.</summary>
    WindowBounds Bounds { get; }

    /// <summary>Whether the window has keyboard focus.</summary>
    bool IsFocused { get; }

    /// <summary>Whether the window is visible.</summary>
    bool IsVisible { get; }

    /// <summary>Whether this is the application's main window.</summary>
    bool IsMainWindow { get; }

    /// <summary>The operations this window supports in the current host.</summary>
    WindowOperations SupportedOperations { get; }

    /// <summary>Sets the title.</summary>
    ValueTask<NativeResult> SetTitleAsync(string title, CancellationToken cancellationToken = default);

    /// <summary>Moves and/or resizes the window.</summary>
    ValueTask<NativeResult> SetBoundsAsync(WindowBounds bounds, CancellationToken cancellationToken = default);

    /// <summary>Changes the state.</summary>
    ValueTask<NativeResult> SetStateAsync(NativeWindowState state, CancellationToken cancellationToken = default);

    /// <summary>Keeps the window above others.</summary>
    ValueTask<NativeResult> SetAlwaysOnTopAsync(bool alwaysOnTop, CancellationToken cancellationToken = default);

    /// <summary>Brings the window to the front and focuses it.</summary>
    ValueTask<NativeResult> FocusAsync(CancellationToken cancellationToken = default);

    /// <summary>Shows or hides the window.</summary>
    ValueTask<NativeResult> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default);

    /// <summary>Requests the window to close; <see cref="Closing"/> handlers may cancel.</summary>
    ValueTask<NativeResult> CloseAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets the native handle (HWND on Windows, NSWindow/UIWindow pointer on Apple platforms) when available.</summary>
    bool TryGetPlatformHandle(out nint handle);

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    event EventHandler? StateChanged;

    /// <summary>Raised when <see cref="Bounds"/> changes.</summary>
    event EventHandler? BoundsChanged;

    /// <summary>Raised when focus changes.</summary>
    event EventHandler? FocusChanged;

    /// <summary>Raised when <see cref="IsVisible"/> changes.</summary>
    event EventHandler? VisibilityChanged;

    /// <summary>Raised before the window closes. Set <see cref="WindowClosingEventArgs.Cancel"/> to keep it open.</summary>
    event EventHandler<WindowClosingEventArgs>? Closing;

    /// <summary>Raised after the window closed.</summary>
    event EventHandler? Closed;
}

/// <summary>Opens and tracks native windows.</summary>
public interface IWindowManager : INativeCapability
{
    /// <summary>The open windows.</summary>
    IReadOnlyList<INativeWindow> Windows { get; }

    /// <summary>The main window, when one exists.</summary>
    INativeWindow? MainWindow { get; }

    /// <summary>Opens a new window, or focuses and returns the open window with the same <see cref="WindowOptions.Id"/>.</summary>
    ValueTask<NativeResult<INativeWindow>> OpenAsync(WindowOptions options, CancellationToken cancellationToken = default);

    /// <summary>Finds an open window by id.</summary>
    INativeWindow? FindWindow(string id);

    /// <summary>Raised when a window opens.</summary>
    event EventHandler<INativeWindow>? WindowOpened;

    /// <summary>Raised when a window closes.</summary>
    event EventHandler<INativeWindow>? WindowClosed;
}

/// <summary>Persists window placement between sessions.</summary>
public interface IWindowPlacementStore
{
    /// <summary>Loads a placement, validated against the currently connected displays (off-screen placements are corrected).</summary>
    ValueTask<WindowPlacement?> LoadAsync(string windowId, CancellationToken cancellationToken = default);

    /// <summary>Saves a placement.</summary>
    ValueTask SaveAsync(string windowId, WindowPlacement placement, CancellationToken cancellationToken = default);
}

/// <summary>A connected display.</summary>
/// <param name="Id">A stable id for the session.</param>
/// <param name="Name">The display name, when known.</param>
/// <param name="Bounds">The full bounds in DIPs.</param>
/// <param name="WorkArea">The bounds excluding task bars and docks, in DIPs.</param>
/// <param name="ScaleFactor">The scale factor (1.0 = 96 DPI on Windows).</param>
/// <param name="IsPrimary">Whether this is the primary display.</param>
public sealed record DisplayInfo(string Id, string? Name, WindowBounds Bounds, WindowBounds WorkArea, double ScaleFactor, bool IsPrimary);

/// <summary>Enumerates displays.</summary>
public interface IScreenService : INativeCapability
{
    /// <summary>Lists the connected displays.</summary>
    ValueTask<NativeResult<IReadOnlyList<DisplayInfo>>> GetDisplaysAsync(CancellationToken cancellationToken = default);
}
