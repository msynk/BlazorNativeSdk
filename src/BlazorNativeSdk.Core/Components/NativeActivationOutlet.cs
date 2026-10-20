using BlazorNativeSdk.Lifecycle;
using Microsoft.AspNetCore.Components;

namespace BlazorNativeSdk.Components;

/// <summary>
/// Connects a Blazor root (a window or the browser page) to the activation pipeline. Place it once in your main layout
/// or <c>Routes.razor</c>: deep links and notification taps received before the UI was ready (cold start) are replayed
/// as soon as it renders, and resolved routes are navigated with this root's <see cref="NavigationManager"/>.
/// It also starts SDK background components on first render.
/// </summary>
/// <remarks>
/// When several windows render the same layout, only the main window receives navigations: windows opened through a
/// host's window manager are marked as secondary by <see cref="NativeWindowRoot"/>. Set <see cref="IsPrimary"/> to override.
/// </remarks>
public sealed class NativeActivationOutlet : ComponentBase, INavigationOutlet, IDisposable
{
    private IDisposable? _registration;

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    [Inject]
    private INavigationOutletRegistry Registry { get; set; } = default!;

    [Inject]
    private NativeSdkRuntime Runtime { get; set; } = default!;

    [Inject]
    private NativeWindowScope WindowScope { get; set; } = default!;

    /// <summary>
    /// Whether this outlet should receive navigations in preference to others. Defaults to <see langword="true"/> except in
    /// secondary windows opened through a window manager (see <see cref="NativeWindowScope.RoutesActivations"/>).
    /// </summary>
    [Parameter]
    public bool? IsPrimary { get; set; }

    /// <summary>Use a full page load when navigating (browser hosts only).</summary>
    [Parameter]
    public bool ForceLoad { get; set; }

    bool INavigationOutlet.IsPrimary => IsPrimary ?? (WindowScope.WindowId is null || WindowScope.RoutesActivations);

    /// <inheritdoc />
    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender)
        {
            Runtime.EnsureStarted();
            _registration = Registry.Attach(this);
        }
    }

    /// <inheritdoc />
    public ValueTask NavigateAsync(string route, CancellationToken cancellationToken) =>
        new(InvokeAsync(() => Navigation.NavigateTo(route, ForceLoad)));

    /// <inheritdoc />
    public void Dispose() => _registration?.Dispose();
}
