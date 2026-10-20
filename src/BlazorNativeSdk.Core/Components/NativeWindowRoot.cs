using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace BlazorNativeSdk.Components;

/// <summary>
/// Identifies the native window that a Blazor root (one <c>BlazorWebView</c> and its service scope) renders into. Hosts set it
/// through <see cref="NativeWindowRoot"/>; it stays empty for the app's main web view and in browsers.
/// </summary>
public sealed class NativeWindowScope
{
    private string? _windowId;
    private int _routesActivations = 1;

    /// <summary>The id of the window this scope renders into, or <see langword="null"/> for the main web view.</summary>
    public string? WindowId => Volatile.Read(ref _windowId);

    /// <summary>
    /// Whether deep links and notification taps may navigate this window. <see langword="false"/> for secondary windows unless
    /// the host opted in, so activations keep reaching the main window even when every window renders the same layout.
    /// </summary>
    public bool RoutesActivations => Volatile.Read(ref _routesActivations) == 1;

    /// <summary>Associates the scope with a window. The first call wins.</summary>
    /// <param name="windowId">The window id.</param>
    /// <param name="routesActivations">Whether activations may navigate this window.</param>
    public void Attach(string windowId, bool routesActivations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        if (Interlocked.CompareExchange(ref _windowId, windowId, null) is null)
        {
            Volatile.Write(ref _routesActivations, routesActivations ? 1 : 0);
        }
    }
}

/// <summary>
/// Root component for windows opened by a window manager. It associates the web view's service scope with the window
/// (<see cref="NativeWindowScope"/>), then renders <see cref="Component"/> with <see cref="ComponentParameters"/>. A
/// <see cref="NativeActivationOutlet"/> inside a secondary window therefore doesn't take deep-link navigation away from the
/// main window.
/// </summary>
public sealed class NativeWindowRoot : ComponentBase
{
    [Inject]
    private NativeWindowScope Scope { get; set; } = default!;

    /// <summary>The window id.</summary>
    [Parameter]
    [EditorRequired]
    public string WindowId { get; set; } = string.Empty;

    /// <summary>Whether activations may navigate this window. Default: <see langword="false"/>.</summary>
    [Parameter]
    public bool RoutesActivations { get; set; }

    /// <summary>The component to render.</summary>
    [Parameter]
    [EditorRequired]
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
    public Type Component { get; set; } = default!;

    /// <summary>The parameters of <see cref="Component"/>.</summary>
    [Parameter]
    public IReadOnlyDictionary<string, object?>? ComponentParameters { get; set; }

    /// <summary>Creates root-component parameters for a host's <c>RootComponent.Parameters</c>.</summary>
    public static Dictionary<string, object?> CreateParameters(
        string windowId,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type component,
        IReadOnlyDictionary<string, object?>? componentParameters = null,
        bool routesActivations = false) => new()
        {
            [nameof(WindowId)] = windowId,
            [nameof(Component)] = component,
            [nameof(ComponentParameters)] = componentParameters,
            [nameof(RoutesActivations)] = routesActivations,
        };

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        ArgumentNullException.ThrowIfNull(Component);
        if (!string.IsNullOrEmpty(WindowId))
        {
            // Runs before any child renders, so the outlet in the window's layout sees the association when it attaches.
            Scope.Attach(WindowId, RoutesActivations);
        }
    }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent(0, Component);
        if (ComponentParameters is { } parameters)
        {
            foreach (var (name, value) in parameters)
            {
                builder.AddComponentParameter(1, name, value);
            }
        }

        builder.CloseComponent();
    }
}
