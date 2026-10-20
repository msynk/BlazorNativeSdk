using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;
using BlazorNativeSdk.WindowsPlatform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk.Wpf.Tests;

public sealed class RegistrationTests : IDisposable
{
    private readonly TestHost _host = new();

    [Fact]
    public async Task AddWpf_declares_the_host_and_replaces_the_bridge_window_manager_and_lifecycle()
    {
        var provider = _host.Build();
        var ct = TestContext.Current.CancellationToken;

        var host = provider.GetRequiredService<NativeHostInfo>();
        Assert.Equal(NativePlatform.Windows, host.Platform);
        Assert.Equal(NativeHostKind.Wpf, host.Host);
        Assert.Equal(DeviceFormFactor.Desktop, host.FormFactor);
        Assert.Equal(Environment.OSVersion.Version, host.OperatingSystemVersion);
        Assert.True(host.IsHybrid);
        Assert.True(host.IsDesktop);
        Assert.StartsWith("WPF on Windows", host.ToString(), StringComparison.Ordinal);

        var manager = provider.GetRequiredService<WpfWindowManager>();
        Assert.Same(manager, provider.GetRequiredService<IWindowManager>());
        Assert.IsType<WpfHostBridge>(provider.GetRequiredService<IWindowsHostBridge>());
        Assert.Single(provider.GetServices<IWindowsHostBridge>());

        var lifecycle = Assert.IsType<WpfAppLifecycle>(provider.GetRequiredService<IAppLifecycle>());
        Assert.Equal(BackgroundExecutionGuarantee.NotRequired, lifecycle.BackgroundGuarantee);
        Assert.Equal(AppLifecycleState.Launching, lifecycle.State);

        // Desktop services, with the Windows adapter replacing the generic implementations it provides natively.
        Assert.IsType<DesktopSingleInstanceService>(provider.GetRequiredService<ISingleInstanceService>());
        Assert.IsType<DesktopProtocolRegistrationService>(provider.GetRequiredService<IProtocolRegistrationService>());
        Assert.Equal("BlazorNativeSdk.Windows", provider.GetRequiredService<IAppStorage>().GetType().Assembly.GetName().Name);
        Assert.Equal("BlazorNativeSdk.Windows", provider.GetRequiredService<IConnectivityService>().GetType().Assembly.GetName().Name);
        Assert.Equal("BlazorNativeSdk.Windows", provider.GetRequiredService<IScreenService>().GetType().Assembly.GetName().Name);

        using (var scope = provider.CreateScope())
        {
            Assert.Contains(scope.ServiceProvider.GetServices<IActivationHandler>(), h => h is WpfActivationFocusHandler);

            var registry = scope.ServiceProvider.GetRequiredService<ICapabilityRegistry>();
            Assert.Same(host, registry.Host);
            Assert.Same(manager, registry.GetProvider(NativeCapabilities.Windows));
            Assert.Same(lifecycle, registry.GetProvider(NativeCapabilities.Lifecycle));

            var lifecycleStatus = await registry.GetStatusAsync(NativeCapabilities.Lifecycle, ct);
            Assert.Equal(CapabilityAvailability.Available, lifecycleStatus.Availability);
            Assert.Equal("wpf", lifecycleStatus.Adapter);
            Assert.True(lifecycleStatus.Supports(nameof(BackgroundExecutionGuarantee.NotRequired)));

            // No WPF Application runs in the test process.
            var windowsStatus = await registry.GetStatusAsync(NativeCapabilities.Windows, ct);
            Assert.Equal(CapabilityAvailability.Unavailable, windowsStatus.Availability);
            Assert.Equal(NativeErrorCodes.HostNotReady, windowsStatus.ReasonCode);
            Assert.Equal("wpf", windowsStatus.Adapter);
        }

        // Containers dispose forwarded singletons twice, and hosts may dispose synchronously or asynchronously.
        provider.Dispose();
        provider.Dispose();
        await provider.DisposeAsync();
    }

    [Fact]
    public void Options_have_documented_defaults_and_flow_to_the_adapters()
    {
        var defaults = new WpfNativeOptions();
        Assert.Equal(@"wwwroot\index.html", defaults.HostPage);
        Assert.Equal("#app", defaults.RootComponentSelector);
        Assert.Null(defaults.DefaultRootComponent);
        Assert.True(defaults.SingleInstance);
        Assert.False(defaults.RegisterProtocolSchemes);
        Assert.True(defaults.FocusMainWindowOnActivation);
        Assert.Equal(TimeSpan.FromSeconds(1), defaults.PlacementSaveDelay);

        using var provider = _host.Build(o =>
        {
            o.HostPage = "content/start.html";
            o.DefaultRootComponent = typeof(RegistrationTests);
            o.ConfigureWindowsAdapter(w => w.NotificationActivationScheme = "bnstest");
            o.ConfigureDesktopAdapter(d => d.AllowedUriSchemes.Add("vscode"));
        });

        var wpf = provider.GetRequiredService<IOptions<WpfNativeOptions>>().Value;
        Assert.Equal("content/start.html", wpf.HostPage);
        Assert.Equal(typeof(RegistrationTests), wpf.DefaultRootComponent);
        Assert.Equal("bnstest", provider.GetRequiredService<IOptions<WindowsNativeOptions>>().Value.NotificationActivationScheme);
        Assert.Contains("vscode", provider.GetRequiredService<IOptions<DesktopNativeOptions>>().Value.AllowedUriSchemes);
    }

    [Fact]
    public void Capabilities_registered_after_AddWpf_override_it()
    {
        using var provider = _host.Build(sdk: b => b.AddCapability<IAppLifecycle, ManualAppLifecycle>(NativeCapabilities.Lifecycle));
        Assert.IsType<ManualAppLifecycle>(provider.GetRequiredService<IAppLifecycle>());
    }

    [Fact]
    public async Task Window_manager_reports_honest_results_without_a_WPF_application()
    {
        await using var provider = _host.Build();
        var ct = TestContext.Current.CancellationToken;
        var manager = provider.GetRequiredService<IWindowManager>();

        Assert.Empty(manager.Windows);
        Assert.Null(manager.MainWindow);
        Assert.Null(manager.FindWindow("main"));

        var invalid = await manager.OpenAsync(new WindowOptions { Id = "settings", Width = -1 }, ct);
        Assert.Equal(NativeResultStatus.Failed, invalid.Status);
        Assert.Equal(NativeErrorCodes.InvalidArgument, invalid.Error!.Code);

        var open = await manager.OpenAsync(new WindowOptions { Id = "settings", Route = "/settings" }, ct);
        Assert.Equal(NativeResultStatus.Unavailable, open.Status);
        Assert.Equal(NativeErrorCodes.HostNotReady, open.Error!.Code);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await manager.OpenAsync(new WindowOptions(), new CancellationToken(canceled: true)));

        // Without a dispatcher the bridge runs work inline and reports no main window.
        var bridge = provider.GetRequiredService<IWindowsHostBridge>();
        Assert.Equal(0, bridge.MainWindowHandle);
        var ran = false;
        await bridge.InvokeOnUIThreadAsync(() =>
        {
            ran = true;
            return Task.CompletedTask;
        });
        Assert.True(ran);
    }

    [Fact]
    public async Task StartAsync_requires_AddWpf()
    {
        await using var provider = new ServiceCollection().AddBlazorNativeSdk().BuildServiceProvider();
        await Assert.ThrowsAsync<InvalidOperationException>(() => WpfNativeSdk.StartAsync(provider, [], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StartAsync_publishes_the_launch_activation_without_single_instance()
    {
        await using var provider = _host.Build(o => o.SingleInstance = false);
        var ct = TestContext.Current.CancellationToken;

        var first = WpfNativeSdk.StartAsync(provider, ["bnstest://orders/42"], ct);
        Assert.Same(first, WpfNativeSdk.StartAsync(provider, ["ignored"], ct));
        Assert.True(await first);

        var launch = provider.GetRequiredService<ActivationService>().LaunchActivation;
        Assert.NotNull(launch);
        Assert.Equal(ActivationKind.DeepLink, launch.Kind);
        Assert.Equal(new Uri("bnstest://orders/42"), launch.Uri);
        Assert.True(launch.IsColdStart);
        Assert.True(provider.GetRequiredService<NativeSdkRuntime>().IsStarted);
        Assert.Null(provider.GetRequiredService<WpfStartupCoordinator>().Lease);
    }

    [Fact]
    public async Task StartAsync_enforces_a_single_instance_and_forwards_arguments_to_the_primary()
    {
        var ct = TestContext.Current.CancellationToken;
        var appId = "bns-wpf-test-" + Guid.NewGuid().ToString("N")[..10];

        await using var primary = _host.Build(appId: appId);
        Assert.True(await WpfNativeSdk.StartAsync(primary, [], ct));
        var lease = primary.GetRequiredService<WpfStartupCoordinator>().Lease;
        Assert.NotNull(lease);
        Assert.True(lease.IsPrimary);

        var activations = primary.GetRequiredService<ActivationService>();
        var received = new List<AppActivation>();
        using var subscription = activations.Subscribe((activation, _) =>
        {
            lock (received)
            {
                received.Add(activation);
            }

            return ValueTask.FromResult(false);
        });
        activations.MarkReady();

        await using (var secondary = _host.Build(appId: appId))
        {
            Assert.False(await WpfNativeSdk.StartAsync(secondary, ["bnstest://orders/7"], ct));
            Assert.Null(secondary.GetRequiredService<WpfStartupCoordinator>().Lease);
        }

        await Wait.UntilAsync(() =>
        {
            lock (received)
            {
                return received.Exists(a => a.Kind == ActivationKind.DeepLink && a.Uri == new Uri("bnstest://orders/7"));
            }
        }, ct);

        lock (received)
        {
            var forwarded = received.Single(a => a.Kind == ActivationKind.DeepLink);
            Assert.False(forwarded.IsColdStart);
            Assert.Contains(received, a => a.Kind == ActivationKind.Launch && a.IsColdStart);
        }
    }

    public void Dispose() => _host.Dispose();
}
