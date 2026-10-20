using BlazorNativeSdk.Browser.Interop;
using BlazorNativeSdk.Browser.Services;
using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.Logging;

namespace BlazorNativeSdk.Browser.Hosting;

/// <summary>
/// Starts the browser listeners of one scope (the WebAssembly app, or one Blazor Server circuit): page lifecycle,
/// connectivity, notification taps and launch activations. Idempotent; failures while prerendering are retried later.
/// </summary>
internal sealed partial class BrowserStartup
{
    private readonly IAppLifecycle _lifecycle;
    private readonly IConnectivityService _connectivity;
    private readonly BrowserActivationBridge _activation;
    private readonly ILogger<BrowserStartup> _logger;

    public BrowserStartup(IAppLifecycle lifecycle, IConnectivityService connectivity, BrowserActivationBridge activation, ILogger<BrowserStartup> logger)
    {
        _lifecycle = lifecycle;
        _connectivity = connectivity;
        _activation = activation;
        _logger = logger;
    }

    /// <summary>Starts every listener that is not running yet.</summary>
    /// <returns><see langword="true"/> when everything is running.</returns>
    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<(string Name, NativeResult Result)>(3);

        // Respect overrides: only start the browser implementations.
        if (_lifecycle is BrowserAppLifecycle lifecycle)
        {
            results.Add(("lifecycle", await lifecycle.StartAsync(cancellationToken).ConfigureAwait(false)));
        }

        if (_connectivity is BrowserConnectivityService connectivity)
        {
            results.Add(("connectivity", await connectivity.StartAsync(cancellationToken).ConfigureAwait(false)));
        }

        results.Add(("activation", await _activation.StartAsync(cancellationToken).ConfigureAwait(false)));

        var allStarted = true;
        foreach (var (name, result) in results)
        {
            if (result.IsSuccess)
            {
                continue;
            }

            allStarted = false;
            if (BrowserInterop.IsHostNotReady(result.Error))
            {
                LogDeferred(_logger, name);
            }
            else
            {
                LogFailed(_logger, name, result.Error.Code);
            }
        }

        return allStarted;
    }

    [LoggerMessage(EventId = 4201, Level = LogLevel.Debug, Message = "Browser {Component} not started yet: JavaScript is not available (prerendering). It starts on first interactive use.")]
    private static partial void LogDeferred(ILogger logger, string component);

    [LoggerMessage(EventId = 4202, Level = LogLevel.Warning, Message = "Starting browser {Component} failed: {Code}.")]
    private static partial void LogFailed(ILogger logger, string component, string code);
}

/// <summary>Runs <see cref="BrowserStartup"/> when the SDK starts (Blazor WebAssembly, where all browser services are singletons).</summary>
internal sealed class BrowserStartupTask : INativeSdkStartupTask
{
    private readonly BrowserStartup _startup;

    public BrowserStartupTask(BrowserStartup startup) => _startup = startup;

    public async ValueTask StartAsync(CancellationToken cancellationToken) =>
        await _startup.StartAsync(cancellationToken).ConfigureAwait(false);
}
