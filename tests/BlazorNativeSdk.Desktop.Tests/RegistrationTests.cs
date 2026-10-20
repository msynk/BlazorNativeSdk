using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;
using BlazorNativeSdk.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk.Desktop.Tests;

public sealed class RegistrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider Build(Action<DesktopNativeOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBlazorNativeSdk(sdk =>
        {
            sdk.Configure(o => o.AppId = "bns-test-" + Guid.NewGuid().ToString("N")[..12]);
            sdk.AddDesktop(configure);
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task AddDesktop_registers_the_desktop_capabilities()
    {
        await using var provider = Build(o => o.AllowedUriSchemes.Add("vscode"));

        Assert.IsType<DesktopIpcService>(provider.GetRequiredService<IIpcService>());
        Assert.IsType<DesktopSingleInstanceService>(provider.GetRequiredService<ISingleInstanceService>());
        Assert.IsType<DesktopFileOperations>(provider.GetRequiredService<IDesktopFileOperations>());
        Assert.IsType<DesktopProtocolRegistrationService>(provider.GetRequiredService<IProtocolRegistrationService>());
        Assert.IsType<PhysicalAppStorage>(provider.GetRequiredService<IAppStorage>());
        Assert.IsType<InProcessBackgroundWorkScheduler>(provider.GetRequiredService<IBackgroundWorkScheduler>());
        Assert.Contains("vscode", provider.GetRequiredService<IOptions<DesktopNativeOptions>>().Value.AllowedUriSchemes);

        using var scope = provider.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ICapabilityRegistry>();
        var ipc = await registry.GetStatusAsync(NativeCapabilities.Ipc, Ct);
        var singleInstance = await registry.GetStatusAsync(NativeCapabilities.SingleInstance, Ct);
        Assert.Equal("desktop", ipc.Adapter);
        Assert.True(singleInstance.IsAvailable);
        Assert.True((await registry.GetStatusAsync(NativeCapabilities.BackgroundWork, Ct)).Supports("runs-in-background"));
    }

    [Fact]
    public void Services_are_shared_between_their_registrations_and_tolerate_synchronous_disposal()
    {
        var provider = Build();
        var ipc = provider.GetRequiredService<IIpcService>();
        Assert.Same(ipc, provider.GetRequiredService<DesktopIpcService>());
        _ = provider.GetRequiredService<ISingleInstanceService>();

        // The container disposes each instance once per registration, possibly synchronously.
        provider.Dispose();
    }
}
