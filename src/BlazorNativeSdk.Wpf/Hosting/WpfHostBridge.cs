using System.Windows;
using System.Windows.Interop;
using BlazorNativeSdk.WindowsPlatform;

namespace BlazorNativeSdk.Wpf;

/// <summary>
/// <see cref="IWindowsHostBridge"/> for WPF: the main window is the window attached with
/// <see cref="WpfWindowManager.AttachMainWindow"/> (else <see cref="Application.MainWindow"/>), and UI work runs on the WPF
/// dispatcher. Registered by <c>AddWpf()</c> in place of the Windows adapter's default bridge.
/// </summary>
public sealed class WpfHostBridge : IWindowsHostBridge
{
    private readonly WpfWindowManager _windows;
    private readonly DefaultWindowsHostBridge _fallback = new();

    /// <summary>Creates the bridge.</summary>
    /// <param name="windows">The window manager that tracks the main window.</param>
    public WpfHostBridge(WpfWindowManager windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        _windows = windows;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Thread-safe. Without an attached main window, <see cref="Application.MainWindow"/> is used on the UI thread; from
    /// other threads the process's main window handle is used, so attach the main window for exact results.
    /// </remarks>
    public nint MainWindowHandle
    {
        get
        {
            if (_windows.MainNativeWindow is { } main && main.TryGetPlatformHandle(out var handle))
            {
                return handle;
            }

            var application = Application.Current;
            if (application is null)
            {
                return 0;
            }

            if (!application.Dispatcher.CheckAccess())
            {
                return _fallback.MainWindowHandle;
            }

            return application.MainWindow is { } window ? new WindowInteropHelper(window).Handle : 0;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Runs inline on the UI thread, otherwise through <c>Dispatcher.InvokeAsync</c>. Without a WPF application or attached
    /// window (for example in tests) the action runs inline. Fails with <see cref="NativeErrorCodes.HostNotReady"/> once the
    /// dispatcher has shut down.
    /// </remarks>
    public Task InvokeOnUIThreadAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var dispatcher = WpfDispatch.ApplicationDispatcher ?? _windows.MainNativeWindow?.Dispatcher;
        return dispatcher is null ? action() : WpfDispatch.InvokeAsync(dispatcher, action);
    }
}
