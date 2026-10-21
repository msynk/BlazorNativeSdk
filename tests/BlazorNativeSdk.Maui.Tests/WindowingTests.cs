using BlazorNativeSdk.Lifecycle;

namespace BlazorNativeSdk.Maui.Tests;

/// <summary>
/// The MAUI window lifecycle from docs/adapters/maui.md: Active while one of the app's windows is activated, Inactive when a
/// window is visible but another app has focus, Background when every window is minimized or hidden.
/// </summary>
public sealed class LifecycleMapperTests
{
    [Fact]
    public void No_state_before_a_window_was_shown()
    {
        Assert.Null(MauiLifecycleMapper.Map([]));
        Assert.Null(MauiLifecycleMapper.Map([(false, true, false), (false, false, true)]));
    }

    [Fact]
    public void An_activated_visible_window_makes_the_app_active() =>
        Assert.Equal(AppLifecycleState.Active, MauiLifecycleMapper.Map([(true, false, true), (true, true, false)]));

    [Fact]
    public void A_visible_window_without_focus_makes_the_app_inactive() =>
        Assert.Equal(AppLifecycleState.Inactive, MauiLifecycleMapper.Map([(true, false, false), (true, false, true)]));

    [Fact]
    public void Hidden_or_minimized_windows_make_the_app_background()
    {
        Assert.Equal(AppLifecycleState.Background, MauiLifecycleMapper.Map([(true, false, true), (true, false, true)]));
        Assert.Equal(AppLifecycleState.Background, MauiLifecycleMapper.Map([(true, true, true)]));   // activated but minimized
    }

    [Fact]
    public void Windows_still_being_created_do_not_count()
    {
        // A window that was never shown neither keeps a launching app out of the background nor makes it active.
        Assert.Equal(AppLifecycleState.Background, MauiLifecycleMapper.Map([(false, true, false), (true, false, true)]));
        Assert.Equal(AppLifecycleState.Inactive, MauiLifecycleMapper.Map([(false, true, false), (true, false, false)]));
    }
}

public sealed class WindowIdTests
{
    [Theory]
    [InlineData("main")]
    [InlineData("settings")]
    [InlineData("window-1")]
    [InlineData("ä ✓ 🎉")]
    public void Accepts_usable_ids(string id) => Assert.True(MauiWindowIds.IsValid(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\nb")]
    [InlineData("a\tb")]
    [InlineData("a\0b")]
    [InlineData("a\u007Fb")]
    [InlineData("a\u0085b")]
    public void Rejects_empty_ids_and_control_characters(string? id) => Assert.False(MauiWindowIds.IsValid(id));

    [Fact]
    public void Ids_are_limited_to_128_characters()
    {
        Assert.True(MauiWindowIds.IsValid(new string('a', MauiWindowIds.MaxLength)));
        Assert.False(MauiWindowIds.IsValid(new string('a', MauiWindowIds.MaxLength + 1)));
        Assert.Equal(128, MauiWindowIds.MaxLength);
    }

    [Fact]
    public void Random_ids_are_valid_and_unique()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => MauiWindowIds.CreateRandom()).ToList();

        Assert.All(ids, id =>
        {
            Assert.Matches("^window-[0-9a-f]{12}$", id);
            Assert.True(MauiWindowIds.IsValid(id));
        });
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(1, "window-1")]
    [InlineData(2, "window-2")]
    [InlineData(1000, "window-1000")]
    public void Windows_opened_directly_through_MAUI_get_sequential_ids(int sequence, string expected) =>
        Assert.Equal(expected, MauiWindowIds.CreateSequential(sequence));
}

public sealed class WindowRouteTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/settings")]
    [InlineData("/orders/42?tab=items#top")]
    [InlineData("/caf%C3%A9")]
    public void Accepts_app_relative_paths(string route) => Assert.True(MauiWindowRoutes.IsAppRoute(route));

    [Theory]
    [InlineData("")]
    [InlineData("settings")]
    [InlineData("//evil.example/path")]               // protocol-relative URL
    [InlineData("/\\evil.example")]                   // browsers treat "/\" like "//"
    [InlineData("https://evil.example/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/redirect?to=https://evil.example")] // any embedded scheme is rejected
    [InlineData("/a\nb")]
    [InlineData("/a\u0000")]
    public void Rejects_anything_that_is_not_an_app_relative_path(string route) => Assert.False(MauiWindowRoutes.IsAppRoute(route));
}
