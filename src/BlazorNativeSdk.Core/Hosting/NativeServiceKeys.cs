using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorNativeSdk;

/// <summary>Keys of keyed services the SDK looks up.</summary>
public static class NativeServiceKeys
{
    /// <summary>
    /// Services that describe the host process rather than one user's session. On Blazor Server, capability services are
    /// scoped per circuit (they talk to the user's browser), while process-wide infrastructure such as the in-process work
    /// scheduler and the HTTP transfer queue runs on the server. Such infrastructure uses an <see cref="IConnectivityService"/>
    /// or <see cref="IAppLifecycle"/> registered under this key when one exists, and the regular registration otherwise.
    /// </summary>
    public const string Process = "bns.process";

    /// <summary>The process-wide <see cref="IConnectivityService"/>.</summary>
    internal static IConnectivityService ProcessConnectivity(IServiceProvider services) =>
        services.GetKeyedService<IConnectivityService>(Process) ?? services.GetRequiredService<IConnectivityService>();

    /// <summary>The process-wide <see cref="IAppLifecycle"/>.</summary>
    internal static IAppLifecycle ProcessLifecycle(IServiceProvider services) =>
        services.GetKeyedService<IAppLifecycle>(Process) ?? services.GetRequiredService<IAppLifecycle>();
}
