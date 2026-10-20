using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BlazorNativeSdk;

internal sealed class NativeSdkBuilder : INativeSdkBuilder
{
    public NativeSdkBuilder(IServiceCollection services) => Services = services;

    public IServiceCollection Services { get; }

    public INativeSdkBuilder Configure(Action<NativeSdkOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Services.Configure(configure);
        return this;
    }

    public INativeSdkBuilder SetHost(NativeHostInfo host)
    {
        ArgumentNullException.ThrowIfNull(host);
        Services.Replace(ServiceDescriptor.Singleton(host));
        return this;
    }

    public INativeSdkBuilder AddCapability<TService, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>(
        CapabilityId capability,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
        where TService : class, INativeCapability
        where TImplementation : class, TService
    {
        Services.TryAdd(new ServiceDescriptor(typeof(TImplementation), typeof(TImplementation), lifetime));
        Services.Replace(new ServiceDescriptor(typeof(TService), static sp => sp.GetRequiredService<TImplementation>(), lifetime));
        RegisterCapability(capability, typeof(TService));
        return this;
    }

    public INativeSdkBuilder AddCapability<TService>(
        CapabilityId capability,
        Func<IServiceProvider, TService> factory,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
        where TService : class, INativeCapability
    {
        ArgumentNullException.ThrowIfNull(factory);
        Services.Replace(new ServiceDescriptor(typeof(TService), factory, lifetime));
        RegisterCapability(capability, typeof(TService));
        return this;
    }

    internal void RegisterCapability(CapabilityId capability, Type serviceType)
    {
        var existing = Services.FirstOrDefault(d =>
            d.ServiceType == typeof(CapabilityRegistration) &&
            d.ImplementationInstance is CapabilityRegistration r &&
            r.Capability == capability);
        if (existing is not null)
        {
            Services.Remove(existing);
        }

        Services.AddSingleton(new CapabilityRegistration(capability, serviceType));
    }
}

/// <summary>Associates a capability id with the service type that provides it.</summary>
/// <param name="Capability">The capability.</param>
/// <param name="ServiceType">The service type, which must implement <see cref="INativeCapability"/>.</param>
public sealed record CapabilityRegistration(CapabilityId Capability, Type ServiceType);
