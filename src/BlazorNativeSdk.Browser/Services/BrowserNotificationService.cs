using System.Text.Json;
using BlazorNativeSdk.Browser.Interop;
using BlazorNativeSdk.Devices;
using Microsoft.Extensions.Logging;

namespace BlazorNativeSdk.Browser.Services;

/// <summary>
/// The Notifications API. With an active service worker registration notifications are shown with
/// <c>registration.showNotification</c> (action buttons, taps after the page closed when the worker imports
/// <c>blazor-native-sdk-sw.js</c>); otherwise with <c>new Notification()</c>. Taps are published as
/// <see cref="Lifecycle.AppActivation"/> values with <see cref="Lifecycle.ActivationKind.Notification"/>.
/// </summary>
internal sealed partial class BrowserNotificationService : INotificationService
{
    internal const string ScheduleUnsupportedReason =
        "Browsers cannot schedule local notifications for delivery while the page is closed (the Notification Triggers " +
        "proposal was abandoned). Send a Web Push message from your server, or show the notification while the page is open.";

    private readonly BrowserInterop _interop;
    private readonly ILogger<BrowserNotificationService> _logger;

    // The bridge publishes taps; it is injected so that taps are routed even when no startup component is used.
    public BrowserNotificationService(BrowserInterop interop, BrowserActivationBridge activationBridge, ILogger<BrowserNotificationService> logger)
    {
        ArgumentNullException.ThrowIfNull(activationBridge);
        _interop = interop;
        _logger = logger;
    }

    public CapabilityId Capability => NativeCapabilities.Notifications;

    public async ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var probe = await _interop.ProbeAsync(Capability, "notifications", cancellationToken).ConfigureAwait(false);
        if (!probe.IsSuccess)
        {
            return BrowserInteropExtensions.ToStatus(Capability, probe.Error);
        }

        var dto = probe.Value;
        if (!dto.Notification)
        {
            return dto.SecureContext
                ? CapabilityStatus.Unsupported(Capability,
                    "The Notifications API is not available in this browser (on iOS and iPadOS it is only available to web apps added to the home screen).",
                    BrowserConstants.AdapterName)
                : CapabilityStatus.Unavailable(Capability, NativeErrorCodes.InsecureContext,
                    "Notifications are only available in a secure context (HTTPS or localhost).", BrowserConstants.AdapterName);
        }

        var features = new List<string> { NotificationFeatures.ActivationRouting };
        if (dto.ServiceWorkerRegistration && _interop.Options.UseServiceWorkerNotifications && dto.MaxNotificationActions is > 0)
        {
            features.Add(NotificationFeatures.Actions);
        }

        if (dto.Badging)
        {
            features.Add(NotificationFeatures.Badges);
        }

        return dto.NotificationPermission switch
        {
            "granted" => CapabilityStatus.Available(Capability, BrowserConstants.AdapterName, features),
            "denied" => CapabilityStatus.PermissionDenied(Capability, "Notifications are blocked for this site. The user can allow them in the browser's site settings.", BrowserConstants.AdapterName),
            _ => CapabilityStatus.RequiresPermission(Capability, BrowserConstants.AdapterName, features),
        };
    }

    public async ValueTask<NativeResult<PermissionStatus>> RequestPermissionAsync(CancellationToken cancellationToken = default)
    {
        var result = await _interop.InvokeInteractiveAsync(
            Capability, "permissionRequest", BrowserInteropExtensions.NewOperationId(), [NativePermissions.Notifications.Name], cancellationToken,
            NativePermissions.Notifications).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            LogFailed(_logger, "permission", result.Error.Code);
            return result.Error;
        }

        return BrowserErrorMapper.ToPermissionStatus(result.Value.ValueKind == JsonValueKind.String ? result.Value.GetString() : null);
    }

    public async ValueTask<NativeResult> ShowAsync(LocalNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (string.IsNullOrWhiteSpace(notification.Id) || string.IsNullOrWhiteSpace(notification.Title))
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "A notification needs an id and a title.");
        }

        if (notification.BadgeNumber is < 0)
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "BadgeNumber cannot be negative.");
        }

        var request = new NotificationRequestDto
        {
            Id = notification.Id,
            Title = notification.Title,
            Body = notification.Subtitle is { Length: > 0 } subtitle && notification.Body is { Length: > 0 } body
                ? subtitle + "\n" + body
                : notification.Body ?? notification.Subtitle,
            Route = notification.Route,
            Data = notification.Data,
            Actions = [.. notification.Actions.Select(a => new NotificationActionDto { Action = a.Id, Title = a.Title })],
            Silent = notification.Silent,
            RequireInteraction = notification.Importance == NotificationImportance.High,
            Badge = notification.BadgeNumber,
        };

        var result = await _interop.InvokeAsync(
            Capability, "showNotification", [BrowserInterop.Serialize(request, BrowserJsonContext.Default.NotificationRequestDto)], cancellationToken,
            NativePermissions.Notifications).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            LogFailed(_logger, "show", result.Error.Code);
            return result.Error;
        }

        if (request.Actions.Count > 0 &&
            result.Value.ValueKind == JsonValueKind.Object &&
            result.Value.TryGetProperty("actionsShown", out var shown) &&
            shown.ValueKind == JsonValueKind.False)
        {
            LogActionsDropped(_logger);
        }

        return NativeResult.Ok;
    }

    public ValueTask<NativeResult> ScheduleAsync(LocalNotification notification, NotificationSchedule schedule, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(NativeResult.Failure(NativeError.Unsupported(Capability, ScheduleUnsupportedReason)));

    public async ValueTask<NativeResult> CancelAsync(string notificationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notificationId);
        var result = await _interop.InvokeAsync(Capability, "closeNotifications", [notificationId], cancellationToken).ConfigureAwait(false);
        return result.WithoutValue();
    }

    public async ValueTask<NativeResult> CancelAllAsync(CancellationToken cancellationToken = default)
    {
        var result = await _interop.InvokeAsync(Capability, "closeNotifications", [null], cancellationToken).ConfigureAwait(false);
        return result.WithoutValue();
    }

    public ValueTask<NativeResult> CreateChannelAsync(NotificationChannel channel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return ValueTask.FromResult(NativeResult.Ok); // Channels are an Android concept; a successful no-op in browsers.
    }

    [LoggerMessage(EventId = 4001, Level = LogLevel.Debug, Message = "Browser notification {Operation} failed: {Code}.")]
    private static partial void LogFailed(ILogger logger, string operation, string code);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Information, Message = "Notification action buttons were not shown: they require an active service worker registration (check NotificationFeatures.Actions).")]
    private static partial void LogActionsDropped(ILogger logger);
}
