using BlazorNativeSdk.Devices;

namespace BlazorNativeSdk.ApplePlatform.Tests;

public sealed class ActivationMappingTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("?")]
    [InlineData("&&")]
    public void Empty_queries_have_no_values(string? query) =>
        Assert.Empty(AppleActivationMapping.ParseQuery(query));

    [Fact]
    public void Parses_and_decodes_query_values()
    {
        var values = AppleActivationMapping.ParseQuery("?id=42&q=two%20words&plus=a+b&flag&empty=&k%5B0%5D=x");

        Assert.Equal("42", values["id"]);
        Assert.Equal("two words", values["q"]);
        Assert.Equal("a b", values["plus"]);
        Assert.Equal(string.Empty, values["flag"]);
        Assert.Equal(string.Empty, values["empty"]);
        Assert.Equal("x", values["k[0]"]);
    }

    [Fact]
    public void The_leading_question_mark_is_optional() =>
        Assert.Equal("1", AppleActivationMapping.ParseQuery("a=1")["a"]);

    [Fact]
    public void The_first_value_of_a_repeated_key_wins() =>
        Assert.Equal("first", AppleActivationMapping.ParseQuery("k=first&k=second")["k"]);

    [Fact]
    public void Keys_are_case_sensitive()
    {
        var values = AppleActivationMapping.ParseQuery("Key=1&key=2");

        Assert.Equal(("1", "2"), (values["Key"], values["key"]));
    }

    [Fact]
    public void Values_without_a_key_are_ignored() =>
        Assert.Equal(["b"], AppleActivationMapping.ParseQuery("=a&b=2").Keys);

    [Fact]
    public void Malformed_escapes_are_kept_verbatim() =>
        Assert.Equal("%zz%", AppleActivationMapping.ParseQuery("v=%zz%")["v"]);

    [Fact]
    public void Quick_actions_carry_their_type_and_user_info()
    {
        var (data, route) = AppleActivationMapping.ForShortcut(
            "com.contoso.new-order",
            new Dictionary<string, string> { ["route"] = "/orders/new", ["source"] = "home" });

        Assert.Equal("/orders/new", route);
        Assert.Equal("com.contoso.new-order", data[AppleActivationMapping.ShortcutTypeKey]);
        Assert.Equal("home", data["source"]);
        Assert.Equal("/orders/new", data["route"]);
    }

    [Theory]
    [InlineData("orders/new")]
    [InlineData("https://evil.example/")]
    [InlineData("")]
    public void Only_app_relative_quick_action_routes_are_used(string candidate) =>
        Assert.Null(AppleActivationMapping.ForShortcut("t", new Dictionary<string, string> { ["route"] = candidate }).Route);

    [Fact]
    public void The_quick_action_type_wins_over_a_user_info_entry_with_the_same_key() =>
        Assert.Equal("real", AppleActivationMapping.ForShortcut("real", new Dictionary<string, string> { ["type"] = "spoofed" }).Data["type"]);
}

public sealed class FileTypeMappingTests
{
    private static readonly Dictionary<string, string> s_mimeTypes = new(StringComparer.Ordinal)
    {
        ["application/pdf"] = "com.adobe.pdf",
        ["text/plain"] = "public.plain-text",
        ["application/json"] = "public.json",
    };

    private static readonly Dictionary<string, string> s_extensions = new(StringComparer.Ordinal)
    {
        ["pdf"] = "com.adobe.pdf",
        ["txt"] = "public.plain-text",
        ["json"] = "public.json",
        ["png"] = "public.png",
    };

    [Theory]
    [InlineData("*/*", "public.item")]
    [InlineData("*", "public.item")]
    [InlineData("image/*", "public.image")]
    [InlineData(" IMAGE/* ", "public.image")]
    [InlineData("video/*", "public.movie")]
    [InlineData("audio/*", "public.audio")]
    [InlineData("text/*", "public.text")]
    [InlineData("application/*", "public.data")]
    [InlineData("image/png", null)]
    [InlineData("font/*", null)]
    public void Maps_MIME_wildcards(string mimeType, string? expected) =>
        Assert.Equal(expected, AppleFileTypeMapping.FromMimeWildcard(mimeType));

    [Fact]
    public void No_filters_mean_any_file() => Assert.Empty(Map([]));

    [Fact]
    public void Identifiers_are_distinct_and_keep_their_order()
    {
        var identifiers = Map([FileTypeFilter.Pdf, FileTypeFilter.Text, FileTypeFilter.Pdf]);

        Assert.Equal(["com.adobe.pdf", "public.plain-text"], identifiers);
    }

    [Fact]
    public void Wildcards_map_to_their_parent_type_and_extensions_still_resolve()
    {
        var identifiers = Map([FileTypeFilter.FromExtensions("Pictures", ".png", "heic")]);

        Assert.Equal(["public.png", "dyn.heic"], identifiers);
        Assert.Equal("public.image", Map([FileTypeFilter.Images])[0]);
    }

    [Fact]
    public void Unknown_wildcards_are_skipped_and_never_resolved()
    {
        var resolved = new List<string>();

        var identifiers = AppleFileTypeMapping.ToTypeIdentifiers(
            [new FileTypeFilter("Fonts", ["font/*"], [])],
            mime =>
            {
                resolved.Add(mime);
                return "should-not-be-used";
            },
            _ => null);

        Assert.Empty(identifiers);
        Assert.Empty(resolved);
    }

    [Fact]
    public void Extension_dots_are_trimmed_and_empty_extensions_skipped()
    {
        var resolved = new List<string>();

        AppleFileTypeMapping.ToTypeIdentifiers(
            [new FileTypeFilter("Mixed", [], [".json", "..txt", "pdf", ".", ""])],
            _ => null,
            extension =>
            {
                resolved.Add(extension);
                return null;
            });

        Assert.Equal(["json", "txt", "pdf"], resolved);
    }

    [Fact]
    public void Unresolvable_types_are_left_out() =>
        Assert.Equal(["public.json"], Map([new FileTypeFilter("Data", ["application/x-unknown", "application/json"], [])]));

    // Simulates UTType: registered types resolve, other extensions get a dynamic identifier, unknown MIME types none.
    private static string[] Map(IReadOnlyList<FileTypeFilter> filters) =>
        AppleFileTypeMapping.ToTypeIdentifiers(
            filters,
            mime => s_mimeTypes.GetValueOrDefault(mime),
            extension => s_extensions.GetValueOrDefault(extension) ?? "dyn." + extension);
}
