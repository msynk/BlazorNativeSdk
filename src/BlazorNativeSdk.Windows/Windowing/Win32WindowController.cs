using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.WindowsPlatform.Interop;

namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>
/// Controls a top-level Win32 window (HWND) independently of the UI framework that created it: state (including
/// borderless full screen), bounds in device-independent pixels, topmost, focus, visibility, closing and placement
/// persistence. Host adapters (WPF, WinForms, WinUI/.NET MAUI) use it to implement <see cref="INativeWindow"/>.
/// </summary>
/// <remarks>
/// <para>
/// Read members never send window messages and are safe from any thread. Members that change the window
/// (<see cref="SetTitle"/>, <see cref="SetState"/>, <see cref="SetBounds"/>, ...) should be called on the thread that owns
/// the window: they work from other threads, but Win32 then sends messages synchronously to the owner thread, which
/// deadlocks if that thread is blocked waiting for the caller. <see cref="Win32NativeWindowBase"/> marshals for you.
/// </para>
/// <para>
/// DIP coordinates use the scale factor of the monitor a rectangle belongs to (physical / scale), the same convention
/// as <see cref="IScreenService"/>, so window bounds and display bounds can be compared directly.
/// Full screen is implemented by switching the window to <c>WS_POPUP</c> sized to its monitor; the previous style and
/// placement are restored when leaving full screen. Frameworks that manage their own window chrome may re-apply styles;
/// host adapters can override full screen with a framework-native implementation.
/// </para>
/// </remarks>
public sealed unsafe class Win32WindowController
{
    // Full-screen restore state is keyed by HWND so that every controller instance for a window agrees on it.
    private static readonly ConcurrentDictionary<nint, FullScreenState> s_fullScreen = new();

    /// <summary>Creates a controller for <paramref name="hwnd"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="hwnd"/> is zero.</exception>
    public Win32WindowController(nint hwnd)
    {
        if (hwnd == 0)
        {
            throw new ArgumentException("The window handle must not be zero.", nameof(hwnd));
        }

        Handle = hwnd;
    }

    /// <summary>The window handle.</summary>
    public nint Handle { get; }

    /// <summary><see langword="true"/> while the handle identifies an existing window.</summary>
    public bool IsAlive => NativeMethods.IsWindow(Handle);

    /// <summary>The window title (read without sending messages to the owner thread).</summary>
    public string Title
    {
        get
        {
            const int capacity = 512;
            var buffer = stackalloc char[capacity];
            var length = NativeMethods.InternalGetWindowText(Handle, buffer, capacity);
            return length > 0 ? new string(buffer, 0, length) : string.Empty;
        }
    }

    /// <summary>The current state. A minimized full-screen window reports <see cref="NativeWindowState.Minimized"/>.</summary>
    public NativeWindowState State
    {
        get
        {
            if (NativeMethods.IsIconic(Handle))
            {
                return NativeWindowState.Minimized;
            }

            if (IsFullScreen)
            {
                return NativeWindowState.FullScreen;
            }

            return NativeMethods.IsZoomed(Handle) ? NativeWindowState.Maximized : NativeWindowState.Normal;
        }
    }

    /// <summary>The current window rectangle in DIPs (screen coordinates).</summary>
    public WindowBounds Bounds
    {
        get
        {
            using var scope = DpiAwarenessScope.PerMonitorV2();
            RECT rect;
            if (!NativeMethods.GetWindowRect(Handle, &rect))
            {
                return default;
            }

            var monitor = Monitors.FromPhysicalRect(rect);
            return monitor?.ToDip(rect) ?? new WindowBounds(rect.Left, rect.Top, rect.Width, rect.Height);
        }
    }

    /// <summary>The restored (normal) bounds in DIPs, also when the window is maximized, minimized or full screen.</summary>
    public WindowBounds RestoredBounds
    {
        get
        {
            using var scope = DpiAwarenessScope.PerMonitorV2();
            var placement = s_fullScreen.TryGetValue(Handle, out var saved) ? saved.Placement : GetPlacementCore();
            var screenRect = WorkspaceToScreen(placement.rcNormalPosition);
            var monitor = Monitors.FromPhysicalRect(screenRect);
            return monitor?.ToDip(screenRect) ?? new WindowBounds(screenRect.Left, screenRect.Top, screenRect.Width, screenRect.Height);
        }
    }

    /// <summary>The scale factor of the window (its DPI / 96).</summary>
    public double ScaleFactor
    {
        get
        {
            var dpi = NativeMethods.GetDpiForWindow(Handle);
            return dpi == 0 ? 1.0 : dpi / (double)Win32.USER_DEFAULT_SCREEN_DPI;
        }
    }

    /// <summary>The id (<c>\\.\DISPLAYn</c>) of the display the window is on, matching <see cref="DisplayInfo.Id"/>.</summary>
    public string? DisplayId => Monitors.FromWindow(Handle)?.Device;

    /// <summary>Whether the window is visible.</summary>
    public bool IsVisible => NativeMethods.IsWindowVisible(Handle);

    /// <summary>Whether the window (or one of its children) is the foreground window.</summary>
    public bool IsFocused
    {
        get
        {
            var foreground = NativeMethods.GetForegroundWindow();
            return foreground != 0 && (foreground == Handle || NativeMethods.GetAncestor(foreground, Win32.GA_ROOT) == Handle);
        }
    }

    /// <summary>Whether the window stays above non-topmost windows.</summary>
    public bool IsAlwaysOnTop => ((nuint)NativeMethods.GetWindowLongPtr(Handle, Win32.GWL_EXSTYLE) & Win32.WS_EX_TOPMOST) != 0;

    /// <summary>Whether the window is in borderless full-screen mode set by <see cref="SetState"/>.</summary>
    public bool IsFullScreen => s_fullScreen.ContainsKey(Handle);

    /// <summary>Sets the title with <c>SetWindowTextW</c>. Frameworks that own the title (WPF <c>Window.Title</c>) may overwrite it later.</summary>
    public NativeResult SetTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        return Check(NativeMethods.SetWindowTextW(Handle, title), "SetWindowTextW");
    }

    /// <summary>Changes the state. Showing a hidden window is part of changing its state.</summary>
    public NativeResult SetState(NativeWindowState state)
    {
        if (!IsAlive)
        {
            return WindowGone();
        }

        switch (state)
        {
            case NativeWindowState.Normal:
                ExitFullScreen();
                return NativeMethods.IsIconic(Handle) || NativeMethods.IsZoomed(Handle) || !NativeMethods.IsWindowVisible(Handle)
                    ? RestoreToNormal()
                    : NativeResult.Ok;

            case NativeWindowState.Minimized:
                _ = NativeMethods.ShowWindow(Handle, Win32.SW_MINIMIZE);
                return NativeResult.Ok;

            case NativeWindowState.Maximized:
                ExitFullScreen();
                _ = NativeMethods.ShowWindow(Handle, Win32.SW_MAXIMIZE);
                return NativeResult.Ok;

            case NativeWindowState.FullScreen:
                return EnterFullScreen();

            default:
                return NativeError.Failed(NativeCapabilities.Windows, NativeErrorCodes.InvalidArgument, $"Unknown window state '{state}'.");
        }
    }

    /// <summary>
    /// Moves and resizes the window. When the window is maximized, minimized or full screen, the restored bounds are updated
    /// instead and take effect when it returns to the normal state.
    /// </summary>
    public NativeResult SetBounds(WindowBounds bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y))
        {
            return NativeError.Failed(NativeCapabilities.Windows, NativeErrorCodes.InvalidArgument, "Window bounds must be finite with a positive width and height.");
        }

        if (!IsAlive)
        {
            return WindowGone();
        }

        using var scope = DpiAwarenessScope.PerMonitorV2();
        var monitors = Monitors.GetAll();
        var monitor = Monitors.FindForDipRect(monitors, bounds);
        var physical = monitor?.ToPhysical(bounds) ?? new RECT((int)bounds.X, (int)bounds.Y, (int)bounds.Right, (int)bounds.Bottom);

        if (State != NativeWindowState.Normal)
        {
            return SetRestoredRect(physical);
        }

        const uint flags = Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER;
        var dpiBefore = NativeMethods.GetDpiForWindow(Handle);
        if (!NativeMethods.SetWindowPos(Handle, 0, physical.Left, physical.Top, physical.Width, physical.Height, flags))
        {
            return LastError("SetWindowPos");
        }

        // Crossing onto a monitor with another DPI makes per-monitor-aware frameworks resize the window to their suggested
        // rectangle (WM_DPICHANGED). Re-apply the requested rectangle once so the final size matches the request.
        if (NativeMethods.GetDpiForWindow(Handle) != dpiBefore)
        {
            _ = NativeMethods.SetWindowPos(Handle, 0, physical.Left, physical.Top, physical.Width, physical.Height, flags);
        }

        return NativeResult.Ok;
    }

    /// <summary>Keeps the window above non-topmost windows (<c>HWND_TOPMOST</c>) or returns it to the normal z-order.</summary>
    public NativeResult SetAlwaysOnTop(bool alwaysOnTop) =>
        Check(
            NativeMethods.SetWindowPos(Handle, alwaysOnTop ? Win32.HWND_TOPMOST : Win32.HWND_NOTOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE),
            "SetWindowPos");

    /// <summary>Shows (and activates) or hides the window.</summary>
    public NativeResult SetVisible(bool visible)
    {
        if (!IsAlive)
        {
            return WindowGone();
        }

        _ = NativeMethods.ShowWindow(Handle, visible ? Win32.SW_SHOW : Win32.SW_HIDE);
        return NativeResult.Ok;
    }

    /// <summary>
    /// Restores the window if minimized, brings it to the front and activates it. Uses the <c>AttachThreadInput</c>
    /// workaround when Windows' foreground lock refuses a plain <c>SetForegroundWindow</c>; when activation is still
    /// refused, the taskbar button flashes and <see cref="NativeResultStatus.Unavailable"/> is returned.
    /// </summary>
    public NativeResult Focus()
    {
        if (!IsAlive)
        {
            return WindowGone();
        }

        if (NativeMethods.IsIconic(Handle))
        {
            _ = NativeMethods.ShowWindow(Handle, Win32.SW_RESTORE);
        }
        else if (!NativeMethods.IsWindowVisible(Handle))
        {
            _ = NativeMethods.ShowWindow(Handle, Win32.SW_SHOW);
        }

        if (NativeMethods.SetForegroundWindow(Handle) && IsFocused)
        {
            return NativeResult.Ok;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = foreground == 0 ? 0 : NativeMethods.GetWindowThreadProcessId(foreground, null);
        var currentThread = NativeMethods.GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != currentThread &&
                       NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            _ = NativeMethods.BringWindowToTop(Handle);
            _ = NativeMethods.SetForegroundWindow(Handle);
        }
        finally
        {
            if (attached)
            {
                _ = NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        if (IsFocused)
        {
            return NativeResult.Ok;
        }

        var flash = new FLASHWINFO
        {
            cbSize = (uint)sizeof(FLASHWINFO),
            hwnd = Handle,
            dwFlags = Win32.FLASHW_ALL | Win32.FLASHW_TIMERNOFG,
        };
        _ = NativeMethods.FlashWindowEx(&flash);
        return NativeError.Unavailable(
            NativeCapabilities.Windows,
            NativeErrorCodes.PermissionRestricted,
            "Windows refused to move the window to the foreground (foreground lock); its taskbar button flashes instead.");
    }

    /// <summary>Asks the window to close by posting <c>WM_CLOSE</c>; the owning framework raises its closing events and may cancel.</summary>
    public NativeResult Close() => Check(NativeMethods.PostMessageW(Handle, Win32.WM_CLOSE, 0, 0), "PostMessageW");

    /// <summary>Captures the restored bounds, state and display for persistence.</summary>
    public WindowPlacement ToPlacement() => new()
    {
        Bounds = RestoredBounds,
        State = State,
        DisplayId = DisplayId,
    };

    /// <summary>
    /// Applies a persisted placement: sets the restored bounds and, when <paramref name="applyState"/> is
    /// <see langword="true"/>, the state (which shows the window). Validate placements against the connected displays first
    /// (<see cref="IWindowPlacementStore.LoadAsync"/> does).
    /// </summary>
    public NativeResult ApplyPlacement(WindowPlacement placement, bool applyState = true)
    {
        ArgumentNullException.ThrowIfNull(placement);
        if (!IsAlive)
        {
            return WindowGone();
        }

        if (placement.Bounds.Width <= 0 || placement.Bounds.Height <= 0)
        {
            return NativeError.Failed(NativeCapabilities.Windows, NativeErrorCodes.InvalidArgument, "The placement bounds must have a positive width and height.");
        }

        using var scope = DpiAwarenessScope.PerMonitorV2();
        var monitor = Monitors.FindForDipRect(Monitors.GetAll(), placement.Bounds);
        var physical = monitor?.ToPhysical(placement.Bounds) ??
                       new RECT((int)placement.Bounds.X, (int)placement.Bounds.Y, (int)placement.Bounds.Right, (int)placement.Bounds.Bottom);

        if (IsFullScreen)
        {
            ExitFullScreen();
        }

        var native = GetPlacementCore();
        native.rcNormalPosition = ScreenToWorkspace(physical);
        native.flags = 0;
        if (applyState)
        {
            native.showCmd = placement.State switch
            {
                NativeWindowState.Maximized or NativeWindowState.FullScreen => (uint)Win32.SW_SHOWMAXIMIZED,
                NativeWindowState.Minimized => (uint)Win32.SW_SHOWMINIMIZED,
                _ => (uint)Win32.SW_SHOWNORMAL,
            };
        }
        else
        {
            native.showCmd = !NativeMethods.IsWindowVisible(Handle) ? (uint)Win32.SW_HIDE : native.showCmd;
        }

        if (!NativeMethods.SetWindowPlacement(Handle, &native))
        {
            return LastError("SetWindowPlacement");
        }

        return applyState && placement.State == NativeWindowState.FullScreen ? EnterFullScreen() : NativeResult.Ok;
    }

    private NativeResult EnterFullScreen()
    {
        if (NativeMethods.IsIconic(Handle))
        {
            _ = NativeMethods.ShowWindow(Handle, Win32.SW_RESTORE);
        }

        if (IsFullScreen)
        {
            return NativeResult.Ok;
        }

        using var scope = DpiAwarenessScope.PerMonitorV2();
        var monitor = Monitors.FromWindow(Handle);
        if (monitor is null)
        {
            return NativeError.Unavailable(NativeCapabilities.Windows, NativeErrorCodes.HostNotReady, "The window is not on any display.");
        }

        var style = NativeMethods.GetWindowLongPtr(Handle, Win32.GWL_STYLE);
        var state = new FullScreenState(style, GetPlacementCore());
        if (!s_fullScreen.TryAdd(Handle, state))
        {
            return NativeResult.Ok;
        }

        PruneDeadWindows();

        // Leave the maximized state first (as browsers do): the saved placement still says "maximized", so leaving full
        // screen returns the window to it.
        if (NativeMethods.IsZoomed(Handle))
        {
            _ = NativeMethods.ShowWindow(Handle, Win32.SW_RESTORE);
            style = NativeMethods.GetWindowLongPtr(Handle, Win32.GWL_STYLE);
        }
        var popupStyle = (nint)(((nuint)style & ~(nuint)Win32.WS_OVERLAPPEDWINDOW) | Win32.WS_POPUP);
        _ = NativeMethods.SetWindowLongPtr(Handle, Win32.GWL_STYLE, popupStyle);
        var rect = monitor.Value.Bounds;
        if (!NativeMethods.SetWindowPos(Handle, Win32.HWND_TOP, rect.Left, rect.Top, rect.Width, rect.Height, Win32.SWP_NOOWNERZORDER | Win32.SWP_FRAMECHANGED | Win32.SWP_SHOWWINDOW))
        {
            var error = LastError("SetWindowPos");
            ExitFullScreen();
            return error;
        }

        return NativeResult.Ok;
    }

    private void ExitFullScreen()
    {
        if (!s_fullScreen.TryRemove(Handle, out var state))
        {
            return;
        }

        using var scope = DpiAwarenessScope.PerMonitorV2();
        // The min/max state comes back through the saved placement, not through raw style bits.
        const uint WS_MINIMIZE = 0x20000000, WS_MAXIMIZE = 0x01000000;
        _ = NativeMethods.SetWindowLongPtr(Handle, Win32.GWL_STYLE, (nint)((nuint)state.Style & ~(nuint)(WS_MINIMIZE | WS_MAXIMIZE)));
        var placement = state.Placement;
        _ = NativeMethods.SetWindowPlacement(Handle, &placement);
        _ = NativeMethods.SetWindowPos(Handle, 0, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOOWNERZORDER | Win32.SWP_FRAMECHANGED);
    }

    // SW_RESTORE returns a window that was maximized before it was minimized to the maximized state (the placement carries
    // WPF_RESTORETOMAXIMIZED). Applying the placement with SW_SHOWNORMAL and no flags restores it straight to its normal
    // bounds, so observers never see an intermediate maximized state.
    private NativeResult RestoreToNormal()
    {
        var placement = new WINDOWPLACEMENT { length = (uint)sizeof(WINDOWPLACEMENT) };
        if (NativeMethods.GetWindowPlacement(Handle, &placement))
        {
            placement.flags = 0;
            placement.showCmd = (uint)Win32.SW_SHOWNORMAL;
            if (NativeMethods.SetWindowPlacement(Handle, &placement))
            {
                return NativeResult.Ok;
            }
        }

        // Fall back to ShowWindow: a second SW_RESTORE takes a window that was restored to maximized back to normal.
        _ = NativeMethods.ShowWindow(Handle, Win32.SW_RESTORE);
        if (NativeMethods.IsZoomed(Handle))
        {
            _ = NativeMethods.ShowWindow(Handle, Win32.SW_RESTORE);
        }

        return IsAlive ? NativeResult.Ok : WindowGone();
    }

    private NativeResult SetRestoredRect(RECT physical)
    {
        var workspace = ScreenToWorkspace(physical);
        if (s_fullScreen.TryGetValue(Handle, out var saved))
        {
            var updated = saved.Placement;
            updated.rcNormalPosition = workspace;
            s_fullScreen[Handle] = saved with { Placement = updated };
            return NativeResult.Ok;
        }

        var placement = GetPlacementCore();
        placement.rcNormalPosition = workspace;
        placement.flags = 0;
        return Check(NativeMethods.SetWindowPlacement(Handle, &placement), "SetWindowPlacement");
    }

    private WINDOWPLACEMENT GetPlacementCore()
    {
        var placement = new WINDOWPLACEMENT { length = (uint)sizeof(WINDOWPLACEMENT) };
        _ = NativeMethods.GetWindowPlacement(Handle, &placement);
        return placement;
    }

    // WINDOWPLACEMENT.rcNormalPosition uses workspace coordinates (relative to the monitor's work area) unless the window is a
    // tool window; they differ from screen coordinates when the taskbar is docked at the top or left.
    private RECT WorkspaceToScreen(RECT rect)
    {
        if (IsToolWindow)
        {
            return rect;
        }

        var monitor = Monitors.FromPhysicalRect(rect);
        if (monitor is not { } m)
        {
            return rect;
        }

        var dx = m.WorkArea.Left - m.Bounds.Left;
        var dy = m.WorkArea.Top - m.Bounds.Top;
        return new RECT(rect.Left + dx, rect.Top + dy, rect.Right + dx, rect.Bottom + dy);
    }

    private RECT ScreenToWorkspace(RECT rect)
    {
        if (IsToolWindow)
        {
            return rect;
        }

        var monitor = Monitors.FromPhysicalRect(rect);
        if (monitor is not { } m)
        {
            return rect;
        }

        var dx = m.WorkArea.Left - m.Bounds.Left;
        var dy = m.WorkArea.Top - m.Bounds.Top;
        return new RECT(rect.Left - dx, rect.Top - dy, rect.Right - dx, rect.Bottom - dy);
    }

    private bool IsToolWindow => ((nuint)NativeMethods.GetWindowLongPtr(Handle, Win32.GWL_EXSTYLE) & Win32.WS_EX_TOOLWINDOW) != 0;

    private static void PruneDeadWindows()
    {
        foreach (var hwnd in s_fullScreen.Keys)
        {
            if (!NativeMethods.IsWindow(hwnd))
            {
                _ = s_fullScreen.TryRemove(hwnd, out _);
            }
        }
    }

    private NativeResult Check(bool succeeded, string api) => succeeded ? NativeResult.Ok : LastError(api);

    private NativeResult LastError(string api)
    {
        var error = Marshal.GetLastPInvokeError();
        return IsAlive
            ? NativeError.Failed(NativeCapabilities.Windows, NativeErrorCodes.PlatformError, $"{api} failed with Win32 error {error}.")
            : WindowGone();
    }

    private static NativeError WindowGone() =>
        NativeError.Unavailable(NativeCapabilities.Windows, NativeErrorCodes.NotFound, "The window no longer exists.");

    private readonly record struct FullScreenState(nint Style, WINDOWPLACEMENT Placement);
}
