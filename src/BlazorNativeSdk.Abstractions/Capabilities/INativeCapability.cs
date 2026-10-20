namespace BlazorNativeSdk;

/// <summary>
/// Base contract for every capability service. Capabilities report their runtime availability so that
/// UI can adapt before an operation is attempted.
/// </summary>
public interface INativeCapability
{
    /// <summary>The capability identifier.</summary>
    CapabilityId Capability { get; }

    /// <summary>
    /// Determines whether the capability can be used in the current host. Implementations must not prompt the user;
    /// they report <see cref="CapabilityAvailability.RequiresPermission"/> instead.
    /// </summary>
    ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Aggregates the capabilities registered in the current host. Inject it to build capability-aware UI.
/// </summary>
public interface ICapabilityRegistry
{
    /// <summary>Information about the current host.</summary>
    NativeHostInfo Host { get; }

    /// <summary>The capabilities with a registered provider (including fallbacks that report <see cref="CapabilityAvailability.Unsupported"/>).</summary>
    IReadOnlyCollection<CapabilityId> Capabilities { get; }

    /// <summary>Gets the status of one capability. Unknown identifiers report <see cref="CapabilityAvailability.Unsupported"/>.</summary>
    ValueTask<CapabilityStatus> GetStatusAsync(CapabilityId capability, CancellationToken cancellationToken = default);

    /// <summary>Gets the status of every registered capability.</summary>
    ValueTask<IReadOnlyList<CapabilityStatus>> GetAllStatusesAsync(CancellationToken cancellationToken = default);

    /// <summary>Resolves the service that implements a capability, when one is registered.</summary>
    INativeCapability? GetProvider(CapabilityId capability);
}
