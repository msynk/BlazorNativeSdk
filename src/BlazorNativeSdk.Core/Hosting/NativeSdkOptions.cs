using System.Reflection;

namespace BlazorNativeSdk;

/// <summary>Application-wide settings used by the SDK and its adapters.</summary>
public sealed class NativeSdkOptions
{
    private string? _appId;

    /// <summary>
    /// A stable application identifier such as <c>com.contoso.notes</c>. Used to name IPC channels, single-instance locks,
    /// app-data folders, keychain services and registry keys. Defaults to the entry assembly name.
    /// </summary>
    public string AppId
    {
        get => _appId ??= Assembly.GetEntryAssembly()?.GetName().Name ?? "BlazorNativeSdkApp";
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            _appId = value;
        }
    }

    /// <summary>A user-facing application name used in OS UI (notification headers, protocol handler names). Defaults to <see cref="AppId"/>.</summary>
    public string? AppName { get; set; }

    /// <summary>Deep-link routing rules.</summary>
    public DeepLinkOptions DeepLinks { get; } = new();

    /// <summary>The user-facing name, falling back to <see cref="AppId"/>.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(AppName) ? AppId : AppName;
}

/// <summary>
/// Rules that decide which URLs may be turned into app routes. URLs that match no rule are not navigated to, so untrusted
/// links cannot drive the app to arbitrary locations. Activation handlers still receive every activation.
/// </summary>
public sealed class DeepLinkOptions
{
    /// <summary>Custom URL schemes owned by the app, such as <c>myapp</c> (Android intent filters, iOS URL types, Windows protocol registration).</summary>
    public ISet<string> Schemes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>HTTPS hosts verified for the app (Android App Links, iOS Universal Links), such as <c>contoso.com</c>.</summary>
    public ISet<string> Hosts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// For custom schemes, treat the URL host as the first route segment so that <c>myapp://orders/42</c> maps to
    /// <c>/orders/42</c>. When <see langword="false"/>, only the path is used (<c>myapp:///orders/42</c>). Default: <see langword="true"/>.
    /// </summary>
    public bool CustomSchemeHostIsFirstSegment { get; set; } = true;

    /// <summary>A custom resolver evaluated before the built-in rules. Return <see langword="null"/> to fall back to the built-in rules.</summary>
    public Func<Uri, string?>? RouteResolver { get; set; }

    /// <summary>
    /// Deliver deep links whose URL matches no allowed scheme or host (and that <see cref="RouteResolver"/> does not accept) to
    /// activation handlers. Default: <see langword="false"/>. On Android and desktop platforms any local app can send the app an
    /// arbitrary URL, so unrecognized links are dropped before handlers see them unless you opt in here; such links are never
    /// navigated to either way.
    /// </summary>
    public bool DeliverUnrecognizedLinks { get; set; }

    /// <summary>Navigate to resolved routes when no handler handled the activation. Default: <see langword="true"/>.</summary>
    public bool NavigateToResolvedRoutes { get; set; } = true;

    /// <summary>The maximum number of activations buffered before the UI is ready. Older activations are dropped. Default: 32.</summary>
    public int MaxQueuedActivations { get; set; } = 32;

    /// <summary>Identical activations received within this window are ignored (platforms sometimes deliver the same intent twice). Default: 2 seconds.</summary>
    public TimeSpan DuplicateSuppressionWindow { get; set; } = TimeSpan.FromSeconds(2);
}
