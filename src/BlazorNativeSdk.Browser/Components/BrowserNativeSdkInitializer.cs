using BlazorNativeSdk.Browser.Hosting;
using Microsoft.AspNetCore.Components;

namespace BlazorNativeSdk.Browser;

/// <summary>
/// Starts the browser adapter's listeners (page lifecycle, connectivity, notification taps, protocol-handler and PWA launch
/// activations) for the current Blazor root once it is interactive. Renders nothing.
/// </summary>
/// <remarks>
/// Place it once in the main layout next to <c>&lt;NativeActivationOutlet /&gt;</c>. It is required with Blazor Server /
/// interactive server rendering, where each circuit has its own browser services and the SDK's application-wide startup
/// tasks cannot reach them. With Blazor WebAssembly the SDK startup also starts the listeners, so the component is optional.
/// It does nothing during prerendering or static rendering.
/// </remarks>
public sealed class BrowserNativeSdkInitializer : ComponentBase
{
    [Inject]
    private BrowserStartup Startup { get; set; } = default!;

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await Startup.StartAsync().ConfigureAwait(false);
        }
    }
}
