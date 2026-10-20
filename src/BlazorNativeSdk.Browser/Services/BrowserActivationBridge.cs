using BlazorNativeSdk.Browser.Interop;
using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.Logging;

namespace BlazorNativeSdk.Browser.Services;

/// <summary>Turns browser launch information into <see cref="AppActivation"/> values. Pure functions.</summary>
internal static class BrowserActivationMapper
{
    /// <summary>The activation for a URL received through a protocol handler (<c>?bns-link=web+myapp://...</c>).</summary>
    public static AppActivation? FromProtocolLink(string? link, DateTimeOffset receivedAt)
    {
        if (string.IsNullOrWhiteSpace(link) || !Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        return new AppActivation
        {
            Kind = ActivationKind.DeepLink,
            Uri = uri,
            IsColdStart = true,
            ReceivedAt = receivedAt,
            Source = BrowserConstants.AdapterName,
        };
    }

    /// <summary>The activation for a PWA launch delivered by <c>window.launchQueue</c>.</summary>
    public static AppActivation? FromLaunchTarget(LaunchTarget target, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!Uri.TryCreate(target.TargetUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return new AppActivation
        {
            Kind = ActivationKind.DeepLink,
            Uri = uri,
            Route = string.IsNullOrWhiteSpace(target.Route) ? null : target.Route,
            IsColdStart = target.IsColdStart,
            ReceivedAt = receivedAt,
            Source = BrowserConstants.AdapterName,
        };
    }

    /// <summary>The activation for a notification tap or action.</summary>
    public static AppActivation FromNotificationClick(NotificationClickDto click, bool isColdStart, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(click);
        return new AppActivation
        {
            Kind = ActivationKind.Notification,
            NotificationId = click.Id,
            ActionId = string.IsNullOrEmpty(click.Action) ? null : click.Action,
            UserText = click.Reply,
            Route = string.IsNullOrWhiteSpace(click.Route) ? null : click.Route,
            Data = click.Data is null ? new Dictionary<string, string>() : new Dictionary<string, string>(click.Data, StringComparer.Ordinal),
            IsColdStart = isColdStart,
            ReceivedAt = receivedAt,
            Source = BrowserConstants.AdapterName,
        };
    }
}

/// <summary>
/// Publishes browser activations (protocol-handler links, PWA launches, notification taps) through
/// <see cref="IActivationPublisher"/>. Created and started by <see cref="Hosting.BrowserStartup"/>.
/// </summary>
internal sealed partial class BrowserActivationBridge : IDisposable, IAsyncDisposable
{
    private readonly BrowserInterop _interop;
    private readonly IActivationPublisher _publisher;
    private readonly TimeProvider _time;
    private readonly ILogger<BrowserActivationBridge> _logger;
    private readonly BrowserStartGate _start;
    private int _disposed;

    public BrowserActivationBridge(BrowserInterop interop, IActivationPublisher publisher, TimeProvider time, ILogger<BrowserActivationBridge> logger)
    {
        _interop = interop;
        _publisher = publisher;
        _time = time;
        _logger = logger;
        _start = new BrowserStartGate(StartCoreAsync);
        _interop.Callbacks.NotificationClicked += OnNotificationClicked;
        _interop.Callbacks.Launched += OnLaunched;
    }

    /// <summary>Reads the launch information of the page once and publishes the launch activations.</summary>
    public Task<NativeResult> StartAsync(CancellationToken cancellationToken = default) => _start.EnsureStartedAsync(cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _interop.Callbacks.NotificationClicked -= OnNotificationClicked;
            _interop.Callbacks.Launched -= OnLaunched;
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private async ValueTask<NativeResult> StartCoreAsync(CancellationToken cancellationToken)
    {
        // The module removes the launch parameters from the address bar after reading them, so a reload does not replay them.
        var result = await _interop.InvokeAsync(NativeCapabilities.DeepLinks, "launchInfo", [], cancellationToken).ConfigureAwait(false);
        var info = BrowserInterop.Read(NativeCapabilities.DeepLinks, result, BrowserJsonContext.Default.LaunchInfoDto);
        if (!info.IsSuccess)
        {
            return info.Error;
        }

        var now = _time.GetUtcNow();
        if (info.Value.Link is { } link)
        {
            if (BrowserActivationMapper.FromProtocolLink(link, now) is { } activation)
            {
                await PublishAsync(activation).ConfigureAwait(false);
            }
            else
            {
                LogInvalidLink(_logger);
            }
        }

        if (info.Value.NotificationClick is { } click)
        {
            await PublishAsync(BrowserActivationMapper.FromNotificationClick(click, isColdStart: true, now)).ConfigureAwait(false);
        }

        return NativeResult.Ok;
    }

    private void OnNotificationClicked(NotificationClickDto click) =>
        _ = PublishAsync(BrowserActivationMapper.FromNotificationClick(click, isColdStart: false, _time.GetUtcNow())).AsTask();

    private void OnLaunched(LaunchTarget target)
    {
        if (BrowserActivationMapper.FromLaunchTarget(target, _time.GetUtcNow()) is { } activation)
        {
            _ = PublishAsync(activation).AsTask();
        }
    }

    private async ValueTask PublishAsync(AppActivation activation)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            await _publisher.PublishAsync(activation).ConfigureAwait(false);
            LogPublished(_logger, activation.Kind);
        }
        catch (Exception ex)
        {
            LogPublishFailed(_logger, activation.Kind, ex);
        }
    }

    [LoggerMessage(EventId = 3801, Level = LogLevel.Debug, Message = "Browser {Kind} activation published.")]
    private static partial void LogPublished(ILogger logger, ActivationKind kind);

    [LoggerMessage(EventId = 3802, Level = LogLevel.Error, Message = "Publishing a browser {Kind} activation failed.")]
    private static partial void LogPublishFailed(ILogger logger, ActivationKind kind, Exception exception);

    [LoggerMessage(EventId = 3803, Level = LogLevel.Information, Message = "The protocol-handler query parameter did not contain an absolute URL and was ignored.")]
    private static partial void LogInvalidLink(ILogger logger);
}
