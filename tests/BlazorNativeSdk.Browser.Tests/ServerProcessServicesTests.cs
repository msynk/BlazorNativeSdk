using BlazorNativeSdk.Browser.Services;
using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace BlazorNativeSdk.Browser.Tests;

/// <summary>Process-wide services on Blazor Server observe the server process, not one circuit's browser.</summary>
public sealed class ServerProcessServicesTests
{
    [Fact]
    public async Task Background_work_and_transfers_resolve_on_a_server_with_scope_validation()
    {
        await using var provider = BuildServerProvider();

        var scheduler = provider.GetRequiredService<IBackgroundWorkScheduler>();
        var transfers = provider.GetRequiredService<ITransferService>();

        Assert.IsType<InProcessBackgroundWorkScheduler>(scheduler);
        Assert.IsType<HttpTransferService>(transfers);
    }

    [Fact]
    public async Task Process_connectivity_and_lifecycle_are_server_side_while_circuits_keep_browser_services()
    {
        await using var provider = BuildServerProvider();
        await using var circuit = provider.CreateAsyncScope();

        Assert.IsType<NetworkInformationConnectivityService>(provider.GetRequiredKeyedService<IConnectivityService>(NativeServiceKeys.Process));
        Assert.IsType<ManualAppLifecycle>(provider.GetRequiredKeyedService<IAppLifecycle>(NativeServiceKeys.Process));
        Assert.IsType<BrowserConnectivityService>(circuit.ServiceProvider.GetRequiredService<IConnectivityService>());
        Assert.IsType<BrowserAppLifecycle>(circuit.ServiceProvider.GetRequiredService<IAppLifecycle>());
    }

    private static ServiceProvider BuildServerProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IJSRuntime, PrerenderingJSRuntime>();
        services.AddBlazorNativeSdk(sdk => sdk
            .AddBrowser()
            .AddInProcessBackgroundWork()
            .AddHttpTransfers());
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
