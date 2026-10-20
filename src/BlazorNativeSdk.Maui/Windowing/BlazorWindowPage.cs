using System.Diagnostics.CodeAnalysis;
using BlazorNativeSdk.Components;
using BlazorNativeSdk.Desktop;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Maui.Controls;

namespace BlazorNativeSdk.Maui;

/// <summary>
/// The page of windows opened by the MAUI window manager: a <see cref="ContentPage"/> that hosts a <see cref="BlazorWebView"/>
/// rendering either <see cref="WindowOptions.RootComponent"/> or <see cref="MauiNativeOptions.DefaultRootComponent"/> at
/// <see cref="WindowOptions.Route"/>.
/// </summary>
/// <remarks>
/// The root component is wrapped in <see cref="NativeWindowRoot"/>, which associates the <c>BlazorWebView</c>'s service scope
/// with the window, so that activations keep navigating the main window (see <see cref="MauiNativeOptions.RouteActivationsToMainWindow"/>).
/// Customize the page or its web view through <see cref="MauiNativeOptions.ConfigureWindow"/>.
/// </remarks>
public class BlazorWindowPage : ContentPage
{
    /// <summary>Creates the page.</summary>
    /// <param name="windowId">The id of the window that shows the page.</param>
    /// <param name="windowOptions">The options the window was opened with.</param>
    /// <param name="options">The MAUI integration options (host page, selectors, default root component).</param>
    /// <exception cref="ArgumentException">Neither <see cref="WindowOptions.RootComponent"/> nor <see cref="MauiNativeOptions.DefaultRootComponent"/> is set, or the route is not an app-relative path.</exception>
    public BlazorWindowPage(string windowId, WindowOptions windowOptions, MauiNativeOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        ArgumentNullException.ThrowIfNull(windowOptions);
        ArgumentNullException.ThrowIfNull(options);

        WindowId = windowId;
        WindowOptions = windowOptions;
        BlazorWebView = new BlazorWebView { HostPage = options.HostPage };

        if (windowOptions.RootComponent is { } component)
        {
            BlazorWebView.RootComponents.Add(CreateRoot(windowId, component, windowOptions.Parameters, options));
        }
        else if (options.DefaultRootComponent is { } defaultComponent)
        {
            var route = string.IsNullOrWhiteSpace(windowOptions.Route) ? "/" : windowOptions.Route;
            if (!IsAppRoute(route))
            {
                throw new ArgumentException($"The window route '{route}' must be an app-relative path such as '/settings'.", nameof(windowOptions));
            }

            BlazorWebView.StartPath = route;
            var parameters = options.DefaultRootComponentParameters is { } values ? new Dictionary<string, object?>(values) : null;
            BlazorWebView.RootComponents.Add(CreateRoot(windowId, defaultComponent, parameters, options));
        }
        else
        {
            throw new ArgumentException(
                $"The window has no root component: set {nameof(WindowOptions)}.{nameof(WindowOptions.RootComponent)} or {nameof(MauiNativeOptions)}.{nameof(MauiNativeOptions.DefaultRootComponent)}.",
                nameof(windowOptions));
        }

        if (!string.IsNullOrWhiteSpace(options.HeadOutletSelector))
        {
            BlazorWebView.RootComponents.Add(new RootComponent { Selector = options.HeadOutletSelector, ComponentType = typeof(HeadOutlet) });
        }

        if (windowOptions.Title is { } title)
        {
            Title = title;
        }

        Content = BlazorWebView;
    }

    /// <summary>The id of the window that shows the page.</summary>
    public string WindowId { get; }

    /// <summary>The options the window was opened with.</summary>
    public WindowOptions WindowOptions { get; }

    /// <summary>The web view that renders the window's Blazor content.</summary>
    public BlazorWebView BlazorWebView { get; }

    /// <summary>Returns whether <paramref name="route"/> is an app-relative path (no scheme, not protocol-relative).</summary>
    internal static bool IsAppRoute(string route) =>
        route.StartsWith('/') &&
        !route.StartsWith("//", StringComparison.Ordinal) &&
        !route.StartsWith("/\\", StringComparison.Ordinal) &&
        !route.Contains("://", StringComparison.Ordinal) &&
        !route.Any(char.IsControl);

    private static RootComponent CreateRoot(
        string windowId,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type component,
        IReadOnlyDictionary<string, object?>? parameters,
        MauiNativeOptions options) => new()
        {
            Selector = options.RootComponentSelector,
            ComponentType = typeof(NativeWindowRoot),
            Parameters = NativeWindowRoot.CreateParameters(windowId, component, parameters, routesActivations: !options.RouteActivationsToMainWindow),
        };
}
