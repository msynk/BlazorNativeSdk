using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>
/// Translates signed toast launch URIs (<c>{NotificationActivationScheme}://bns-toast/?...</c>, delivered by protocol
/// activation through the launch arguments or single-instance forwarding) into <see cref="ActivationKind.Notification"/>
/// activations carrying the notification id, action, route and data. Toast URIs whose signature does not verify are
/// dropped, so web pages and other programs cannot forge notification taps. Everything else passes through unchanged.
/// </summary>
internal sealed partial class WindowsToastActivationTranslator : IActivationTranslator
{
    private readonly string? _scheme;
    private readonly ToastActivationKeyStore _keys;
    private readonly ILogger _logger;

    public WindowsToastActivationTranslator(IOptions<WindowsNativeOptions> options, ToastActivationKeyStore keys, ILogger<WindowsToastActivationTranslator> logger)
    {
        _scheme = string.IsNullOrWhiteSpace(options.Value.NotificationActivationScheme) ? null : options.Value.NotificationActivationScheme;
        _keys = keys;
        _logger = logger;
    }

    public AppActivation? Translate(AppActivation activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        if (_scheme is null || activation.Kind == ActivationKind.Notification || !IsToastUri(activation.Uri))
        {
            return activation;
        }

        try
        {
            if (!ToastLaunchArguments.TryParseProtocolUri(activation.Uri, _scheme, _keys.Key, out var payload))
            {
                LogUnverified(_logger);
                return null;
            }

            // The URI itself is dropped so it is never mapped to a route by the deep-link rules.
            return new AppActivation
            {
                Kind = ActivationKind.Notification,
                NotificationId = payload.NotificationId,
                ActionId = payload.ActionId,
                Route = payload.Route,
                Data = payload.Data,
                Arguments = activation.Arguments,
                IsColdStart = activation.IsColdStart,
                ReceivedAt = activation.ReceivedAt,
                Source = WindowsAdapter.Name,
            };
        }
        catch (Exception ex)
        {
            LogTranslationFailed(_logger, ex);
            return null;
        }
    }

    private bool IsToastUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        string.Equals(uri.Scheme, _scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.Host, ToastLaunchArguments.ProtocolHost, StringComparison.OrdinalIgnoreCase);

    [LoggerMessage(EventId = 3951, Level = LogLevel.Warning, Message = "A toast activation URI had a missing or invalid signature and was dropped.")]
    private static partial void LogUnverified(ILogger logger);

    [LoggerMessage(EventId = 3952, Level = LogLevel.Error, Message = "A toast activation URI could not be translated and was dropped.")]
    private static partial void LogTranslationFailed(ILogger logger, Exception exception);
}
