#if WINDOWS
using Microsoft.Maui.LifecycleEvents;

namespace BlazorNativeSdk.Maui;

/// <summary>Wires MAUI (WinUI) window events to the window tracker.</summary>
internal static class WindowsLifecycleWiring
{
    /// <summary>Registers the lifecycle handlers.</summary>
    public static void Configure(IWindowsLifecycleBuilder windows, MauiIntegration integration)
    {
        windows
            // Raised after the MAUI window's handler exists and before WinUI activates (shows) it: the application exists by
            // now, so attach and track the window early enough to restore its placement without flicker.
            .OnWindowCreated(window => integration.Tracker?.EnsureAttached())
            .OnPlatformMessage((window, args) => integration.Tracker?.OnPlatformMessage(args.Hwnd, args.MessageId, args.WParam, args.LParam));
    }
}
#endif
