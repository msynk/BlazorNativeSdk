using System.Diagnostics.CodeAnalysis;

namespace BlazorNativeSdk.Lifecycle;

/// <summary>Why the app was activated.</summary>
public enum ActivationKind
{
    /// <summary>A normal launch without extra payload.</summary>
    Launch,

    /// <summary>A URL: custom scheme, Android App Link, iOS Universal Link, browser protocol handler or PWA launch.</summary>
    DeepLink,

    /// <summary>The user tapped a notification or one of its actions.</summary>
    Notification,

    /// <summary>An app shortcut, jump list entry or quick action.</summary>
    Shortcut,

    /// <summary>The app was asked to open a file.</summary>
    File,

    /// <summary>A second instance of a single-instance desktop app forwarded its arguments.</summary>
    SecondaryInstance,

    /// <summary>An adapter-specific activation.</summary>
    Custom,
}

/// <summary>An activation of the app: deep link, notification tap, shortcut, forwarded launch, ...</summary>
public sealed record AppActivation
{
    /// <summary>The kind.</summary>
    public required ActivationKind Kind { get; init; }

    /// <summary>The URL that activated the app, when any.</summary>
    public Uri? Uri { get; init; }

    /// <summary>An explicit app route (for example <see cref="Devices.LocalNotification.Route"/>). Takes precedence over <see cref="Uri"/> mapping.</summary>
    public string? Route { get; init; }

    /// <summary>The notification id for notification activations.</summary>
    public string? NotificationId { get; init; }

    /// <summary>The chosen action id for notification action activations; <see langword="null"/> for a plain tap.</summary>
    public string? ActionId { get; init; }

    /// <summary>Text typed by the user in a text-input notification action.</summary>
    public string? UserText { get; init; }

    /// <summary>Custom data (notification data, shortcut data, query values).</summary>
    public IReadOnlyDictionary<string, string> Data { get; init; } = new Dictionary<string, string>();

    /// <summary>Raw command-line arguments for launch and secondary-instance activations.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Files passed to the app for <see cref="ActivationKind.File"/> activations.</summary>
    public IReadOnlyList<Devices.NativeFile> Files { get; init; } = [];

    /// <summary><see langword="true"/> when this activation launched the process (as opposed to reaching a running app).</summary>
    public bool IsColdStart { get; init; }

    /// <summary>
    /// <see langword="true"/> when the activation does not bring the app to the foreground (for example a notification action
    /// with <see cref="Devices.NotificationAction.OpensApp"/> set to <see langword="false"/>). Background activations are
    /// dispatched to handlers immediately, without waiting for the UI, and are never navigated to.
    /// </summary>
    public bool IsBackground { get; init; }

    /// <summary>When the adapter received the activation.</summary>
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The adapter that produced the activation (diagnostics).</summary>
    public string? Source { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"{Kind} {Route ?? Uri?.ToString()}{(ActionId is null ? string.Empty : " action=" + ActionId)}";
}

/// <summary>The result of matching an activation against a route template registered with <see cref="IAppActivationService.MapRoute"/>.</summary>
/// <param name="Activation">The activation.</param>
/// <param name="Route">The app route, such as <c>/orders/42</c>.</param>
/// <param name="Values">Route values captured by the template, such as <c>id = 42</c>.</param>
public sealed record ActivationRouteMatch(AppActivation Activation, string Route, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// Single entry point for every way the app can be activated. Adapters publish activations through
/// <see cref="IActivationPublisher"/>; this service buffers them until the Blazor UI is ready, runs handlers and, by
/// default, navigates to the mapped app route.
/// </summary>
/// <remarks>
/// Processing order for each activation:
/// <list type="number">
/// <item><see cref="IActivationTranslator"/>s registered in dependency injection, when the activation is published.</item>
/// <item><see cref="IActivationHandler"/>s registered in dependency injection, then handlers registered with <see cref="Subscribe"/>,
/// in registration order, until one returns <see langword="true"/>.</item>
/// <item>Route handlers registered with <see cref="MapRoute"/>, when the activation resolves to a matching route.</item>
/// <item>Navigation to the resolved route through the active navigation outlet (when navigation is enabled).</item>
/// </list>
/// Activations that arrive before a navigation outlet is attached (cold start) are queued and replayed in order.
/// Activations marked <see cref="AppActivation.IsBackground"/> skip the queue and are never navigated to.
/// URLs are only mapped to routes when their scheme and host are allowed by the deep-link options, so untrusted links
/// cannot navigate to arbitrary locations.
/// </remarks>
public interface IAppActivationService : INativeCapability
{
    /// <summary>The activation that launched the process, when known.</summary>
    AppActivation? LaunchActivation { get; }

    /// <summary>Registers a handler that observes every activation. Return <see langword="true"/> to mark it handled.</summary>
    IDisposable Subscribe(Func<AppActivation, CancellationToken, ValueTask<bool>> handler);

    /// <summary>
    /// Registers a handler for activations that resolve to a route matching <paramref name="template"/>, such as
    /// <c>/orders/{id}</c> or <c>/files/{**path}</c>. A matched route is considered handled and is not navigated to.
    /// </summary>
    IDisposable MapRoute(string template, Func<ActivationRouteMatch, CancellationToken, ValueTask> handler);

    /// <summary>Resolves the app route for an activation using the configured deep-link rules.</summary>
    bool TryResolveRoute(AppActivation activation, [NotNullWhen(true)] out string? route);
}

/// <summary>Adapter-facing entry point used to report activations.</summary>
public interface IActivationPublisher
{
    /// <summary>Reports an activation. Safe to call before the UI is ready and from any thread.</summary>
    ValueTask PublishAsync(AppActivation activation, CancellationToken cancellationToken = default);
}

/// <summary>
/// Rewrites platform-specific activations into canonical ones before they are routed. Adapters register translators
/// (for example to turn a signed toast-launch URL back into a <see cref="ActivationKind.Notification"/> activation) instead
/// of wrapping <see cref="IActivationPublisher"/>, so translators compose regardless of which adapter owns the pipeline.
/// </summary>
public interface IActivationTranslator
{
    /// <summary>Translates an activation.</summary>
    /// <param name="activation">The published activation (possibly already translated by an earlier translator).</param>
    /// <returns>
    /// The activation to route: the same instance when the translator does not apply, a replacement, or
    /// <see langword="null"/> to drop it (for example a forged URL whose signature does not verify).
    /// </returns>
    AppActivation? Translate(AppActivation activation);
}

/// <summary>
/// An activation handler registered in dependency injection. Unlike handlers added with
/// <see cref="IAppActivationService.Subscribe"/>, these exist from the moment the app starts, so they also receive
/// activations that arrive before any component renders, including background activations in a process that never shows
/// UI. Handlers are resolved from a new scope per activation and run before subscribed handlers.
/// </summary>
public interface IActivationHandler
{
    /// <summary>Handles an activation. Return <see langword="true"/> to mark it handled and stop further processing.</summary>
    ValueTask<bool> HandleAsync(AppActivation activation, CancellationToken cancellationToken);
}
