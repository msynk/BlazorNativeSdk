using Microsoft.Extensions.DependencyInjection;

namespace BlazorNativeSdk;

/// <summary>Default <see cref="ICapabilityRegistry"/>. Resolves providers lazily so that unused adapters are never created.</summary>
internal sealed class CapabilityRegistry : ICapabilityRegistry
{
    private readonly IServiceProvider _services;
    private readonly Dictionary<CapabilityId, Type> _registrations;

    public CapabilityRegistry(IServiceProvider services, NativeHostInfo host, IEnumerable<CapabilityRegistration> registrations)
    {
        _services = services;
        Host = host;
        _registrations = [];
        foreach (var registration in registrations)
        {
            // Later registrations win, mirroring service replacement.
            _registrations[registration.Capability] = registration.ServiceType;
        }

        Capabilities = OrderCapabilities(_registrations.Keys);
    }

    public NativeHostInfo Host { get; }

    public IReadOnlyCollection<CapabilityId> Capabilities { get; }

    public INativeCapability? GetProvider(CapabilityId capability) =>
        _registrations.TryGetValue(capability, out var type) ? _services.GetService(type) as INativeCapability : null;

    public async ValueTask<CapabilityStatus> GetStatusAsync(CapabilityId capability, CancellationToken cancellationToken = default)
    {
        var provider = GetProvider(capability);
        if (provider is null)
        {
            return CapabilityStatus.Unsupported(capability, NativeError.NoAdapter(capability).Message, reasonCode: NativeErrorCodes.NoAdapter);
        }

        try
        {
            return await provider.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CapabilityStatus.FromError(NativeError.FromException(capability, ex)) with { Capability = capability };
        }
    }

    public async ValueTask<IReadOnlyList<CapabilityStatus>> GetAllStatusesAsync(CancellationToken cancellationToken = default)
    {
        var tasks = Capabilities.Select(c => GetStatusAsync(c, cancellationToken).AsTask()).ToArray();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static CapabilityId[] OrderCapabilities(IEnumerable<CapabilityId> capabilities)
    {
        var wellKnown = NativeCapabilities.All;
        return [.. capabilities.OrderBy(c =>
        {
            for (var i = 0; i < wellKnown.Count; i++)
            {
                if (wellKnown[i] == c)
                {
                    return i;
                }
            }

            return int.MaxValue;
        }).ThenBy(c => c.Name, StringComparer.Ordinal)];
    }
}
