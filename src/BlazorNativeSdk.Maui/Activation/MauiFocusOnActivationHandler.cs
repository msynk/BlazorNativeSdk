#if WINDOWS
using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk.Maui;

/// <summary>
/// Brings the main window to the front when an activation reaches the already running app (a forwarded second launch, a
/// deep link from another app, a toast click), as users expect from desktop apps. Never claims the activation.
/// </summary>
internal sealed partial class MauiFocusOnActivationHandler : IActivationHandler
{
    private readonly IWindowManager _windows;
    private readonly IOptions<MauiNativeOptions> _options;
    private readonly ILogger<MauiFocusOnActivationHandler> _logger;

    /// <summary>Creates the handler.</summary>
    public MauiFocusOnActivationHandler(IWindowManager windows, IOptions<MauiNativeOptions> options, ILogger<MauiFocusOnActivationHandler> logger)
    {
        _windows = windows;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<bool> HandleAsync(AppActivation activation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activation);
        if (!_options.Value.FocusMainWindowOnActivation || activation.IsColdStart || activation.IsBackground)
        {
            return false;
        }

        var open = _windows.Windows;
        var target = _windows.MainWindow ?? (open.Count > 0 ? open[^1] : null);
        if (target is null)
        {
            return false;
        }

        var result = await target.FocusAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            LogFocusFailed(_logger, target.Id, result.Error.Message);
        }

        return false;
    }

    [LoggerMessage(EventId = 8201, Level = LogLevel.Debug, Message = "Could not bring window '{WindowId}' to the front for an activation: {Reason}")]
    private static partial void LogFocusFailed(ILogger logger, string windowId, string reason);
}
#endif
