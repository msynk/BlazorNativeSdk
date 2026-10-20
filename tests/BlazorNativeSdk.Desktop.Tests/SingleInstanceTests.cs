using System.Text.Json;
using BlazorNativeSdk.Desktop.Ipc;
using BlazorNativeSdk.Desktop.SingleInstance;
using BlazorNativeSdk.Lifecycle;
using BlazorNativeSdk.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk.Desktop.Tests;

public sealed class SingleInstanceTests : IDisposable
{
    private readonly string _lockDirectory = TestSupport.CreateTempDirectory();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_lockDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private (DesktopSingleInstanceService Service, RecordingPublisher Publisher, DesktopIpcService Ipc) Create(NativeSdkOptions sdk, Action<DesktopNativeOptions>? configure = null)
    {
        var options = new DesktopNativeOptions { SingleInstanceLockDirectory = _lockDirectory, SingleInstanceConnectTimeout = TimeSpan.FromSeconds(5) };
        configure?.Invoke(options);
        var ipc = new DesktopIpcService(Options.Create(sdk), Options.Create(options), NullLogger<DesktopIpcService>.Instance);
        var publisher = new RecordingPublisher();
        var service = new DesktopSingleInstanceService(
            ipc,
            publisher,
            new PhysicalAppStorage(Options.Create(sdk)),
            Options.Create(sdk),
            Options.Create(options),
            NullLogger<DesktopSingleInstanceService>.Instance);
        return (service, publisher, ipc);
    }

    private static NativeSdkOptions Sdk()
    {
        var sdk = TestSupport.SdkOptions();
        sdk.DeepLinks.Schemes.Add("myapp");
        return sdk;
    }

    [Fact]
    public async Task The_second_instance_forwards_its_arguments_to_the_primary()
    {
        var sdk = Sdk();
        var (primaryService, primaryPublisher, primaryIpc) = Create(sdk);
        var (secondaryService, secondaryPublisher, secondaryIpc) = Create(sdk);
        await using var _ = primaryIpc;
        await using var __ = secondaryIpc;

        var primary = await primaryService.AcquireAsync(["--start-minimized"], Ct);
        Assert.True(primary.IsSuccess, primary.ToString());
        await using var primaryLease = primary.Value!;

        var secondary = await secondaryService.AcquireAsync(["--verbose", "myapp://orders/42?tab=items"], Ct);
        Assert.True(secondary.IsSuccess, secondary.ToString());
        await using var secondaryLease = secondary.Value!;

        Assert.True(primaryLease.IsPrimary);
        Assert.False(primaryLease.ArgumentsForwarded);
        Assert.False(secondaryLease.IsPrimary);
        Assert.True(secondaryLease.ArgumentsForwarded);

        // The primary published its own cold-start launch, then the forwarded deep link.
        var activations = primaryPublisher.Activations;
        Assert.Equal(2, activations.Count);
        Assert.Equal(ActivationKind.Launch, activations[0].Kind);
        Assert.True(activations[0].IsColdStart);
        Assert.Equal(["--start-minimized"], activations[0].Arguments);

        Assert.Equal(ActivationKind.DeepLink, activations[1].Kind);
        Assert.Equal(new Uri("myapp://orders/42?tab=items"), activations[1].Uri);
        Assert.False(activations[1].IsColdStart);
        Assert.Equal(["--verbose", "myapp://orders/42?tab=items"], activations[1].Arguments);

        // The secondary never publishes anything itself.
        Assert.Empty(secondaryPublisher.Activations);
    }

    [Fact]
    public async Task Releasing_the_primary_lease_lets_the_next_process_become_primary()
    {
        var sdk = Sdk();
        var (first, _, firstIpc) = Create(sdk);
        var (second, secondPublisher, secondIpc) = Create(sdk, o => o.PublishLaunchActivation = false);
        await using var _ = firstIpc;
        await using var __ = secondIpc;

        var lease = (await first.AcquireAsync([], Ct)).Value!;
        Assert.True(lease.IsPrimary);
        await lease.DisposeAsync();
        await lease.DisposeAsync();

        var next = await second.AcquireAsync(["plain"], Ct);
        Assert.True(next.IsSuccess, next.ToString());
        await using var nextLease = next.Value!;
        Assert.True(nextLease.IsPrimary);
        Assert.Empty(secondPublisher.Activations);
    }

    [Fact]
    public async Task Plain_forwarded_arguments_become_a_secondary_instance_activation()
    {
        var sdk = Sdk();
        var (primaryService, publisher, primaryIpc) = Create(sdk, o => o.PublishLaunchActivation = false);
        var (secondaryService, _, secondaryIpc) = Create(sdk);
        await using var _ = primaryIpc;
        await using var __ = secondaryIpc;
        await using var primary = (await primaryService.AcquireAsync([], Ct)).Value!;

        await using var secondary = (await secondaryService.AcquireAsync(["--new-window", "evil://not-allowed"], Ct)).Value!;

        Assert.True(secondary.ArgumentsForwarded);
        var activation = Assert.Single(publisher.Activations);
        Assert.Equal(ActivationKind.SecondaryInstance, activation.Kind);
        Assert.Null(activation.Uri);
        Assert.Equal(["--new-window", "evil://not-allowed"], activation.Arguments);
    }

    [Fact]
    public async Task Forwarded_relative_file_paths_resolve_against_the_secondary_working_directory()
    {
        var sdk = Sdk();
        var (service, publisher, ipc) = Create(sdk);
        await using var _ = ipc;
        var workingDirectory = TestSupport.CreateTempDirectory();
        var file = Path.Combine(workingDirectory, "report.txt");
        await File.WriteAllTextAsync(file, "hello", Ct);
        var payload = JsonSerializer.Serialize(
            new ForwardedLaunch { Arguments = ["report.txt"], WorkingDirectory = workingDirectory },
            DesktopJsonContext.Default.ForwardedLaunch);

        var reply = await service.HandleForwardAsync(IpcMessage.Create(SingleInstanceProtocol.Forward, payload), new IpcPeer { ConnectionId = "c" }, Ct);

        Assert.Equal(SingleInstanceProtocol.Ack, reply!.Type);
        var activation = Assert.Single(publisher.Activations);
        Assert.Equal(ActivationKind.File, activation.Kind);
        Assert.Equal(Path.GetFullPath(file), Assert.Single(activation.Files).FullPath);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"arguments\":null}")]
    [InlineData("{\"arguments\":[null]}")]
    public async Task Malformed_forwarded_launches_are_ignored(string payload)
    {
        var (service, publisher, ipc) = Create(Sdk());
        await using var _ = ipc;

        var reply = await service.HandleForwardAsync(IpcMessage.Create(SingleInstanceProtocol.Forward, payload), new IpcPeer { ConnectionId = "c" }, Ct);

        Assert.Equal(SingleInstanceProtocol.Invalid, reply!.Type);
        Assert.Empty(publisher.Activations);
    }

    [Fact]
    public async Task Too_many_forwarded_arguments_are_rejected()
    {
        var (service, publisher, ipc) = Create(Sdk(), o => o.MaxForwardedArguments = 3);
        await using var _ = ipc;
        var payload = JsonSerializer.Serialize(new ForwardedLaunch { Arguments = ["a", "b", "c", "d"] }, DesktopJsonContext.Default.ForwardedLaunch);

        var reply = await service.HandleForwardAsync(IpcMessage.Create(SingleInstanceProtocol.Forward, payload), new IpcPeer { ConnectionId = "c" }, Ct);

        Assert.Equal(SingleInstanceProtocol.Invalid, reply!.Type);
        Assert.Empty(publisher.Activations);
    }

    [Fact]
    public async Task Disposing_the_service_releases_primary_leases()
    {
        var sdk = Sdk();
        var (first, _, firstIpc) = Create(sdk);
        var (second, _, secondIpc) = Create(sdk);
        await using var __ = secondIpc;
        var lease = (await first.AcquireAsync([], Ct)).Value!;
        Assert.True(lease.IsPrimary);

        first.Dispose();
        await first.DisposeAsync();
        await firstIpc.DisposeAsync();

        await using var next = (await second.AcquireAsync([], Ct)).Value!;
        Assert.True(next.IsPrimary);
    }
}
