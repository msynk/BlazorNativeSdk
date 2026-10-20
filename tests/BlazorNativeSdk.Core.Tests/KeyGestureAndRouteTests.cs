using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.Lifecycle;

namespace BlazorNativeSdk.Tests;

public sealed class KeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Shift+K", KeyModifiers.Control | KeyModifiers.Shift, NativeKey.K)]
    [InlineData("CommandOrControl+N", KeyModifiers.CommandOrControl, NativeKey.N)]
    [InlineData("cmdorctrl+alt+f12", KeyModifiers.CommandOrControl | KeyModifiers.Alt, NativeKey.F12)]
    [InlineData("Super+Space", KeyModifiers.Meta, NativeKey.Space)]
    [InlineData("Ctrl++", KeyModifiers.Control, NativeKey.Plus)]
    [InlineData("Alt+1", KeyModifiers.Alt, NativeKey.D1)]
    [InlineData("Ctrl+Esc", KeyModifiers.Control, NativeKey.Escape)]
    [InlineData("Shift+num5", KeyModifiers.Shift, NativeKey.NumPad5)]
    [InlineData("F24", KeyModifiers.None, NativeKey.F24)]
    public void Parses_electron_style_accelerators(string text, KeyModifiers modifiers, NativeKey key)
    {
        Assert.True(KeyGesture.TryParse(text, out var gesture));
        Assert.Equal(new KeyGesture(modifiers, key), gesture);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+K+L")]
    [InlineData("Hyper+K")]
    public void Rejects_invalid_gestures(string text)
    {
        Assert.False(KeyGesture.TryParse(text, out _));
        Assert.Throws<FormatException>(() => KeyGesture.Parse(text.Length == 0 ? " " : text));
    }

    [Fact]
    public void Resolves_command_or_control_per_platform()
    {
        var gesture = KeyGesture.Parse("CmdOrCtrl+S");

        Assert.Equal(KeyModifiers.Control, gesture.Resolve(NativePlatform.Windows).Modifiers);
        Assert.Equal(KeyModifiers.Meta, gesture.Resolve(NativePlatform.MacOS).Modifiers);
    }

    [Fact]
    public void ToString_round_trips()
    {
        foreach (var text in new[] { "Ctrl+Alt+Shift+Meta+F23", "CommandOrControl+1", "Shift+Plus" })
        {
            var gesture = KeyGesture.Parse(text);
            Assert.Equal(gesture, KeyGesture.Parse(gesture.ToString()));
        }
    }
}

public sealed class RouteTemplateTests
{
    [Theory]
    [InlineData("/orders/{id}", "/orders/42", "id", "42")]
    [InlineData("/orders/{id:int}", "/Orders/42?ref=mail", "ref", "mail")]
    [InlineData("/files/{**path}", "/files/a/b%20c/d.txt", "path", "a/b c/d.txt")]
    [InlineData("/search/{term?}", "/search", null, null)]
    [InlineData("/users/{name}", "/users/j%C3%BCrgen", "name", "jürgen")]
    public void Matches_routes(string template, string route, string? key, string? value)
    {
        Assert.True(RouteTemplate.Parse(template).TryMatch(route, out var values));
        if (key is not null)
        {
            Assert.Equal(value, values[key]);
        }
    }

    [Theory]
    [InlineData("/orders/{id}", "/orders")]
    [InlineData("/orders/{id}", "/orders/1/extra")]
    [InlineData("/orders", "/invoices")]
    public void Does_not_match_other_routes(string template, string route)
    {
        Assert.False(RouteTemplate.Parse(template).TryMatch(route, out _));
    }

    [Theory]
    [InlineData("/a/{*rest}/b")]
    [InlineData("/a/{id?}/b")]
    [InlineData("/a/x{id}")]
    [InlineData("/{}")]
    public void Rejects_invalid_templates(string template)
    {
        Assert.Throws<FormatException>(() => RouteTemplate.Parse(template));
    }

    [Fact]
    public void Route_values_win_over_query_values()
    {
        Assert.True(RouteTemplate.Parse("/orders/{id}").TryMatch("/orders/7?id=9&x=a+b", out var values));
        Assert.Equal("7", values["id"]);
        Assert.Equal("a b", values["x"]);
    }
}
