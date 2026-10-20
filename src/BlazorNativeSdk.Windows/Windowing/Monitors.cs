using System.Runtime.InteropServices;
using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.WindowsPlatform.Interop;

namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>A connected monitor in physical pixels with its effective DPI.</summary>
internal readonly record struct MonitorData(nint Handle, string Device, string? Name, RECT Bounds, RECT WorkArea, uint Dpi, bool IsPrimary)
{
    /// <summary>The scale factor (1.0 = 96 DPI).</summary>
    public double Scale => Dpi == 0 ? 1.0 : Dpi / (double)Win32.USER_DEFAULT_SCREEN_DPI;

    /// <summary>The monitor bounds in DIPs.</summary>
    public WindowBounds DipBounds => ToDip(Bounds);

    /// <summary>The work area in DIPs.</summary>
    public WindowBounds DipWorkArea => ToDip(WorkArea);

    /// <summary>
    /// Converts a physical rectangle on this monitor to DIPs. The convention (shared by <see cref="WindowsScreenService"/> and
    /// <see cref="Win32WindowController"/>) divides physical coordinates by the scale of the monitor the rectangle belongs to,
    /// so monitor origins and window positions stay consistent in mixed-DPI layouts.
    /// </summary>
    public WindowBounds ToDip(RECT rect) =>
        new(rect.Left / Scale, rect.Top / Scale, rect.Width / Scale, rect.Height / Scale);

    /// <summary>Converts a DIP rectangle on this monitor to physical pixels.</summary>
    public RECT ToPhysical(WindowBounds bounds)
    {
        var left = (int)Math.Round(bounds.X * Scale);
        var top = (int)Math.Round(bounds.Y * Scale);
        var width = (int)Math.Round(bounds.Width * Scale);
        var height = (int)Math.Round(bounds.Height * Scale);
        return new RECT(left, top, left + width, top + height);
    }
}

/// <summary>Enumerates monitors with per-monitor-aware (physical) coordinates.</summary>
internal static unsafe class Monitors
{
    /// <summary>Lists the connected monitors, primary first.</summary>
    public static List<MonitorData> GetAll()
    {
        using var scope = DpiAwarenessScope.PerMonitorV2();
        var handles = new List<nint>();
        var gcHandle = GCHandle.Alloc(handles);
        try
        {
            _ = NativeMethods.EnumDisplayMonitors(0, null, &EnumCallback, GCHandle.ToIntPtr(gcHandle));
        }
        finally
        {
            gcHandle.Free();
        }

        var monitors = new List<MonitorData>(handles.Count);
        foreach (var handle in handles)
        {
            if (TryGetCore(handle, out var data))
            {
                monitors.Add(data);
            }
        }

        monitors.Sort(static (a, b) => b.IsPrimary.CompareTo(a.IsPrimary));
        return monitors;
    }

    /// <summary>Gets the monitor nearest to a window.</summary>
    public static MonitorData? FromWindow(nint hwnd)
    {
        using var scope = DpiAwarenessScope.PerMonitorV2();
        var monitor = NativeMethods.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        return monitor != 0 && TryGetCore(monitor, out var data) ? data : null;
    }

    /// <summary>Gets the monitor nearest to a physical rectangle.</summary>
    public static MonitorData? FromPhysicalRect(RECT rect)
    {
        using var scope = DpiAwarenessScope.PerMonitorV2();
        var monitor = NativeMethods.MonitorFromRect(&rect, Win32.MONITOR_DEFAULTTONEAREST);
        return monitor != 0 && TryGetCore(monitor, out var data) ? data : null;
    }

    /// <summary>Finds the monitor that should host a DIP rectangle: the one containing its center, else the one it overlaps most, else the primary.</summary>
    public static MonitorData? FindForDipRect(IReadOnlyList<MonitorData> monitors, WindowBounds bounds)
    {
        if (monitors.Count == 0)
        {
            return null;
        }

        var centerX = bounds.X + (bounds.Width / 2);
        var centerY = bounds.Y + (bounds.Height / 2);
        foreach (var monitor in monitors)
        {
            var dip = monitor.DipBounds;
            if (centerX >= dip.X && centerX < dip.Right && centerY >= dip.Y && centerY < dip.Bottom)
            {
                return monitor;
            }
        }

        MonitorData? best = null;
        var bestArea = 0.0;
        foreach (var monitor in monitors)
        {
            var area = monitor.DipBounds.IntersectionArea(bounds);
            if (area > bestArea)
            {
                bestArea = area;
                best = monitor;
            }
        }

        return best ?? monitors.FirstOrDefault(m => m.IsPrimary);
    }

    /// <summary>Converts a DIP screen point to physical pixels using the monitor that contains it.</summary>
    public static POINT DipPointToPhysical(double x, double y)
    {
        var monitor = FindForDipRect(GetAll(), new WindowBounds(x, y, 0, 0));
        var scale = monitor?.Scale ?? 1.0;
        return new POINT { X = (int)Math.Round(x * scale), Y = (int)Math.Round(y * scale) };
    }

    private static bool TryGetCore(nint monitor, out MonitorData data)
    {
        data = default;
        var info = new MONITORINFOEXW { cbSize = (uint)sizeof(MONITORINFOEXW) };
        if (!NativeMethods.GetMonitorInfoW(monitor, &info))
        {
            return false;
        }

        uint dpiX = Win32.USER_DEFAULT_SCREEN_DPI, dpiY = Win32.USER_DEFAULT_SCREEN_DPI;
        if (NativeMethods.GetDpiForMonitor(monitor, Win32.MDT_EFFECTIVE_DPI, &dpiX, &dpiY) < 0 || dpiX == 0)
        {
            dpiX = NativeMethods.GetDpiForSystem();
        }

        var device = new string(info.szDevice);
        data = new MonitorData(
            monitor,
            device,
            GetFriendlyName(info.szDevice),
            info.rcMonitor,
            info.rcWork,
            dpiX,
            (info.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0);
        return true;
    }

    private static string? GetFriendlyName(char* device)
    {
        var display = new DISPLAY_DEVICEW { cb = (uint)sizeof(DISPLAY_DEVICEW) };
        if (!NativeMethods.EnumDisplayDevicesW(device, 0, &display, 0))
        {
            return null;
        }

        var name = new string(display.DeviceString);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    [UnmanagedCallersOnly]
    private static int EnumCallback(nint monitor, nint hdc, RECT* rect, nint data)
    {
        if (GCHandle.FromIntPtr(data).Target is List<nint> list)
        {
            list.Add(monitor);
        }

        return 1;
    }
}
