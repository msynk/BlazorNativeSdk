namespace BlazorNativeSdk.WindowsPlatform.Interop;

/// <summary>
/// Temporarily switches the calling thread to per-monitor (v2) DPI awareness so that window and monitor coordinates are
/// physical pixels regardless of how the host process is manifested, then restores the previous context.
/// </summary>
/// <remarks>Never create windows inside the scope: windows inherit the thread's awareness at creation time.</remarks>
internal readonly ref struct DpiAwarenessScope
{
    private readonly nint _previous;

    private DpiAwarenessScope(nint previous) => _previous = previous;

    public static DpiAwarenessScope PerMonitorV2() =>
        new(NativeMethods.SetThreadDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2));

    public void Dispose()
    {
        if (_previous != 0)
        {
            _ = NativeMethods.SetThreadDpiAwarenessContext(_previous);
        }
    }
}
