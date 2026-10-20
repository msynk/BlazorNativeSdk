using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorNativeSdk;

/// <summary>Configures BlazorNativeSdk services. Adapter packages add extension methods to this builder.</summary>
public interface INativeSdkBuilder
{
    /// <summary>The service collection.</summary>
    IServiceCollection Services { get; }

    /// <summary>Configures application-wide options.</summary>
    INativeSdkBuilder Configure(Action<NativeSdkOptions> configure);

    /// <summary>Declares the host. Adapters call this; the last call wins.</summary>
    INativeSdkBuilder SetHost(NativeHostInfo host);

    /// <summary>
    /// Registers <typeparamref name="TImplementation"/> as the provider of <typeparamref name="TService"/>, replacing any previous
    /// provider. Calling this after an adapter method overrides the adapter for that capability.
    /// </summary>
    /// <remarks>
    /// Each implementation type is registered once, so a class that implements several capability interfaces is shared
    /// between them.
    /// </remarks>
    INativeSdkBuilder AddCapability<TService, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>(
        CapabilityId capability,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
        where TService : class, INativeCapability
        where TImplementation : class, TService;

    /// <summary>Registers a capability provider created by a factory, replacing any previous provider.</summary>
    INativeSdkBuilder AddCapability<TService>(
        CapabilityId capability,
        Func<IServiceProvider, TService> factory,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
        where TService : class, INativeCapability;
}
