using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk.Lifecycle;

/// <summary>A UI surface that can navigate to app routes (typically one per Blazor root / window).</summary>
public interface INavigationOutlet
{
    /// <summary>Whether this outlet should receive navigations in preference to others (the main window).</summary>
    bool IsPrimary { get; }

    /// <summary>Navigates to an app route on the UI thread.</summary>
    ValueTask NavigateAsync(string route, CancellationToken cancellationToken);
}

/// <summary>Attaches navigation outlets to the activation pipeline.</summary>
public interface INavigationOutletRegistry
{
    /// <summary>Attaches an outlet. Buffered activations are replayed once the first outlet attaches. Dispose to detach.</summary>
    IDisposable Attach(INavigationOutlet outlet);

    /// <summary>
    /// Marks the pipeline ready without a navigation outlet (headless hosts, or apps that handle all activations themselves).
    /// Buffered activations are processed; route navigation is skipped while no outlet is attached.
    /// </summary>
    void MarkReady();
}

/// <summary>
/// Default implementation of <see cref="IAppActivationService"/> and <see cref="IActivationPublisher"/>: a single ordered
/// queue that buffers activations until the UI is ready, runs handlers, resolves routes safely and navigates.
/// </summary>
public sealed partial class ActivationService : IAppActivationService, IActivationPublisher, INavigationOutletRegistry, IAsyncDisposable, IDisposable
{
    private readonly Channel<AppActivation> _queue;
    private readonly Lock _gate = new();
    private readonly List<Func<AppActivation, CancellationToken, ValueTask<bool>>> _handlers = [];
    private readonly List<(RouteTemplate Template, Func<ActivationRouteMatch, CancellationToken, ValueTask> Handler)> _routes = [];
    private readonly List<INavigationOutlet> _outlets = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly DeepLinkOptions _options;
    private readonly ILogger<ActivationService> _logger;
    private readonly IActivationTranslator[] _translators;
    private readonly IServiceScopeFactory? _scopes;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _processor;
    private AppActivation? _last;
    private int _outstanding;
    private int _disposed;

    /// <summary>Creates the service.</summary>
    /// <param name="options">The SDK options.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="translators">Translators applied, in registration order, to every published activation.</param>
    /// <param name="scopes">Creates the scope from which <see cref="IActivationHandler"/>s are resolved for each activation.</param>
    public ActivationService(
        IOptions<NativeSdkOptions> options,
        ILogger<ActivationService> logger,
        IEnumerable<IActivationTranslator>? translators = null,
        IServiceScopeFactory? scopes = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value.DeepLinks;
        _logger = logger;
        _translators = translators?.ToArray() ?? [];
        _scopes = scopes;
        _queue = Channel.CreateBounded<AppActivation>(
            new BoundedChannelOptions(Math.Max(1, _options.MaxQueuedActivations))
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest,
            },
            dropped =>
            {
                Interlocked.Decrement(ref _outstanding);
                LogDropped(_logger, dropped);
            });
    }

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.DeepLinks;

    /// <inheritdoc />
    public AppActivation? LaunchActivation { get; private set; }

    /// <summary><see langword="true"/> once an outlet attached or <see cref="MarkReady"/> was called.</summary>
    public bool IsReady => _ready.Task.IsCompleted;

    /// <summary>Completes once the UI is ready and every published activation has been dispatched (useful in tests).</summary>
    public async Task WhenIdleAsync(CancellationToken cancellationToken = default)
    {
        await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        while (Volatile.Read(ref _outstanding) > 0)
        {
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var features = new List<string> { "routing" };
        if (_options.Schemes.Count > 0)
        {
            features.Add("custom-scheme");
        }

        if (_options.Hosts.Count > 0)
        {
            features.Add("verified-links");
        }

        return ValueTask.FromResult(CapabilityStatus.Available(Capability, "core", features));
    }

    /// <inheritdoc />
    public IDisposable Subscribe(Func<AppActivation, CancellationToken, ValueTask<bool>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _handlers.Add(handler);
        }

        return new Disposer(() =>
        {
            lock (_gate)
            {
                _handlers.Remove(handler);
            }
        });
    }

    /// <inheritdoc />
    public IDisposable MapRoute(string template, Func<ActivationRouteMatch, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var entry = (RouteTemplate.Parse(template), handler);
        lock (_gate)
        {
            _routes.Add(entry);
        }

        return new Disposer(() =>
        {
            lock (_gate)
            {
                _routes.Remove(entry);
            }
        });
    }

    /// <inheritdoc />
    public bool TryResolveRoute(AppActivation activation, [NotNullWhen(true)] out string? route)
    {
        ArgumentNullException.ThrowIfNull(activation);
        route = null;

        if (!string.IsNullOrWhiteSpace(activation.Route))
        {
            if (IsSafeAppRoute(activation.Route))
            {
                route = activation.Route;
                return true;
            }

            return false;
        }

        if (activation.Uri is null)
        {
            return false;
        }

        route = ResolveUri(activation.Uri);
        return route is not null;
    }

    /// <summary>Resolves a URL to an app route using the configured rules, or returns <see langword="null"/>.</summary>
    public string? ResolveUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri)
        {
            var relative = uri.OriginalString;
            return IsSafeAppRoute(relative) ? relative : null;
        }

        if (_options.RouteResolver?.Invoke(uri) is { } custom)
        {
            return IsSafeAppRoute(custom) ? custom : null;
        }

        string path;
        if (_options.Schemes.Contains(uri.Scheme))
        {
            var host = uri.Host;
            var absolutePath = uri.AbsolutePath;
            path = _options.CustomSchemeHostIsFirstSegment && host.Length > 0
                ? "/" + host + (absolutePath == "/" ? string.Empty : absolutePath)
                : absolutePath;
        }
        else if ((uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && _options.Hosts.Contains(uri.Host))
        {
            path = uri.AbsolutePath;
        }
        else
        {
            return null;
        }

        if (path.Length == 0)
        {
            path = "/";
        }

        var candidate = path + uri.Query + uri.Fragment;
        return IsSafeAppRoute(candidate) ? candidate : null;
    }

    /// <inheritdoc />
    public ValueTask PublishAsync(AppActivation activation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activation);
        var translated = Translate(activation);
        if (translated is null)
        {
            return ValueTask.CompletedTask;
        }

        activation = translated;
        if (!IsRecognized(activation))
        {
            LogUnrecognizedLink(_logger, activation.Uri!.Scheme);
            return ValueTask.CompletedTask;
        }

        if (activation.IsBackground)
        {
            return PublishBackgroundAsync(activation);
        }

        lock (_gate)
        {
            if (IsDuplicate(activation))
            {
                LogDuplicate(_logger, activation);
                return ValueTask.CompletedTask;
            }

            _last = activation;
            if (activation.IsColdStart && LaunchActivation is null)
            {
                LaunchActivation = activation;
            }

            _processor ??= Task.Run(ProcessAsync, CancellationToken.None);
            Interlocked.Increment(ref _outstanding);
        }

        if (!_queue.Writer.TryWrite(activation))
        {
            Interlocked.Decrement(ref _outstanding);
        }
        else if (!IsReady)
        {
            LogBuffered(_logger, activation);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public IDisposable Attach(INavigationOutlet outlet)
    {
        ArgumentNullException.ThrowIfNull(outlet);
        lock (_gate)
        {
            _outlets.Add(outlet);
            _processor ??= Task.Run(ProcessAsync, CancellationToken.None);
        }

        _ready.TrySetResult();
        return new Disposer(() =>
        {
            lock (_gate)
            {
                _outlets.Remove(outlet);
            }
        });
    }

    /// <inheritdoc />
    public void MarkReady()
    {
        lock (_gate)
        {
            _processor ??= Task.Run(ProcessAsync, CancellationToken.None);
        }

        _ready.TrySetResult();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _queue.Writer.TryComplete();
            _shutdown.Cancel();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_processor is not null)
        {
            try
            {
                await _processor.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _shutdown.Dispose();
    }

    private AppActivation? Translate(AppActivation activation)
    {
        foreach (var translator in _translators)
        {
            AppActivation? next;
            try
            {
                next = translator.Translate(activation);
            }
            catch (Exception ex)
            {
                LogTranslatorFailed(_logger, translator.GetType().Name, ex);
                continue;
            }

            if (next is null)
            {
                LogRejectedByTranslator(_logger, translator.GetType().Name, activation.Kind);
                return null;
            }

            activation = next;
        }

        return activation;
    }

    /// <summary>
    /// Deep links with an absolute URL and no explicit route must match an allowed scheme or host (or the custom resolver),
    /// unless <see cref="DeepLinkOptions.DeliverUnrecognizedLinks"/> is set. Other activation kinds always pass.
    /// </summary>
    private bool IsRecognized(AppActivation activation)
    {
        if (activation.Kind != ActivationKind.DeepLink || activation.Route is not null || activation.Uri is not { IsAbsoluteUri: true } uri || _options.DeliverUnrecognizedLinks)
        {
            return true;
        }

        if (_options.Schemes.Contains(uri.Scheme))
        {
            return true;
        }

        if ((uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && _options.Hosts.Contains(uri.Host))
        {
            return true;
        }

        return _options.RouteResolver?.Invoke(uri) is not null;
    }

    private ValueTask PublishBackgroundAsync(AppActivation activation)
    {
        // Background activations (e.g. notification actions that do not open the app) may arrive in a process that never
        // shows UI, so they are not queued behind the UI and are never navigated to.
        Interlocked.Increment(ref _outstanding);
        _ = Task.Run(async () =>
        {
            try
            {
                await DispatchAsync(activation, allowNavigation: false, _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                Interlocked.Decrement(ref _outstanding);
            }
        }, CancellationToken.None);
        return ValueTask.CompletedTask;
    }

    private async ValueTask<bool> RunDependencyInjectionHandlersAsync(AppActivation activation, CancellationToken token)
    {
        if (_scopes is null)
        {
            return false;
        }

        await using var scope = _scopes.CreateAsyncScope();
        foreach (var handler in scope.ServiceProvider.GetServices<IActivationHandler>())
        {
            try
            {
                if (await handler.HandleAsync(activation, token).ConfigureAwait(false))
                {
                    LogHandled(_logger, activation);
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                LogHandlerFailed(_logger, activation, ex);
            }
        }

        return false;
    }

    internal static bool IsSafeAppRoute(string route) =>
        route.StartsWith('/') &&
        !route.StartsWith("//", StringComparison.Ordinal) &&
        !route.StartsWith("/\\", StringComparison.Ordinal) &&
        !route.Contains("://", StringComparison.Ordinal) &&
        !route.Any(char.IsControl);

    private bool IsDuplicate(AppActivation activation)
    {
        if (_last is null || activation.Kind == ActivationKind.Launch)
        {
            return false;
        }

        return _last.Kind == activation.Kind &&
               _last.Uri == activation.Uri &&
               _last.Route == activation.Route &&
               _last.NotificationId == activation.NotificationId &&
               _last.ActionId == activation.ActionId &&
               (activation.ReceivedAt - _last.ReceivedAt).Duration() < _options.DuplicateSuppressionWindow;
    }

    private async Task ProcessAsync()
    {
        var token = _shutdown.Token;
        try
        {
            // Activations that arrive before the UI is ready stay in the bounded queue (oldest dropped first)
            // and are replayed in order once an outlet attaches or MarkReady is called.
            await _ready.Task.WaitAsync(token).ConfigureAwait(false);
            await foreach (var activation in _queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    await DispatchAsync(activation, allowNavigation: true, token).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _outstanding);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task DispatchAsync(AppActivation activation, bool allowNavigation, CancellationToken token)
    {
        if (await RunDependencyInjectionHandlersAsync(activation, token).ConfigureAwait(false))
        {
            return;
        }

        Func<AppActivation, CancellationToken, ValueTask<bool>>[] handlers;
        (RouteTemplate Template, Func<ActivationRouteMatch, CancellationToken, ValueTask> Handler)[] routes;
        lock (_gate)
        {
            handlers = [.. _handlers];
            routes = [.. _routes];
        }

        foreach (var handler in handlers)
        {
            try
            {
                if (await handler(activation, token).ConfigureAwait(false))
                {
                    LogHandled(_logger, activation);
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                LogHandlerFailed(_logger, activation, ex);
            }
        }

        if (!TryResolveRoute(activation, out var route))
        {
            if (activation.Uri is not null)
            {
                LogUnmappedUri(_logger, activation.Uri);
            }

            return;
        }

        foreach (var (template, handler) in routes)
        {
            if (template.TryMatch(route, out var values))
            {
                try
                {
                    await handler(new ActivationRouteMatch(activation, route, values), token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    LogHandlerFailed(_logger, activation, ex);
                }

                return;
            }
        }

        if (allowNavigation && _options.NavigateToResolvedRoutes)
        {
            var outlet = SelectOutlet();
            if (outlet is null)
            {
                LogNoOutlet(_logger, route);
                return;
            }

            try
            {
                await outlet.NavigateAsync(route, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                LogHandlerFailed(_logger, activation, ex);
            }
        }
    }

    private INavigationOutlet? SelectOutlet()
    {
        lock (_gate)
        {
            for (var i = _outlets.Count - 1; i >= 0; i--)
            {
                if (_outlets[i].IsPrimary)
                {
                    return _outlets[i];
                }
            }

            return _outlets.Count > 0 ? _outlets[^1] : null;
        }
    }

    [LoggerMessage(EventId = 1201, Level = LogLevel.Debug, Message = "Activation {Activation} buffered until the UI is ready.")]
    private static partial void LogBuffered(ILogger logger, AppActivation activation);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Warning, Message = "Activation {Activation} dropped: the buffer is full.")]
    private static partial void LogDropped(ILogger logger, AppActivation activation);

    [LoggerMessage(EventId = 1203, Level = LogLevel.Debug, Message = "Activation {Activation} handled by a subscriber.")]
    private static partial void LogHandled(ILogger logger, AppActivation activation);

    [LoggerMessage(EventId = 1204, Level = LogLevel.Error, Message = "An activation handler failed for {Activation}.")]
    private static partial void LogHandlerFailed(ILogger logger, AppActivation activation, Exception exception);

    [LoggerMessage(EventId = 1205, Level = LogLevel.Information, Message = "URL {Uri} does not match any allowed scheme or host and was not navigated to.")]
    private static partial void LogUnmappedUri(ILogger logger, Uri uri);

    [LoggerMessage(EventId = 1206, Level = LogLevel.Warning, Message = "No navigation outlet is attached; route {Route} was not navigated to. Add <NativeActivationOutlet /> to your layout.")]
    private static partial void LogNoOutlet(ILogger logger, string route);

    [LoggerMessage(EventId = 1209, Level = LogLevel.Warning, Message = "Activation translator {Translator} rejected a {Kind} activation.")]
    private static partial void LogRejectedByTranslator(ILogger logger, string translator, ActivationKind kind);

    [LoggerMessage(EventId = 1210, Level = LogLevel.Warning, Message = "A deep link with the unrecognized scheme '{Scheme}' (or host) was dropped. Allow it in DeepLinkOptions or set DeliverUnrecognizedLinks.")]
    private static partial void LogUnrecognizedLink(ILogger logger, string scheme);

    [LoggerMessage(EventId = 1208, Level = LogLevel.Error, Message = "Activation translator {Translator} failed; the activation was passed on unchanged.")]
    private static partial void LogTranslatorFailed(ILogger logger, string translator, Exception exception);

    [LoggerMessage(EventId = 1207, Level = LogLevel.Debug, Message = "Duplicate activation {Activation} ignored.")]
    private static partial void LogDuplicate(ILogger logger, AppActivation activation);

    private sealed class Disposer(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}
