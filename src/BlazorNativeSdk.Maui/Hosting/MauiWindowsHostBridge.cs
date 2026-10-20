#if WINDOWS
using BlazorNativeSdk.WindowsPlatform;

namespace BlazorNativeSdk.Maui;

/// <summary>
/// Connects the Windows adapter to the MAUI (WinUI) host: the main window is the HWND of the app's first MAUI window
/// (<c>WinRT.Interop.WindowNative.GetWindowHandle</c> of its <c>Microsoft.UI.Xaml.Window</c>), falling back to the first
/// open window after it closed, and UI work runs through the MAUI application dispatcher.
/// </summary>
internal sealed class MauiWindowsHostBridge : IWindowsHostBridge
{
    private readonly MauiWindowTracker _tracker;
    private readonly MauiUIDispatcher _dispatcher;

    /// <summary>Creates the bridge.</summary>
    public MauiWindowsHostBridge(MauiWindowTracker tracker, MauiUIDispatcher dispatcher)
    {
        _tracker = tracker;
        _dispatcher = dispatcher;
        tracker.Enable();
    }

    /// <inheritdoc />
    public nint MainWindowHandle => _tracker.GetMainWindowHandle();

    /// <inheritdoc />
    public Task InvokeOnUIThreadAsync(Func<Task> action) => _dispatcher.InvokeAsync(action);
}

/// <summary>Builds the <see cref="NativeHostInfo"/> of a MAUI app on Windows.</summary>
internal static class MauiWindowsHost
{
    /// <summary>Creates host information: Windows, MAUI, desktop form factor.</summary>
    public static NativeHostInfo Create()
    {
        var detected = NativeHostDetector.Detect(NativeHostKind.Maui);
        return detected with
        {
            Platform = NativePlatform.Windows,
            FormFactor = DeviceFormFactor.Desktop,
            Description = $"MAUI on Windows {Environment.OSVersion.Version}",
        };
    }
}
#endif
