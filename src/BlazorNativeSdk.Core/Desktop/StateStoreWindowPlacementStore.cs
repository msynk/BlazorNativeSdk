using System.Text.Json;
using BlazorNativeSdk.Serialization;

namespace BlazorNativeSdk.Desktop;

/// <summary>
/// <see cref="IWindowPlacementStore"/> backed by <see cref="INativeStateStore"/>. Loaded placements are validated against the
/// connected displays so that windows saved on a disconnected monitor reappear on screen.
/// </summary>
public sealed class StateStoreWindowPlacementStore : IWindowPlacementStore
{
    private const string Prefix = "bns.window.";
    private readonly INativeStateStore _store;
    private readonly IScreenService _screens;

    /// <summary>Creates the store.</summary>
    public StateStoreWindowPlacementStore(INativeStateStore store, IScreenService screens)
    {
        _store = store;
        _screens = screens;
    }

    /// <inheritdoc />
    public async ValueTask<WindowPlacement?> LoadAsync(string windowId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        var json = await _store.GetAsync(Prefix + windowId, cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        WindowPlacement? placement;
        try
        {
            placement = JsonSerializer.Deserialize(json, CoreJsonContext.Default.WindowPlacement);
        }
        catch (JsonException)
        {
            return null;
        }

        if (placement is null)
        {
            return null;
        }

        var displays = await _screens.GetDisplaysAsync(cancellationToken).ConfigureAwait(false);
        return displays.IsSuccess ? WindowPlacementGuard.EnsureVisible(placement, displays.Value) : placement;
    }

    /// <inheritdoc />
    public ValueTask SaveAsync(string windowId, WindowPlacement placement, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        ArgumentNullException.ThrowIfNull(placement);

        // Never persist minimized state: restoring a window minimized is almost never what the user expects.
        if (placement.State == NativeWindowState.Minimized)
        {
            placement = placement with { State = NativeWindowState.Normal };
        }

        return _store.SetAsync(Prefix + windowId, JsonSerializer.Serialize(placement, CoreJsonContext.Default.WindowPlacement), cancellationToken);
    }
}

/// <summary>Validates window placements against the connected displays.</summary>
public static class WindowPlacementGuard
{
    /// <summary>The minimum fraction of a window (and at least 64x32 DIPs of its title area) that must be visible.</summary>
    public const double MinimumVisibleFraction = 0.25;

    /// <summary>
    /// Returns a placement that is visible on one of <paramref name="displays"/>. Windows that are mostly off-screen are moved
    /// to the work area of the display they overlap most (or the primary display) and shrunk to fit.
    /// </summary>
    public static WindowPlacement EnsureVisible(WindowPlacement placement, IReadOnlyList<DisplayInfo> displays)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(displays);
        if (displays.Count == 0)
        {
            return placement;
        }

        var bounds = placement.Bounds;
        var area = Math.Max(1, bounds.Width * bounds.Height);
        var visible = displays.Sum(d => d.WorkArea.IntersectionArea(bounds));

        // The title bar must be reachable: require the top strip to intersect a work area.
        var titleStrip = new WindowBounds(bounds.X + 16, bounds.Y, Math.Max(64, bounds.Width - 32), 32);
        var titleVisible = displays.Any(d => d.WorkArea.IntersectionArea(titleStrip) > 0);

        if (visible / area >= MinimumVisibleFraction && titleVisible)
        {
            return placement;
        }

        var target = displays
            .OrderByDescending(d => d.WorkArea.IntersectionArea(bounds))
            .ThenByDescending(d => d.IsPrimary)
            .First();
        if (target.WorkArea.IntersectionArea(bounds) <= 0)
        {
            target = displays.FirstOrDefault(d => d.IsPrimary) ?? displays[0];
        }

        var work = target.WorkArea;
        var width = Math.Min(bounds.Width, work.Width);
        var height = Math.Min(bounds.Height, work.Height);
        var x = Math.Clamp(bounds.X, work.X, work.Right - width);
        var y = Math.Clamp(bounds.Y, work.Y, work.Bottom - height);

        // If the window was entirely elsewhere, center it.
        if (target.WorkArea.IntersectionArea(bounds) <= 0)
        {
            x = work.X + ((work.Width - width) / 2);
            y = work.Y + ((work.Height - height) / 2);
        }

        return placement with { Bounds = new WindowBounds(x, y, width, height), DisplayId = target.Id };
    }
}
