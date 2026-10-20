using System.Windows;
using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.Wpf;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorNativeSdk;

/// <summary>Extensions that connect WPF windows to BlazorNativeSdk.</summary>
public static class WpfWindowExtensions
{
    /// <summary>
    /// Adopts <paramref name="window"/> as the app's main window (see <see cref="WpfWindowManager.AttachMainWindow"/>): it is
    /// reported by <see cref="IWindowManager.MainWindow"/>, parents pickers, the share UI and Windows Hello, drives the app
    /// lifecycle and, with <paramref name="persistPlacement"/>, reopens where the user left it. Call it from the window's
    /// constructor, before the window is shown.
    /// </summary>
    /// <param name="window">The main window.</param>
    /// <param name="services">The app's service provider.</param>
    /// <param name="id">The window id, also the placement key. Default: <c>main</c>.</param>
    /// <param name="persistPlacement">Restore and save the window's placement. Default: <see langword="true"/>.</param>
    /// <returns>The adopted window, or <see cref="NativeErrorCodes.Conflict"/> when another main window is attached.</returns>
    /// <exception cref="InvalidOperationException"><c>AddWpf()</c> was not called on the service collection.</exception>
    public static NativeResult<WpfNativeWindow> AttachBlazorNativeSdk(
        this Window window,
        IServiceProvider services,
        string id = WpfWindowManager.DefaultMainWindowId,
        bool persistPlacement = true)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(services);
        return services.GetRequiredService<WpfWindowManager>().AttachMainWindow(window, id, persistPlacement);
    }
}
