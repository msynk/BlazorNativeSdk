using System.Windows;
using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.Lifecycle;

namespace BlazorNativeSdk.Wpf.Tests;

public sealed class WindowSettingsTests
{
    [Fact]
    public void Defaults_center_a_resizable_window_with_the_app_title()
    {
        var settings = Create(new WindowOptions());

        Assert.Equal("App", settings.Title);
        Assert.True(double.IsNaN(settings.Width));
        Assert.True(double.IsNaN(settings.Height));
        Assert.True(double.IsNaN(settings.Left));
        Assert.True(double.IsNaN(settings.Top));
        Assert.Equal(0, settings.MinWidth);
        Assert.Equal(0, settings.MinHeight);
        Assert.Equal(WindowStartupLocation.CenterScreen, settings.StartupLocation);
        Assert.Equal(ResizeMode.CanResize, settings.ResizeMode);
        Assert.False(settings.Topmost);
        Assert.Equal(WindowState.Normal, settings.WindowState);
        Assert.False(settings.EnterFullScreen);
        Assert.Null(settings.ExplicitBounds);
        Assert.Null(settings.Placement);
        Assert.Null(settings.StartPath);
    }

    [Fact]
    public void Maps_every_window_option_to_wpf_properties()
    {
        var settings = Create(new WindowOptions
        {
            Title = "Settings",
            Width = 800,
            Height = 600,
            X = 10,
            Y = 20,
            MinWidth = 300,
            MinHeight = 200,
            Resizable = false,
            AlwaysOnTop = true,
            InitialState = NativeWindowState.Maximized,
            Route = "settings/general",
        });

        Assert.Equal("Settings", settings.Title);
        Assert.Equal(800, settings.Width);
        Assert.Equal(600, settings.Height);
        Assert.Equal(10, settings.Left);
        Assert.Equal(20, settings.Top);
        Assert.Equal(300, settings.MinWidth);
        Assert.Equal(200, settings.MinHeight);
        Assert.Equal(WindowStartupLocation.Manual, settings.StartupLocation);
        Assert.Equal(ResizeMode.CanMinimize, settings.ResizeMode);
        Assert.True(settings.Topmost);
        Assert.Equal(WindowState.Maximized, settings.WindowState);
        Assert.Equal(new WindowBounds(10, 20, 800, 600), settings.ExplicitBounds);
        Assert.Equal("/settings/general", settings.StartPath);
    }

    [Fact]
    public void Owned_windows_center_on_their_owner_and_partial_positions_use_system_defaults()
    {
        Assert.Equal(WindowStartupLocation.CenterOwner, Create(new WindowOptions(), hasOwner: true).StartupLocation);

        var onlyX = Create(new WindowOptions { X = 15, Width = 400 });
        Assert.Equal(WindowStartupLocation.Manual, onlyX.StartupLocation);
        Assert.Equal(15, onlyX.Left);
        Assert.True(double.IsNaN(onlyX.Top));
        Assert.Null(onlyX.ExplicitBounds);

        var positionOnly = Create(new WindowOptions { X = 5, Y = 6 });
        Assert.Equal(5, positionOnly.ExplicitBounds!.Value.X);
        Assert.True(double.IsNaN(positionOnly.ExplicitBounds.Value.Width));
    }

    [Fact]
    public void A_persisted_placement_wins_over_requested_bounds_and_state()
    {
        var placement = new WindowPlacement { Bounds = new WindowBounds(100, 120, 640, 480), State = NativeWindowState.Maximized, DisplayId = @"\\.\DISPLAY1" };
        var settings = Create(new WindowOptions { X = 1, Y = 2, Width = 300, Height = 200 }, placement, hasOwner: true);

        Assert.Same(placement, settings.Placement);
        Assert.Equal(100, settings.Left);
        Assert.Equal(120, settings.Top);
        Assert.Equal(640, settings.Width);
        Assert.Equal(480, settings.Height);
        Assert.Equal(WindowStartupLocation.Manual, settings.StartupLocation);
        Assert.Null(settings.ExplicitBounds);
        Assert.Equal(WindowState.Maximized, settings.WindowState);
    }

    [Theory]
    [InlineData(NativeWindowState.Normal, null, NativeWindowState.Normal)]
    [InlineData(NativeWindowState.Maximized, null, NativeWindowState.Maximized)]
    [InlineData(NativeWindowState.FullScreen, null, NativeWindowState.FullScreen)]
    [InlineData(NativeWindowState.Normal, NativeWindowState.Maximized, NativeWindowState.Maximized)]
    [InlineData(NativeWindowState.Maximized, NativeWindowState.Normal, NativeWindowState.Normal)]
    [InlineData(NativeWindowState.Normal, NativeWindowState.FullScreen, NativeWindowState.FullScreen)]
    [InlineData(NativeWindowState.Normal, NativeWindowState.Minimized, NativeWindowState.Normal)]
    [InlineData(NativeWindowState.Minimized, NativeWindowState.Maximized, NativeWindowState.Minimized)]
    [InlineData(NativeWindowState.Minimized, null, NativeWindowState.Minimized)]
    public void Resolves_the_initial_state(NativeWindowState requested, NativeWindowState? persisted, NativeWindowState expected)
    {
        var placement = persisted is { } state ? new WindowPlacement { Bounds = new WindowBounds(0, 0, 100, 100), State = state } : null;
        Assert.Equal(expected, WpfWindowSettings.ResolveInitialState(requested, placement));
    }

    [Fact]
    public void Full_screen_starts_normal_and_switches_after_showing()
    {
        var settings = Create(new WindowOptions { InitialState = NativeWindowState.FullScreen });
        Assert.Equal(WindowState.Normal, settings.WindowState);
        Assert.True(settings.EnterFullScreen);
    }

    [Fact]
    public void Ignores_a_degenerate_placement()
    {
        var placement = new WindowPlacement { Bounds = new WindowBounds(0, 0, 0, 100), State = NativeWindowState.Maximized };
        var settings = Create(new WindowOptions { Width = 300 }, placement);
        Assert.Null(settings.Placement);
        Assert.Equal(300, settings.Width);
        Assert.Equal(WindowState.Normal, settings.WindowState);
    }

    [Theory]
    [InlineData(NativeWindowState.Normal, WindowState.Normal)]
    [InlineData(NativeWindowState.Minimized, WindowState.Minimized)]
    [InlineData(NativeWindowState.Maximized, WindowState.Maximized)]
    [InlineData(NativeWindowState.FullScreen, WindowState.Normal)]
    public void Maps_states_to_wpf(NativeWindowState state, WindowState expected)
    {
        Assert.Equal(expected, WpfWindowSettings.ToWpfState(state));
        if (state != NativeWindowState.FullScreen)
        {
            Assert.Equal(state, WpfWindowSettings.FromWpfState(expected));
        }
    }

    [Theory]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("settings", "/settings")]
    [InlineData(" /orders/42 ", "/orders/42")]
    [InlineData("/search?q=a:b#top", "/search?q=a:b#top")]
    [InlineData("files/a:b", "/files/a:b")]
    public void Normalizes_app_routes(string route, string expected)
    {
        Assert.True(WpfWindowSettings.TryNormalizeRoute(route, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("https://contoso.com/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("C:/Windows")]
    [InlineData("//contoso.com/path")]
    [InlineData("/\\contoso.com")]
    [InlineData("/a\nb")]
    public void Rejects_routes_that_leave_the_app(string route)
    {
        Assert.False(WpfWindowSettings.TryNormalizeRoute(route, out _));
        var result = WpfWindowSettings.Create(new WindowOptions { Route = route }, null, "App", hasOwner: false);
        Assert.Equal(NativeErrorCodes.InvalidArgument, result.Error!.Code);
    }

    [Fact]
    public void Ignores_the_route_when_a_root_component_is_set()
    {
        var settings = Create(new WindowOptions { RootComponent = typeof(WindowSettingsTests), Route = "https://ignored" });
        Assert.Null(settings.StartPath);
    }

    public static TheoryData<WindowOptions> InvalidOptions => new()
    {
        new WindowOptions { Width = 0 },
        new WindowOptions { Height = -1 },
        new WindowOptions { Width = double.NaN },
        new WindowOptions { Height = double.PositiveInfinity },
        new WindowOptions { MinWidth = -1 },
        new WindowOptions { MinHeight = double.NaN },
        new WindowOptions { X = double.NegativeInfinity },
        new WindowOptions { Y = double.NaN },
        new WindowOptions { Id = " " },
        new WindowOptions { Id = new string('w', WpfWindowSettings.MaxIdLength + 1) },
        new WindowOptions { Id = "a", OwnerId = "a" },
        new WindowOptions { InitialState = (NativeWindowState)42 },
    };

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void Rejects_invalid_options(WindowOptions options)
    {
        var error = WpfWindowSettings.Validate(options);
        Assert.NotNull(error);
        Assert.Equal(NativeResultStatus.Failed, error.Status);
        Assert.Equal(NativeErrorCodes.InvalidArgument, error.Code);
        Assert.Equal(NativeCapabilities.Windows, error.Capability);
        Assert.False(WpfWindowSettings.Create(options, null, "App", hasOwner: false).IsSuccess);
    }

    private static WpfWindowSettings Create(WindowOptions options, WindowPlacement? placement = null, bool hasOwner = false)
    {
        var result = WpfWindowSettings.Create(options, placement, "App", hasOwner);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        return result.Value!;
    }
}

public sealed class LifecycleMappingTests
{
    [Theory]
    // Launching: nothing shown, never activated.
    [InlineData(false, false, false, false, 0, null)]
    // Activation alone drives apps that never show a tracked window.
    [InlineData(false, true, true, false, 0, AppLifecycleState.Active)]
    [InlineData(false, false, true, false, 0, AppLifecycleState.Inactive)]
    // Visible windows: foreground or not.
    [InlineData(false, true, true, true, 1, AppLifecycleState.Active)]
    [InlineData(false, false, true, true, 2, AppLifecycleState.Inactive)]
    [InlineData(false, false, false, true, 1, AppLifecycleState.Inactive)]
    // Every tracked window minimized, hidden or closed.
    [InlineData(false, false, true, true, 0, AppLifecycleState.Background)]
    [InlineData(false, true, true, true, 0, AppLifecycleState.Background)]
    // Exit and session end win over everything.
    [InlineData(true, true, true, true, 1, AppLifecycleState.Terminating)]
    [InlineData(true, false, false, false, 0, AppLifecycleState.Terminating)]
    public void Maps_wpf_signals_to_lifecycle_states(bool terminating, bool active, bool activated, bool shown, int shownCount, AppLifecycleState? expected)
    {
        var snapshot = new WpfLifecycleSnapshot(terminating, active, activated, shown, shownCount);
        Assert.Equal(expected, WpfLifecycleStateMapper.Map(snapshot));
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    public void Focuses_the_main_window_only_for_foreground_activations_of_a_running_app(bool enabled, bool background, bool coldStart, bool expected)
    {
        var activation = new AppActivation { Kind = ActivationKind.DeepLink, IsBackground = background, IsColdStart = coldStart };
        Assert.Equal(expected, WpfActivationFocusHandler.ShouldFocus(activation, enabled));
    }
}

public sealed class WindowRootComponentTests
{
    [Fact]
    public void Opened_windows_render_their_root_through_NativeWindowRoot_without_routing_activations()
    {
        var parameters = new Dictionary<string, object?> { ["Value"] = 42 };
        var root = WpfWindowManager.CreateRootComponent(
            "settings",
            typeof(WindowRootComponentTests),
            new WindowOptions { RootComponent = typeof(WindowRootComponentTests), Parameters = parameters },
            new WpfNativeOptions { RootComponentSelector = "#root" });

        Assert.Equal("#root", root.Selector);
        Assert.Equal(typeof(Components.NativeWindowRoot), root.ComponentType);
        Assert.NotNull(root.Parameters);
        Assert.Equal("settings", root.Parameters[nameof(Components.NativeWindowRoot.WindowId)]);
        Assert.Equal(typeof(WindowRootComponentTests), root.Parameters[nameof(Components.NativeWindowRoot.Component)]);
        Assert.False(Assert.IsType<bool>(root.Parameters[nameof(Components.NativeWindowRoot.RoutesActivations)]));
        var forwarded = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(root.Parameters[nameof(Components.NativeWindowRoot.ComponentParameters)]);
        Assert.Equal(42, forwarded["Value"]);
    }

    [Fact]
    public void Route_windows_use_the_default_root_without_parameters_and_can_opt_into_activations()
    {
        var root = WpfWindowManager.CreateRootComponent(
            "viewer",
            typeof(WindowRootComponentTests),
            new WindowOptions { Route = "/viewer", Parameters = new Dictionary<string, object?> { ["Ignored"] = 1 } },
            new WpfNativeOptions { RouteActivationsToMainWindow = false });

        Assert.Equal("#app", root.Selector);
        Assert.Null(root.Parameters![nameof(Components.NativeWindowRoot.ComponentParameters)]);
        Assert.True(Assert.IsType<bool>(root.Parameters[nameof(Components.NativeWindowRoot.RoutesActivations)]));
        Assert.True(new WpfNativeOptions().RouteActivationsToMainWindow);
    }
}
