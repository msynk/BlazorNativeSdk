using System.Diagnostics.CodeAnalysis;
using System.Windows;
using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.WindowsPlatform;
using Microsoft.AspNetCore.Components.WebView.Wpf;

namespace BlazorNativeSdk.Wpf;

/// <summary>Options for the WPF host integration (<c>builder.AddWpf(o =&gt; ...)</c>).</summary>
public sealed class WpfNativeOptions
{
    private readonly List<Action<WindowsNativeOptions>> _windowsAdapter = [];
    private readonly List<Action<DesktopNativeOptions>> _desktopAdapter = [];

    /// <summary>
    /// The host page of windows opened by <see cref="WpfWindowManager"/>, relative to the application directory.
    /// Default: <c>wwwroot\index.html</c>.
    /// </summary>
    public string HostPage { get; set; } = @"wwwroot\index.html";

    /// <summary>The CSS selector of the element that hosts the root component in <see cref="HostPage"/>. Default: <c>#app</c>.</summary>
    public string RootComponentSelector { get; set; } = "#app";

    /// <summary>
    /// The root component of windows opened with <see cref="WindowOptions.Route"/> (or without
    /// <see cref="WindowOptions.RootComponent"/>), typically the app's <c>Routes</c> component. When <see langword="null"/>,
    /// the first root component of the <c>BlazorWebView</c> in the attached main window is used.
    /// </summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
    public Type? DefaultRootComponent { get; set; }

    /// <summary>
    /// Enforce a single running instance in <see cref="WpfNativeSdk.StartAsync"/>: a second instance forwards its arguments
    /// (deep links, files) to the running one and <c>StartAsync</c> returns <see langword="false"/>. Default: <see langword="true"/>.
    /// </summary>
    public bool SingleInstance { get; set; } = true;

    /// <summary>
    /// For unpackaged apps, register the custom URL schemes of <see cref="DeepLinkOptions.Schemes"/> and
    /// <see cref="WindowsNativeOptions.NotificationActivationScheme"/> for the current user in
    /// <see cref="WpfNativeSdk.StartAsync"/> (through <see cref="IProtocolRegistrationService"/>), so the OS launches the
    /// app with the URL. Packaged (MSIX) apps declare protocols in their manifest instead. Default: <see langword="false"/>.
    /// </summary>
    public bool RegisterProtocolSchemes { get; set; }

    /// <summary>
    /// Bring the main window to the front when an activation reaches the running app (a forwarded deep link, a second
    /// launch, a notification click). Background activations never focus it. Default: <see langword="true"/>.
    /// </summary>
    public bool FocusMainWindowOnActivation { get; set; } = true;

    /// <summary>
    /// Make deep links and notification taps navigate in the main window rather than in secondary windows opened by
    /// <see cref="WpfWindowManager"/> (their <c>&lt;NativeActivationOutlet /&gt;</c> is treated as non-primary, see
    /// <see cref="Components.NativeWindowRoot"/>). Windows the app attaches itself are not affected. Default: <see langword="true"/>.
    /// </summary>
    public bool RouteActivationsToMainWindow { get; set; } = true;

    /// <summary>
    /// How long a window must stay still before its moved or resized placement is saved. The placement is always saved
    /// when the window closes. Default: 1 second.
    /// </summary>
    public TimeSpan PlacementSaveDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Customizes each window that <see cref="WpfWindowManager.OpenAsync"/> creates (icon, resources, styles,
    /// <see cref="BlazorWebView"/> events such as <c>UrlLoading</c>) before it is shown. Runs on the UI thread.
    /// </summary>
    public Action<Window, BlazorWebView, WindowOptions>? ConfigureWindow { get; set; }

    /// <summary>Configures the Windows adapter (<see cref="WindowsNativeOptions"/>) that <c>AddWpf()</c> registers.</summary>
    /// <param name="configure">The configuration callback.</param>
    /// <returns>These options.</returns>
    public WpfNativeOptions ConfigureWindowsAdapter(Action<WindowsNativeOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _windowsAdapter.Add(configure);
        return this;
    }

    /// <summary>Configures the desktop services (<see cref="DesktopNativeOptions"/>) that <c>AddWpf()</c> registers.</summary>
    /// <param name="configure">The configuration callback.</param>
    /// <returns>These options.</returns>
    public WpfNativeOptions ConfigureDesktopAdapter(Action<DesktopNativeOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _desktopAdapter.Add(configure);
        return this;
    }

    internal void ApplyTo(WindowsNativeOptions options)
    {
        foreach (var configure in _windowsAdapter)
        {
            configure(options);
        }
    }

    internal void ApplyTo(DesktopNativeOptions options)
    {
        foreach (var configure in _desktopAdapter)
        {
            configure(options);
        }
    }
}
