using System.Diagnostics;

namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>
/// Connects the Windows adapter to the UI framework that hosts the app (WPF, Windows Forms, WinUI/.NET MAUI). Host adapter
/// packages register an implementation that knows the main window and its dispatcher; without one, the adapter falls back
/// to <see cref="DefaultWindowsHostBridge"/>.
/// </summary>
public interface IWindowsHostBridge
{
    /// <summary>The HWND of the application's main window, or <c>0</c> when no window exists yet.</summary>
    /// <remarks>Used to parent pickers, the share UI, Windows Hello prompts and the window menu bar.</remarks>
    nint MainWindowHandle { get; }

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread that owns <see cref="MainWindowHandle"/> and completes when the
    /// returned task completes. Implementations run inline when already on that thread.
    /// </summary>
    Task InvokeOnUIThreadAsync(Func<Task> action);
}

/// <summary>
/// The fallback <see cref="IWindowsHostBridge"/>: the main window is <see cref="Process.MainWindowHandle"/> and work runs
/// inline on the calling thread.
/// </summary>
/// <remarks>
/// This keeps the package usable on its own, but operations that must run on the window's thread (menu bars, context menus
/// owned by the window, file pickers) only work when called from the UI thread. Host adapter packages (WPF, MAUI) replace
/// this bridge with one that dispatches correctly.
/// </remarks>
public sealed class DefaultWindowsHostBridge : IWindowsHostBridge
{
    /// <inheritdoc />
    public nint MainWindowHandle
    {
        get
        {
            using var process = Process.GetCurrentProcess();
            return process.MainWindowHandle;
        }
    }

    /// <inheritdoc />
    public Task InvokeOnUIThreadAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action();
    }
}

/// <summary>Helpers for <see cref="IWindowsHostBridge"/>.</summary>
internal static class WindowsHostBridgeExtensions
{
    /// <summary>Runs a value-producing function on the UI thread.</summary>
    public static async Task<T> InvokeOnUIThreadAsync<T>(this IWindowsHostBridge bridge, Func<Task<T>> func)
    {
        T result = default!;
        await bridge.InvokeOnUIThreadAsync(async () => result = await func().ConfigureAwait(false)).ConfigureAwait(false);
        return result;
    }

    /// <summary>Runs a synchronous function on the UI thread.</summary>
    public static async Task<T> RunOnUIThreadAsync<T>(this IWindowsHostBridge bridge, Func<T> func)
    {
        T result = default!;
        await bridge.InvokeOnUIThreadAsync(() =>
        {
            result = func();
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        return result;
    }
}
