using System.Collections.Frozen;
using BlazorNativeSdk.Browser.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk.Browser;

/// <summary>
/// Registers the app as the handler of <c>web+</c> URL schemes with <c>navigator.registerProtocolHandler</c>. When the user
/// opens such a link the browser loads the app with <c>?bns-link=&lt;url&gt;</c> (see
/// <see cref="BrowserNativeOptions.ProtocolHandlerQueryParameter"/>), and the adapter publishes a
/// <see cref="Lifecycle.ActivationKind.DeepLink"/> activation for it.
/// </summary>
/// <remarks>
/// Add the scheme to <c>DeepLinkOptions.Schemes</c> (<c>builder.ConfigureDeepLinks(o =&gt; o.Schemes.Add("web+myapp"))</c>)
/// so that <c>web+myapp://orders/42</c> is mapped to the route <c>/orders/42</c>. Chromium-based browsers and Firefox on
/// desktop support protocol handlers; Safari and mobile browsers do not. Inject this service; it is registered by
/// <c>AddBrowser()</c>.
/// </remarks>
public sealed partial class BrowserProtocolHandlers
{
    private static readonly FrozenSet<string> s_safelisted = new[]
    {
        "bitcoin", "ftp", "ftps", "geo", "im", "irc", "ircs", "magnet", "mailto", "matrix", "mms", "news", "nntp",
        "openpgp4fpr", "sftp", "sip", "sms", "smsto", "ssh", "tel", "urn", "webcal", "wtai", "xmpp",
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly BrowserInterop _interop;
    private readonly NativeSdkOptions _sdkOptions;
    private readonly ILogger<BrowserProtocolHandlers> _logger;

    internal BrowserProtocolHandlers(BrowserInterop interop, IOptions<NativeSdkOptions> sdkOptions, ILogger<BrowserProtocolHandlers> logger)
    {
        _interop = interop;
        _sdkOptions = sdkOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Asks the browser to open links with <paramref name="scheme"/> (for example <c>web+myapp</c>) in this app. Browsers
    /// typically ask the user to confirm. Call it from a user gesture.
    /// </summary>
    /// <param name="scheme">A <c>web+</c> scheme with lowercase ASCII letters, or a scheme on the browser safelist (<c>mailto</c>, <c>tel</c>, ...).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// Success when the browser accepted the registration (the user may still decline it later in the browser UI),
    /// <see cref="NativeResultStatus.Unsupported"/> when the browser has no protocol handler support, or a failure for an
    /// invalid scheme.
    /// </returns>
    public async ValueTask<NativeResult> RegisterAsync(string scheme, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        var normalized = scheme.Trim().TrimEnd(':').ToLowerInvariant();
        if (!IsValidScheme(normalized))
        {
            return NativeError.Failed(NativeCapabilities.DeepLinks, NativeErrorCodes.InvalidArgument,
                $"'{scheme}' cannot be registered: use 'web+' followed by lowercase ASCII letters (for example 'web+myapp') or a safelisted scheme such as 'mailto'.");
        }

        if (!_sdkOptions.DeepLinks.Schemes.Contains(normalized))
        {
            LogSchemeNotMapped(_logger, normalized);
        }

        var result = await _interop.InvokeAsync(
            NativeCapabilities.DeepLinks, "registerProtocolHandler", [normalized, _interop.Options.ProtocolHandlerQueryParameter], cancellationToken).ConfigureAwait(false);
        return result.WithoutValue();
    }

    /// <summary>Returns whether a scheme may be passed to <c>navigator.registerProtocolHandler</c>.</summary>
    internal static bool IsValidScheme(string scheme)
    {
        if (s_safelisted.Contains(scheme))
        {
            return true;
        }

        if (!scheme.StartsWith("web+", StringComparison.Ordinal) || scheme.Length == 4)
        {
            return false;
        }

        foreach (var c in scheme.AsSpan(4))
        {
            if (c is < 'a' or > 'z')
            {
                return false;
            }
        }

        return true;
    }

    [LoggerMessage(EventId = 4101, Level = LogLevel.Warning, Message = "Protocol handler '{Scheme}' is not listed in DeepLinkOptions.Schemes; links with it will be delivered to activation handlers but not navigated to.")]
    private static partial void LogSchemeNotMapped(ILogger logger, string scheme);
}
