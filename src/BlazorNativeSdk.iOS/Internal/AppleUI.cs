using MainThread = Microsoft.Maui.ApplicationModel.MainThread;
using MauiPlatform = Microsoft.Maui.ApplicationModel.Platform;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>UIKit presentation helpers. Every member must be called on the main thread unless stated otherwise.</summary>
internal static class AppleUI
{
    /// <summary>Runs <paramref name="action"/> on the main thread.</summary>
    public static Task<T> OnMainThreadAsync<T>(Func<Task<T>> action) => MainThread.InvokeOnMainThreadAsync(action);

    /// <summary>Runs <paramref name="action"/> on the main thread.</summary>
    public static Task<T> OnMainThreadAsync<T>(Func<T> action) => MainThread.InvokeOnMainThreadAsync(action);

    /// <summary>Runs <paramref name="action"/> on the main thread.</summary>
    public static Task OnMainThreadAsync(Action action) => MainThread.InvokeOnMainThreadAsync(action);

    /// <summary>The error returned when no window can present UI.</summary>
    public static NativeError NoPresenter(CapabilityId capability) =>
        NativeError.Unavailable(capability, NativeErrorCodes.HostNotReady,
            "No foreground window is available to present native UI. Call this operation while the app is active.");

    /// <summary>Returns the top-most view controller of the active window that is not being dismissed.</summary>
    public static UIViewController? GetTopViewController()
    {
        UIViewController? controller = null;
        try
        {
            controller = MauiPlatform.GetCurrentUIViewController();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)
        {
            // Essentials throws when it has no window; fall back to scene inspection.
        }

        controller ??= GetActiveWindow()?.RootViewController;
        if (controller is null)
        {
            return null;
        }

        while (controller.PresentedViewController is { IsBeingDismissed: false } presented)
        {
            controller = presented;
        }

        return controller;
    }

    /// <summary>Anchors a popover (iPad, Mac) to the center of the presenting view.</summary>
    public static void AnchorPopover(UIViewController presented, UIViewController presenter)
    {
        if (presented.PopoverPresentationController is not { } popover || presenter.View is not { } view)
        {
            return;
        }

        var bounds = view.Bounds;
        popover.SourceView = view;
        popover.SourceRect = new CGRect(bounds.GetMidX(), bounds.GetMidY(), 0, 0);
        popover.PermittedArrowDirections = 0;
    }

    /// <summary>Releases a UIKit object on the main thread (UIKit objects must not be deallocated on other threads). Any thread.</summary>
    public static void ReleaseOnMainThread(NSObject value)
    {
        if (NSThread.IsMain)
        {
            value.Dispose();
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(value.Dispose);
        }
    }

    /// <summary>Dismisses <paramref name="controller"/> when it is still presented.</summary>
    public static async Task DismissAsync(UIViewController controller)
    {
        if (controller.PresentingViewController is not null && !controller.IsBeingDismissed)
        {
            await controller.DismissViewControllerAsync(true).ConfigureAwait(true);
        }
    }

    /// <summary>Dismisses the top-most presented controller when it is a <typeparamref name="T"/> (cancellation of system pickers).</summary>
    public static Task DismissTopIfAsync<T>()
        where T : UIViewController =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            if (GetTopViewController() is T top)
            {
                await DismissAsync(top).ConfigureAwait(true);
            }
        });

    private static UIWindow? GetActiveWindow()
    {
        UIWindow? fallback = null;
        foreach (var scene in UIApplication.SharedApplication.ConnectedScenes.ToArray())
        {
            if (scene is not UIWindowScene windowScene)
            {
                continue;
            }

            var window = windowScene.KeyWindow ?? windowScene.Windows.FirstOrDefault(w => w.IsKeyWindow) ?? windowScene.Windows.FirstOrDefault();
            if (window is null)
            {
                continue;
            }

            if (windowScene.ActivationState == UISceneActivationState.ForegroundActive)
            {
                return window;
            }

            fallback ??= windowScene.ActivationState == UISceneActivationState.ForegroundInactive ? window : null;
        }

        return fallback;
    }
}
