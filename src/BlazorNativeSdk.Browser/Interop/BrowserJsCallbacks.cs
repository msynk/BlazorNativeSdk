using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace BlazorNativeSdk.Browser.Interop;

/// <summary>
/// Receives events from the JavaScript session (lifecycle, connectivity, notification taps, PWA launches) and re-raises
/// them as .NET events. One instance is shared by every browser service in a scope.
/// </summary>
internal sealed partial class BrowserJsCallbacks
{
    private readonly ILogger _logger;

    public BrowserJsCallbacks(ILogger logger) => _logger = logger;

    /// <summary>A page lifecycle event: state (<c>active</c>, <c>inactive</c>, <c>background</c>, <c>terminating</c>) and the DOM event name.</summary>
    public event Action<string, string>? LifecycleChanged;

    /// <summary>A connectivity change.</summary>
    public event Action<ConnectivityDto>? ConnectivityChanged;

    /// <summary>A notification tap or action, from the page or from the service worker.</summary>
    public event Action<NotificationClickDto>? NotificationClicked;

    /// <summary>A PWA launch (<c>window.launchQueue</c>): target URL, same-origin app route (or <see langword="null"/>) and whether it is the launch that loaded the page.</summary>
    public event Action<LaunchTarget>? Launched;

    /// <summary>Called by JavaScript.</summary>
    [JSInvokable]
    public void OnLifecycle(string state, string reason) => Raise(LifecycleChanged, state, reason, nameof(OnLifecycle));

    /// <summary>Called by JavaScript with a JSON <see cref="ConnectivityDto"/>.</summary>
    [JSInvokable]
    public void OnConnectivity(string json)
    {
        var dto = Parse(json, BrowserJsonContext.Default.ConnectivityDto);
        if (dto is not null)
        {
            Raise(ConnectivityChanged, dto, nameof(OnConnectivity));
        }
    }

    /// <summary>Called by JavaScript with a JSON <see cref="NotificationClickDto"/>.</summary>
    [JSInvokable]
    public void OnNotificationClick(string json)
    {
        var dto = Parse(json, BrowserJsonContext.Default.NotificationClickDto);
        if (dto is not null)
        {
            Raise(NotificationClicked, dto, nameof(OnNotificationClick));
        }
    }

    /// <summary>Called by JavaScript when <c>launchQueue</c> delivers a target URL.</summary>
    [JSInvokable]
    public void OnLaunch(string targetUrl, string? route, bool isColdStart) =>
        Raise(Launched, new LaunchTarget(targetUrl, route, isColdStart), nameof(OnLaunch));

    private T? Parse<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException ex)
        {
            LogCallbackFailed(_logger, typeof(T).Name, ex);
            return null;
        }
    }

    private void Raise<T>(Action<T>? handler, T arg, string name)
    {
        try
        {
            handler?.Invoke(arg);
        }
        catch (Exception ex)
        {
            LogCallbackFailed(_logger, name, ex);
        }
    }

    private void Raise<T1, T2>(Action<T1, T2>? handler, T1 arg1, T2 arg2, string name)
    {
        try
        {
            handler?.Invoke(arg1, arg2);
        }
        catch (Exception ex)
        {
            LogCallbackFailed(_logger, name, ex);
        }
    }

    [LoggerMessage(EventId = 3010, Level = LogLevel.Error, Message = "A browser callback handler failed ({Callback}).")]
    private static partial void LogCallbackFailed(ILogger logger, string callback, Exception exception);
}

/// <summary>A PWA launch target delivered by <c>window.launchQueue</c>.</summary>
/// <param name="TargetUrl">The absolute target URL.</param>
/// <param name="Route">The same-origin path, query and fragment, or <see langword="null"/>.</param>
/// <param name="IsColdStart"><see langword="true"/> for the launch that loaded the page.</param>
internal sealed record LaunchTarget(string TargetUrl, string? Route, bool IsColdStart);
