using System.Collections.Concurrent;
using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk.Tests;

public sealed class ActivationServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ActivationService Create(Action<DeepLinkOptions>? configure = null)
    {
        var options = new NativeSdkOptions { AppId = "test" };
        options.DeepLinks.Schemes.Add("myapp");
        options.DeepLinks.Hosts.Add("contoso.com");
        configure?.Invoke(options.DeepLinks);
        return new ActivationService(Options.Create(options), NullLogger<ActivationService>.Instance);
    }

    private static AppActivation Link(string uri, bool coldStart = false) =>
        new() { Kind = ActivationKind.DeepLink, Uri = new Uri(uri), IsColdStart = coldStart };

    [Fact]
    public async Task Buffers_activations_until_an_outlet_attaches_and_replays_them_in_order()
    {
        await using var service = Create();
        var outlet = new RecordingOutlet();

        await service.PublishAsync(Link("myapp://orders/1", coldStart: true), Ct);
        await service.PublishAsync(Link("https://contoso.com/orders/2?x=1"), Ct);
        await Task.Delay(50, Ct);
        Assert.Empty(outlet.Routes);

        using var _ = service.Attach(outlet);
        await service.WhenIdleAsync(Ct);

        Assert.Equal(["/orders/1", "/orders/2?x=1"], outlet.Routes);
        Assert.Equal(new Uri("myapp://orders/1"), service.LaunchActivation!.Uri);
    }

    [Fact]
    public async Task Handlers_can_claim_an_activation()
    {
        await using var service = Create();
        var outlet = new RecordingOutlet();
        using var attach = service.Attach(outlet);
        var seen = new ConcurrentQueue<AppActivation>();
        using var subscription = service.Subscribe((a, _) =>
        {
            seen.Enqueue(a);
            return ValueTask.FromResult(a.Uri!.AbsolutePath.Contains("claimed", StringComparison.Ordinal));
        });

        await service.PublishAsync(Link("myapp://x/claimed"), Ct);
        await service.PublishAsync(Link("myapp://x/free"), Ct);
        await service.WhenIdleAsync(Ct);

        Assert.Equal(2, seen.Count);
        Assert.Equal(["/x/free"], outlet.Routes);
    }

    [Fact]
    public async Task Mapped_routes_receive_values_and_are_not_navigated()
    {
        await using var service = Create();
        var outlet = new RecordingOutlet();
        using var attach = service.Attach(outlet);
        ActivationRouteMatch? match = null;
        using var map = service.MapRoute("/orders/{id}", (m, _) =>
        {
            match = m;
            return ValueTask.CompletedTask;
        });

        await service.PublishAsync(Link("myapp://orders/42?tab=items"), Ct);
        await service.WhenIdleAsync(Ct);

        Assert.NotNull(match);
        Assert.Equal("42", match.Values["id"]);
        Assert.Equal("items", match.Values["tab"]);
        Assert.Empty(outlet.Routes);
    }

    [Theory]
    [InlineData("https://evil.com/orders/1")]
    [InlineData("otherapp://orders/1")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    public async Task Untrusted_urls_are_never_navigated(string uri)
    {
        await using var service = Create();
        var outlet = new RecordingOutlet();
        using var attach = service.Attach(outlet);

        await service.PublishAsync(Link(uri), Ct);
        await service.WhenIdleAsync(Ct);

        Assert.Empty(outlet.Routes);
    }

    [Theory]
    [InlineData("//evil.com/path")]
    [InlineData("/\\evil.com")]
    [InlineData("https://evil.com")]
    [InlineData("relative/path")]
    [InlineData("/ok/../x\n")]
    public void Unsafe_explicit_routes_are_rejected(string route)
    {
        var service = Create();
        Assert.False(service.TryResolveRoute(new AppActivation { Kind = ActivationKind.Notification, Route = route }, out _));
    }

    [Fact]
    public async Task Unrecognized_links_are_dropped_before_handlers_unless_opted_in()
    {
        foreach (var optIn in new[] { false, true })
        {
            await using var service = Create(o => o.DeliverUnrecognizedLinks = optIn);
            var seen = new ConcurrentQueue<Uri>();
            using var sub = service.Subscribe((a, _) =>
            {
                seen.Enqueue(a.Uri!);
                return ValueTask.FromResult(true);
            });
            service.MarkReady();

            await service.PublishAsync(Link("otherapp://steal?token=1"), Ct);
            await service.PublishAsync(Link("https://evil.com/x"), Ct);
            await service.PublishAsync(Link("myapp://orders/1"), Ct);
            await service.WhenIdleAsync(Ct);

            Assert.Equal(optIn ? 3 : 1, seen.Count);
        }
    }

    [Fact]
    public void Custom_scheme_mapping_can_use_path_only()
    {
        var service = Create(o => o.CustomSchemeHostIsFirstSegment = false);
        Assert.Equal("/orders/1", service.ResolveUri(new Uri("myapp://ignored/orders/1")));
    }

    [Fact]
    public void Custom_resolver_runs_first_but_must_return_a_safe_route()
    {
        var service = Create(o => o.RouteResolver = u => u.Host == "safe" ? "/custom" : "//evil");
        Assert.Equal("/custom", service.ResolveUri(new Uri("other://safe")));
        Assert.Null(service.ResolveUri(new Uri("other://unsafe")));
    }

    [Fact]
    public async Task Notification_routes_take_precedence_over_uris()
    {
        await using var service = Create();
        var outlet = new RecordingOutlet();
        using var attach = service.Attach(outlet);

        await service.PublishAsync(new AppActivation { Kind = ActivationKind.Notification, NotificationId = "n1", Route = "/inbox/7" }, Ct);
        await service.WhenIdleAsync(Ct);

        Assert.Equal(["/inbox/7"], outlet.Routes);
    }

    [Fact]
    public async Task Duplicate_activations_within_the_window_are_ignored()
    {
        await using var service = Create();
        var outlet = new RecordingOutlet();
        using var attach = service.Attach(outlet);
        var now = DateTimeOffset.UtcNow;

        await service.PublishAsync(Link("myapp://a/1") with { ReceivedAt = now }, Ct);
        await service.PublishAsync(Link("myapp://a/1") with { ReceivedAt = now.AddMilliseconds(100) }, Ct);
        await service.PublishAsync(Link("myapp://a/1") with { ReceivedAt = now.AddSeconds(10) }, Ct);
        await service.WhenIdleAsync(Ct);

        Assert.Equal(2, outlet.Routes.Count);
    }

    [Fact]
    public async Task Oldest_activations_are_dropped_when_the_buffer_is_full()
    {
        await using var service = Create(o => o.MaxQueuedActivations = 2);
        var outlet = new RecordingOutlet();

        for (var i = 1; i <= 4; i++)
        {
            await service.PublishAsync(Link($"myapp://n/{i}"), Ct);
        }

        using var attach = service.Attach(outlet);
        await service.WhenIdleAsync(Ct);

        Assert.Equal(["/n/3", "/n/4"], outlet.Routes);
    }

    [Fact]
    public async Task Primary_outlet_receives_navigation()
    {
        await using var service = Create();
        var primary = new RecordingOutlet();
        var secondary = new RecordingOutlet { IsPrimary = false };
        using var a = service.Attach(primary);
        using var b = service.Attach(secondary);

        await service.PublishAsync(Link("myapp://x/1"), Ct);
        await service.WhenIdleAsync(Ct);

        Assert.Single(primary.Routes);
        Assert.Empty(secondary.Routes);
    }

    [Fact]
    public async Task MarkReady_processes_activations_without_an_outlet()
    {
        await using var service = Create();
        var handled = 0;
        using var sub = service.Subscribe((_, _) =>
        {
            Interlocked.Increment(ref handled);
            return ValueTask.FromResult(true);
        });

        await service.PublishAsync(Link("myapp://x/1"), Ct);
        service.MarkReady();
        await service.WhenIdleAsync(Ct);

        Assert.Equal(1, handled);
    }

    [Fact]
    public async Task Translators_rewrite_or_drop_activations_before_routing()
    {
        var options = new NativeSdkOptions { AppId = "test" };
        options.DeepLinks.Schemes.Add("myapp");
        await using var service = new ActivationService(
            Options.Create(options),
            NullLogger<ActivationService>.Instance,
            [new DelegateTranslator(a => a.Uri?.Host == "forged" ? null : a), new DelegateTranslator(a => a.Uri?.Host == "toast" ? a with { Uri = null, Kind = ActivationKind.Notification, Route = "/inbox" } : a)]);
        var outlet = new RecordingOutlet();
        using var attach = service.Attach(outlet);

        await service.PublishAsync(Link("myapp://forged/x"), Ct);
        await service.PublishAsync(Link("myapp://toast/x"), Ct);
        await service.WhenIdleAsync(Ct);

        Assert.Equal(["/inbox"], outlet.Routes);
    }

    [Fact]
    public async Task Dependency_injection_handlers_run_first_even_before_the_ui_is_ready()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddBlazorNativeSdk(sdk => sdk
            .ConfigureDeepLinks(l => l.Schemes.Add("myapp"))
            .AddActivationHandler<ArchiveHandler>());
        await using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var service = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<ActivationService>(provider);
        var outlet = new RecordingOutlet();
        ArchiveHandler.Handled.Clear();

        // A background notification action in a process that never shows UI: handled without an outlet, never navigated.
        await service.PublishAsync(new AppActivation { Kind = ActivationKind.Notification, NotificationId = "n1", ActionId = "archive", Route = "/inbox/1", IsBackground = true }, Ct);
        await PollAsync(() => ArchiveHandler.Handled.Count == 1);

        // An unclaimed background activation is never navigated; foreground deep links still wait for the UI.
        await service.PublishAsync(new AppActivation { Kind = ActivationKind.Notification, NotificationId = "n2", Route = "/inbox/2", IsBackground = true, ActionId = "mark-read" }, Ct);
        await service.PublishAsync(Link("myapp://orders/5"), Ct);
        using var attach = service.Attach(outlet);
        await service.WhenIdleAsync(Ct);

        Assert.Equal(["n1"], ArchiveHandler.Handled.ToArray());
        Assert.Equal(["/orders/5"], outlet.Routes);
    }

    private static async Task PollAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(10, Ct);
        }
    }

    private sealed class DelegateTranslator(Func<AppActivation, AppActivation?> translate) : IActivationTranslator
    {
        public AppActivation? Translate(AppActivation activation) => translate(activation);
    }

    private sealed class ArchiveHandler : IActivationHandler
    {
        public static ConcurrentQueue<string> Handled { get; } = new();

        public ValueTask<bool> HandleAsync(AppActivation activation, CancellationToken cancellationToken)
        {
            if (activation.ActionId == "archive")
            {
                Handled.Enqueue(activation.NotificationId!);
                return ValueTask.FromResult(true);
            }

            return ValueTask.FromResult(false);
        }
    }

    private sealed class RecordingOutlet : INavigationOutlet
    {
        private readonly ConcurrentQueue<string> _routes = new();

        public bool IsPrimary { get; init; } = true;

        public List<string> Routes => [.. _routes];

        public ValueTask NavigateAsync(string route, CancellationToken cancellationToken)
        {
            _routes.Enqueue(route);
            return ValueTask.CompletedTask;
        }
    }
}
