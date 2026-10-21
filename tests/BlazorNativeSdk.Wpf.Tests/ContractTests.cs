using BlazorNativeSdk.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorNativeSdk.Wpf.Tests;

/// <summary>Runs the capability contracts against the services registered by <c>AddWpf()</c>.</summary>
public sealed class ContractTests : IDisposable
{
    private readonly TestHost _host = new();

    [Fact]
    public async Task Every_capability_registered_by_AddWpf_reports_an_honest_status()
    {
        await using var provider = _host.Build();
        await using var scope = provider.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<ICapabilityRegistry>();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(NativeCapabilities.All.Count, registry.Capabilities.Count);
        foreach (var capability in registry.Capabilities)
        {
            await CapabilityContracts.VerifyStatusAsync(registry.GetProvider(capability)!, capability, ct);
        }

        // Without a running WPF Application the window manager is honest about it.
        var windows = await registry.GetStatusAsync(NativeCapabilities.Windows, ct);
        Assert.Equal(CapabilityAvailability.Unavailable, windows.Availability);
        Assert.NotNull(windows.Reason);
    }

    public void Dispose() => _host.Dispose();
}
