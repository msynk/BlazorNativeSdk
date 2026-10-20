using BlazorNativeSdk.Desktop;

namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>
/// <see cref="IScreenService"/> over <c>EnumDisplayMonitors</c>, <c>GetMonitorInfoW</c> and <c>GetDpiForMonitor</c>.
/// Bounds are DIPs: physical pixels divided by each monitor's effective scale factor.
/// </summary>
internal sealed class WindowsScreenService : IScreenService
{
    public CapabilityId Capability => NativeCapabilities.Screens;

    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Available(Capability, WindowsAdapter.Name));

    public ValueTask<NativeResult<IReadOnlyList<DisplayInfo>>> GetDisplaysAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var displays = Monitors.GetAll()
                .Select(static m => new DisplayInfo(m.Device, m.Name, m.DipBounds, m.DipWorkArea, m.Scale, m.IsPrimary))
                .ToArray();
            if (displays.Length == 0)
            {
                return ValueTask.FromResult<NativeResult<IReadOnlyList<DisplayInfo>>>(
                    NativeError.Unavailable(Capability, NativeErrorCodes.HardwareUnavailable, "No display is connected (or the session has no interactive desktop)."));
            }

            return ValueTask.FromResult(NativeResult.Success<IReadOnlyList<DisplayInfo>>(displays));
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult<NativeResult<IReadOnlyList<DisplayInfo>>>(NativeError.FromException(Capability, ex));
        }
    }
}
