using BlazorNativeSdk.Browser.Hosting;
using BlazorNativeSdk.Browser.Interop;
using BlazorNativeSdk.Browser.Services;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;
using BlazorNativeSdk.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace BlazorNativeSdk.Browser.Tests;

/// <summary>An IJSRuntime that behaves like Blazor during prerendering: every call throws InvalidOperationException.</summary>
internal sealed class PrerenderingJSRuntime : IJSRuntime
{
    public int Calls { get; private set; }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
    {
        Calls++;
        throw new InvalidOperationException("JavaScript interop calls cannot be issued at this time. This is because the component is being statically rendered.");
    }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
}

public sealed class RegistrationTests
{
    private static ServiceProvider BuildProvider(bool isWebAssembly, Action<BrowserNativeOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IJSRuntime, PrerenderingJSRuntime>();
        services.AddBlazorNativeSdk(sdk =>
        {
            if (isWebAssembly)
            {
                BrowserNativeSdkBuilderExtensions.AddBrowserCore(sdk, configure, isWebAssembly: true);
            }
            else
            {
                sdk.AddBrowser(configure);
            }
        });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public static TheoryData<Type, Type> Capabilities => new()
    {
        { typeof(ICameraService), typeof(BrowserCameraService) },
        { typeof(IBarcodeScanner), typeof(BrowserBarcodeScanner) },
        { typeof(IFilePickerService), typeof(BrowserFilePickerService) },
        { typeof(ISecureStorage), typeof(BrowserSecureStorage) },
        { typeof(IShareService), typeof(BrowserShareService) },
        { typeof(IClipboardService), typeof(BrowserClipboardService) },
        { typeof(IPermissionService), typeof(BrowserPermissionService) },
        { typeof(INotificationService), typeof(BrowserNotificationService) },
        { typeof(IAppLifecycle), typeof(BrowserAppLifecycle) },
        { typeof(IConnectivityService), typeof(BrowserConnectivityService) },
        { typeof(IBiometricService), typeof(BrowserBiometricService) },
        { typeof(IAppStorage), typeof(BrowserAppStorage) },
    };

    [Theory]
    [MemberData(nameof(Capabilities))]
    public void Every_capability_resolves_to_the_browser_implementation(Type service, Type implementation)
    {
        using var provider = BuildProvider(isWebAssembly: false);
        using var scope = provider.CreateScope();

        Assert.IsType(implementation, scope.ServiceProvider.GetRequiredService(service));
    }

    [Fact]
    public void Server_host_uses_scoped_services_and_keeps_the_server_state_store()
    {
        var services = new ServiceCollection();
        services.AddBlazorNativeSdk(sdk => sdk.AddBrowser());

        Assert.Equal(ServiceLifetime.Scoped, services.Last(d => d.ServiceType == typeof(ICameraService)).Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, services.Single(d => d.ServiceType == typeof(BrowserInterop)).Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, services.Last(d => d.ServiceType == typeof(IActivationPublisher)).Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, services.Last(d => d.ServiceType == typeof(IAppActivationService)).Lifetime);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(INativeSdkStartupTask) && d.ImplementationType == typeof(BrowserStartupTask));

        using var provider = BuildProvider(isWebAssembly: false);
        var host = provider.GetRequiredService<NativeHostInfo>();
        Assert.Equal(NativePlatform.Browser, host.Platform);
        Assert.Equal(NativeHostKind.Server, host.Host);
        Assert.IsType<InMemoryStateStore>(provider.GetRequiredService<INativeStateStore>());
    }

    [Fact]
    public void Server_activation_pipeline_is_isolated_per_circuit()
    {
        using var provider = BuildProvider(isWebAssembly: false);
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        var a = first.ServiceProvider.GetRequiredService<IActivationPublisher>();
        var b = second.ServiceProvider.GetRequiredService<IActivationPublisher>();

        Assert.NotSame(a, b);
        Assert.Same(a, first.ServiceProvider.GetRequiredService<IAppActivationService>());
        Assert.Same(a, first.ServiceProvider.GetRequiredService<INavigationOutletRegistry>());
    }

    [Fact]
    public void WebAssembly_host_uses_singletons_local_storage_and_a_startup_task()
    {
        using var provider = BuildProvider(isWebAssembly: true);

        Assert.Equal(NativeHostKind.WebAssembly, provider.GetRequiredService<NativeHostInfo>().Host);
        Assert.Same(provider.GetRequiredService<ICameraService>(), provider.GetRequiredService<ICameraService>());
        Assert.IsType<LocalStorageStateStore>(provider.GetRequiredService<INativeStateStore>());
        Assert.Contains(provider.GetServices<INativeSdkStartupTask>(), t => t is BrowserStartupTask);
    }

    [Fact]
    public async Task Registry_reports_browser_providers()
    {
        await using var provider = BuildProvider(isWebAssembly: false);
        await using var scope = provider.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<ICapabilityRegistry>();

        Assert.IsType<BrowserCameraService>(registry.GetProvider(NativeCapabilities.Camera));
        Assert.IsType<BrowserBiometricService>(registry.GetProvider(NativeCapabilities.Biometrics));
        Assert.Equal(NativeHostKind.Server, registry.Host.Host);
    }

    [Fact]
    public void Scopes_can_be_disposed_synchronously_and_twice()
    {
        using var provider = BuildProvider(isWebAssembly: false);
        var scope = provider.CreateScope();
        foreach (var service in Capabilities.Select(row => row.Data.Item1))
        {
            scope.ServiceProvider.GetRequiredService(service);
        }

        scope.ServiceProvider.GetRequiredService<BrowserStartup>();
        scope.Dispose();
        scope.Dispose();
    }

    [Fact]
    public void Invalid_options_are_rejected()
    {
        using var provider = BuildProvider(isWebAssembly: false, o => o.ProtocolHandlerQueryParameter = "bad name");
        using var scope = provider.CreateScope();

        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => scope.ServiceProvider.GetRequiredService<IClipboardService>());
    }

    [Fact]
    public async Task Prerendering_reports_unknown_status_and_host_not_ready()
    {
        await using var provider = BuildProvider(isWebAssembly: false);
        await using var scope = provider.CreateAsyncScope();

        var status = await scope.ServiceProvider.GetRequiredService<ICameraService>().GetStatusAsync(TestContext.Current.CancellationToken);
        var write = await scope.ServiceProvider.GetRequiredService<IClipboardService>().SetTextAsync("x", TestContext.Current.CancellationToken);
        var started = await scope.ServiceProvider.GetRequiredService<BrowserStartup>().StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CapabilityAvailability.Unknown, status.Availability);
        Assert.Equal(NativeResultStatus.Unavailable, write.Status);
        Assert.Equal(NativeErrorCodes.HostNotReady, write.Error!.Code);
        Assert.False(started);

        // The failed import is not cached: the next call tries again.
        var runtime = (PrerenderingJSRuntime)provider.GetRequiredService<IJSRuntime>();
        var before = runtime.Calls;
        await scope.ServiceProvider.GetRequiredService<IClipboardService>().SetTextAsync("x", TestContext.Current.CancellationToken);
        Assert.True(runtime.Calls > before);
    }
}
