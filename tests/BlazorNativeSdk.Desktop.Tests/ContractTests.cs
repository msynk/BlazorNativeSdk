using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;
using BlazorNativeSdk.Storage;
using BlazorNativeSdk.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorNativeSdk.Desktop.Tests;

/// <summary>Runs the capability contracts against the real services registered by <c>AddDesktop()</c>.</summary>
public sealed class ContractTests : IDisposable
{
    private readonly string _appId = "bns-contract-" + Guid.NewGuid().ToString("N")[..12];
    private readonly List<string> _appDirectories = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Desktop_storage_checkpoints_and_ipc_satisfy_the_contracts()
    {
        await using var provider = Build();
        var storage = provider.GetRequiredService<IAppStorage>();
        Assert.IsType<PhysicalAppStorage>(storage);
        TrackAppDirectories(storage);
        var checkpoints = provider.GetRequiredService<IWorkflowCheckpointStore>();
        Assert.True((await checkpoints.GetStatusAsync(Ct)).IsAvailable, "The file state store makes checkpoints durable.");

        await CapabilityContracts.VerifyAppStorageAsync(storage, Ct);
        await CapabilityContracts.VerifyStateStoreAsync(Assert.IsType<FileStateStore>(provider.GetRequiredService<INativeStateStore>()), Ct);
        await CapabilityContracts.VerifyWorkflowCheckpointStoreAsync(checkpoints, cancellationToken: Ct);
        await CapabilityContracts.VerifyIpcAsync(provider.GetRequiredService<IIpcService>(), Ct);
    }

    [Fact]
    public async Task Every_capability_reports_an_honest_status_with_the_desktop_services()
    {
        await using var provider = Build();
        TrackAppDirectories(provider.GetRequiredService<IAppStorage>());
        await using var scope = provider.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<ICapabilityRegistry>();
        CapabilityId[] desktop =
        [
            NativeCapabilities.AppStorage, NativeCapabilities.Connectivity, NativeCapabilities.BackgroundWork, NativeCapabilities.Transfers,
            NativeCapabilities.WorkflowCheckpoints, NativeCapabilities.Ipc, NativeCapabilities.SingleInstance, NativeCapabilities.DesktopFiles,
            NativeCapabilities.ProtocolRegistration,
        ];

        foreach (var capability in registry.Capabilities)
        {
            var implementation = registry.GetProvider(capability)!;
            await CapabilityContracts.VerifyStatusAsync(implementation, capability, Ct);
            if (desktop.Contains(capability))
            {
                var status = await implementation.GetStatusAsync(Ct);
                Assert.True(status.Adapter != Unsupported.UnsupportedCapability.AdapterName, $"{capability} must be provided by AddDesktop(): {status}");
            }
        }
    }

    public void Dispose()
    {
        foreach (var directory in _appDirectories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBlazorNativeSdk(sdk => sdk.Configure(o => o.AppId = _appId).AddDesktop());
        return services.BuildServiceProvider();
    }

    // The app-specific folders created for the unique app id (never the user's Documents or Downloads).
    private void TrackAppDirectories(IAppStorage storage)
    {
        foreach (var directory in new[] { AppDirectory.AppData, AppDirectory.Cache, AppDirectory.Temp })
        {
            _appDirectories.Add(storage.GetDirectory(directory).GetValueOrThrow());
        }
    }
}
