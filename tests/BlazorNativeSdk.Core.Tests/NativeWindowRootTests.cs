// RenderTree types (Renderer, RenderBatch): a minimal renderer is the point of these tests.
#pragma warning disable BL0006

using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using BlazorNativeSdk.Components;
using BlazorNativeSdk.Lifecycle;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlazorNativeSdk.Tests;

/// <summary>
/// Renders <see cref="NativeActivationOutlet"/> in separate DI scopes (one per web view, as Blazor Hybrid hosts do) with a
/// minimal renderer, and checks which outlet the activation pipeline navigates.
/// </summary>
public sealed class NativeWindowRootTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Scope_attaches_once_and_defaults_to_the_main_web_view()
    {
        var scope = new NativeWindowScope();
        Assert.Null(scope.WindowId);
        Assert.True(scope.RoutesActivations);

        scope.Attach("settings", routesActivations: false);
        scope.Attach("other", routesActivations: true);
        Assert.Equal("settings", scope.WindowId);
        Assert.False(scope.RoutesActivations);
    }

    [Fact]
    public async Task Outlet_in_a_secondary_window_is_not_primary_and_does_not_take_navigation_from_the_main_window()
    {
        await using var provider = BuildProvider();
        var activation = provider.GetRequiredService<ActivationService>();

        await using var main = await RootView.RenderAsync(provider, typeof(NativeActivationOutlet), parameters: null);
        await using var secondary = await RootView.RenderAsync(
            provider,
            typeof(NativeWindowRoot),
            NativeWindowRoot.CreateParameters("settings", typeof(NativeActivationOutlet)));

        Assert.Equal("settings", secondary.Services.GetRequiredService<NativeWindowScope>().WindowId);
        Assert.False(secondary.Services.GetRequiredService<NativeWindowScope>().RoutesActivations);
        Assert.Null(main.Services.GetRequiredService<NativeWindowScope>().WindowId);

        // The secondary outlet attached last; it would win if it were primary.
        await activation.PublishAsync(new AppActivation { Kind = ActivationKind.DeepLink, Uri = new Uri("myapp://orders/1") }, Ct);
        await activation.WhenIdleAsync(Ct);
        Assert.Equal(["/orders/1"], main.Navigation.Navigations);
        Assert.Empty(secondary.Navigation.Navigations);

        // A window that opted into activations (RouteActivationsToMainWindow = false) is primary again.
        await using var routing = await RootView.RenderAsync(
            provider,
            typeof(NativeWindowRoot),
            NativeWindowRoot.CreateParameters("viewer", typeof(NativeActivationOutlet), routesActivations: true));
        await activation.PublishAsync(new AppActivation { Kind = ActivationKind.DeepLink, Uri = new Uri("myapp://orders/2") }, Ct);
        await activation.WhenIdleAsync(Ct);
        Assert.Equal(["/orders/2"], routing.Navigation.Navigations);
        Assert.Equal(["/orders/1"], main.Navigation.Navigations);
        Assert.Empty(secondary.Navigation.Navigations);
    }

    [Fact]
    public async Task Explicit_IsPrimary_overrides_the_window_scope()
    {
        await using var provider = BuildProvider();
        var activation = provider.GetRequiredService<ActivationService>();

        await using var main = await RootView.RenderAsync(provider, typeof(NativeActivationOutlet), parameters: null);
        await using var secondary = await RootView.RenderAsync(
            provider,
            typeof(NativeWindowRoot),
            NativeWindowRoot.CreateParameters(
                "settings",
                typeof(NativeActivationOutlet),
                new Dictionary<string, object?> { [nameof(NativeActivationOutlet.IsPrimary)] = true }));

        await activation.PublishAsync(new AppActivation { Kind = ActivationKind.DeepLink, Uri = new Uri("myapp://orders/3") }, Ct);
        await activation.WhenIdleAsync(Ct);
        Assert.Equal(["/orders/3"], secondary.Navigation.Navigations);
        Assert.Empty(main.Navigation.Navigations);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddBlazorNativeSdk(sdk => sdk.ConfigureDeepLinks(l => l.Schemes.Add("myapp")));
        services.AddScoped<RecordingNavigationManager>();
        services.AddScoped<NavigationManager>(static sp => sp.GetRequiredService<RecordingNavigationManager>());
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>One "web view": a DI scope and a renderer with one root component.</summary>
    private sealed class RootView : IAsyncDisposable
    {
        private readonly AsyncServiceScope _scope;
        private readonly MinimalRenderer _renderer;

        private RootView(AsyncServiceScope scope)
        {
            _scope = scope;
            _renderer = new MinimalRenderer(scope.ServiceProvider);
        }

        public IServiceProvider Services => _scope.ServiceProvider;

        public RecordingNavigationManager Navigation => Services.GetRequiredService<RecordingNavigationManager>();

        public static async Task<RootView> RenderAsync(IServiceProvider services, Type component, IDictionary<string, object?>? parameters)
        {
            var view = new RootView(services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope());
            await view._renderer.RenderRootAsync(component, parameters is null ? ParameterView.Empty : ParameterView.FromDictionary(parameters));
            return view;
        }

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _scope.DisposeAsync();
        }
    }

    private sealed class MinimalRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public Task RenderRootAsync(Type component, ParameterView parameters) => Dispatcher.InvokeAsync(() =>
        {
            var id = AssignRootComponentId(InstantiateComponent(component));
            return RenderRootComponentAsync(id, parameters);
        });

        protected override void HandleException(Exception exception) => ExceptionDispatchInfo.Capture(exception).Throw();

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    }

    private sealed class RecordingNavigationManager : NavigationManager
    {
        private readonly ConcurrentQueue<string> _navigations = new();

        public RecordingNavigationManager() => Initialize("app://localhost/", "app://localhost/");

        public List<string> Navigations => [.. _navigations];

        protected override void NavigateToCore(string uri, NavigationOptions options) => _navigations.Enqueue(uri);
    }
}
