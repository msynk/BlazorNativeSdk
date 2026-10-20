using System.Diagnostics.CodeAnalysis;
using BlazorNativeSdk.Desktop;
using Microsoft.AspNetCore.Components.WebView.Maui;
using MauiWindow = Microsoft.Maui.Controls.Window;

namespace BlazorNativeSdk.Maui;

/// <summary>
/// Options for the .NET MAUI host integration, configured through the <c>options</c> callback of
/// <c>builder.UseBlazorNativeSdk(sdk =&gt; ..., options =&gt; ...)</c>.
/// </summary>
/// <remarks>
/// Window-related options apply to the desktop targets (Windows and Mac Catalyst), where <see cref="IWindowManager"/> is
/// provided. Single-instance and protocol options apply to Windows only.
/// </remarks>
public sealed class MauiNativeOptions
{
    /// <summary>
    /// The host page of windows opened by <see cref="IWindowManager.OpenAsync"/>, relative to the app package root.
    /// Default: <c>wwwroot/index.html</c>.
    /// </summary>
    public string HostPage { get; set; } = "wwwroot/index.html";

    /// <summary>The CSS selector of the element in <see cref="HostPage"/> that hosts the root component. Default: <c>#app</c>.</summary>
    public string RootComponentSelector { get; set; } = "#app";

    /// <summary>
    /// The root component of windows opened without <see cref="WindowOptions.RootComponent"/>, typically the app's
    /// <c>Routes</c> component. Such windows load <see cref="WindowOptions.Route"/> (default <c>/</c>) through
    /// <c>BlazorWebView.StartPath</c>. When <see langword="null"/>, those windows cannot be opened and
    /// <see cref="IWindowManager.OpenAsync"/> returns <see cref="NativeErrorCodes.InvalidArgument"/>.
    /// </summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
    public Type? DefaultRootComponent { get; set; }

    /// <summary>Parameters passed to <see cref="DefaultRootComponent"/> (for example <c>AdditionalAssemblies</c> of a router).</summary>
    public IDictionary<string, object?>? DefaultRootComponentParameters { get; set; }

    /// <summary>
    /// The selector of a <c>HeadOutlet</c> root component added to windows opened by the window manager, so
    /// <c>&lt;PageTitle&gt;</c> and <c>&lt;HeadContent&gt;</c> work in them. <see langword="null"/> adds none.
    /// Default: <c>head::after</c>.
    /// </summary>
    public string? HeadOutletSelector { get; set; } = "head::after";

    /// <summary>The <see cref="INativeWindow.Id"/> of the app's first (main) window. Default: <c>main</c>.</summary>
    public string MainWindowId { get; set; } = "main";

    /// <summary>
    /// Windows: enforce a single running instance. A second launch forwards its activation (arguments, deep link, file,
    /// toast click) to the running instance and exits before showing a window. Unpackaged apps use the Desktop
    /// <see cref="ISingleInstanceService"/> (per-user lock file and named pipe); MSIX-packaged apps use the Windows App SDK
    /// <c>AppInstance</c> redirection, which also carries protocol and file activations. Default: <see langword="true"/>.
    /// </summary>
    public bool SingleInstance { get; set; } = true;

    /// <summary>
    /// Windows, unpackaged apps: at startup, register every scheme in <see cref="DeepLinkOptions.Schemes"/> and the Windows
    /// adapter's notification activation scheme for the current user through <see cref="IProtocolRegistrationService"/>
    /// (only when not registered for this executable yet), so the OS launches the app with the URL. Packaged apps declare
    /// protocols in their manifest instead. Default: <see langword="false"/>.
    /// </summary>
    public bool RegisterProtocolSchemes { get; set; }

    /// <summary>
    /// Windows: bring the main window to the front when an activation reaches the running app (a forwarded launch, a deep
    /// link from another app, a notification click). Background activations never focus it. Default: <see langword="true"/>.
    /// </summary>
    public bool FocusMainWindowOnActivation { get; set; } = true;

    /// <summary>
    /// Windows: restore the main window's size, position and state from the previous session and save it when it changes,
    /// under <see cref="MainWindowId"/>. Mac Catalyst restores window frames itself. Default: <see langword="true"/>.
    /// </summary>
    public bool PersistMainWindowPlacement { get; set; } = true;

    /// <summary>
    /// How long a window must stay still before its moved or resized placement is saved. The placement is always saved when
    /// the window closes. Default: 1 second.
    /// </summary>
    public TimeSpan PlacementSaveDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long <see cref="IWindowManager.OpenAsync"/> waits for the platform to create a requested window (Mac Catalyst
    /// creates scenes asynchronously). Default: 15 seconds.
    /// </summary>
    public TimeSpan WindowOpenTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Make deep links and notification taps navigate in the main window rather than in secondary windows opened by the
    /// window manager (their <c>&lt;NativeActivationOutlet /&gt;</c> is treated as non-primary). Default: <see langword="true"/>.
    /// </summary>
    public bool RouteActivationsToMainWindow { get; set; } = true;

    /// <summary>
    /// Customizes each window that <see cref="IWindowManager.OpenAsync"/> creates (MAUI window properties,
    /// <see cref="BlazorWebView"/> events such as <c>UrlLoading</c> or <c>BlazorWebViewInitializing</c>) before it is opened.
    /// Runs on the UI thread.
    /// </summary>
    public Action<MauiWindow, BlazorWebView, WindowOptions>? ConfigureWindow { get; set; }

    /// <summary>Validates the options.</summary>
    /// <exception cref="InvalidOperationException">An option has an invalid value.</exception>
    internal void Validate()
    {
        static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException($"{nameof(MauiNativeOptions)}: {message}");
            }
        }

        Require(!string.IsNullOrWhiteSpace(HostPage), $"{nameof(HostPage)} must not be empty.");
        Require(!string.IsNullOrWhiteSpace(RootComponentSelector), $"{nameof(RootComponentSelector)} must not be empty.");
        Require(MauiWindowIds.IsValid(MainWindowId), $"{nameof(MainWindowId)} must be 1-{MauiWindowIds.MaxLength} characters without control characters.");
        Require(PlacementSaveDelay >= TimeSpan.Zero, $"{nameof(PlacementSaveDelay)} must not be negative.");
        Require(WindowOpenTimeout > TimeSpan.Zero, $"{nameof(WindowOpenTimeout)} must be positive.");
    }
}
