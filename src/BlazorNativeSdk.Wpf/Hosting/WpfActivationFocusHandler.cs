using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk.Wpf;

/// <summary>
/// Brings the main window to the front when an activation reaches the running app (forwarded deep link or launch,
/// notification click), so the user sees where it was routed. Never claims the activation.
/// </summary>
internal sealed class WpfActivationFocusHandler : IActivationHandler
{
    private readonly WpfWindowManager _windows;
    private readonly WpfNativeOptions _options;

    public WpfActivationFocusHandler(WpfWindowManager windows, IOptions<WpfNativeOptions> options)
    {
        _windows = windows;
        _options = options.Value;
    }

    public async ValueTask<bool> HandleAsync(AppActivation activation, CancellationToken cancellationToken)
    {
        if (ShouldFocus(activation, _options.FocusMainWindowOnActivation) && _windows.MainNativeWindow is { IsClosed: false } main)
        {
            // A refused foreground change flashes the taskbar button instead; that is not a reason to stop routing.
            _ = await main.FocusAsync(cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>Foreground activations of a running app focus the main window; cold starts show it anyway.</summary>
    internal static bool ShouldFocus(AppActivation activation, bool enabled) =>
        enabled && !activation.IsBackground && !activation.IsColdStart;
}
