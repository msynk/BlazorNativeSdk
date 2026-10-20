using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace BlazorNativeSdk.Components;

/// <summary>
/// Renders content based on a capability's runtime availability, so UI can hide or replace features up front instead of
/// failing when used.
/// </summary>
/// <example>
/// <code>
/// &lt;CapabilityView Capability="NativeCapabilities.Barcode"&gt;
///     &lt;Available&gt;&lt;button @onclick="Scan"&gt;Scan ticket&lt;/button&gt;&lt;/Available&gt;
///     &lt;NotAvailable Context="status"&gt;Scanning is not available: @status.Reason&lt;/NotAvailable&gt;
/// &lt;/CapabilityView&gt;
/// </code>
/// </example>
public sealed class CapabilityView : ComponentBase
{
    private CapabilityStatus? _status;
    private CapabilityId _loaded;

    [Inject]
    private ICapabilityRegistry Registry { get; set; } = default!;

    /// <summary>The capability to check.</summary>
    [Parameter, EditorRequired]
    public CapabilityId Capability { get; set; }

    /// <summary>Optional feature that must also be supported (see the <c>*Features</c> classes).</summary>
    [Parameter]
    public string? Feature { get; set; }

    /// <summary>Treat <see cref="CapabilityAvailability.RequiresPermission"/> (and unknown) as available. Default: <see langword="true"/>.</summary>
    [Parameter]
    public bool AllowPermissionPrompt { get; set; } = true;

    /// <summary>Rendered when the capability can be used.</summary>
    [Parameter]
    public RenderFragment<CapabilityStatus>? Available { get; set; }

    /// <summary>Rendered when the capability cannot be used.</summary>
    [Parameter]
    public RenderFragment<CapabilityStatus>? NotAvailable { get; set; }

    /// <summary>Rendered while the status is loading.</summary>
    [Parameter]
    public RenderFragment? Loading { get; set; }

    /// <summary>Alias for <see cref="Available"/> when no status context is needed.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync()
    {
        if (_status is null || _loaded != Capability)
        {
            _loaded = Capability;
            _status = null;
            _status = await Registry.GetStatusAsync(Capability).ConfigureAwait(true);
        }
    }

    /// <summary>Re-reads the status (for example after a permission was granted).</summary>
    public async Task RefreshAsync()
    {
        _status = await Registry.GetStatusAsync(Capability).ConfigureAwait(true);
        StateHasChanged();
    }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (_status is null)
        {
            builder.AddContent(0, Loading);
            return;
        }

        var usable = AllowPermissionPrompt ? _status.IsUsable : _status.IsAvailable;
        if (usable && Feature is not null && !_status.Supports(Feature))
        {
            usable = false;
        }

        if (usable)
        {
            if (Available is not null)
            {
                builder.AddContent(1, Available(_status));
            }
            else
            {
                builder.AddContent(2, ChildContent);
            }
        }
        else if (NotAvailable is not null)
        {
            builder.AddContent(3, NotAvailable(_status));
        }
    }
}
