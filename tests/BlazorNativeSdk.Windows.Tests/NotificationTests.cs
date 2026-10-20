using System.Security.Cryptography;
using System.Text;
using System.Xml;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk.WindowsPlatform.Tests;

public sealed class ToastXmlBuilderTests
{
    private static readonly ToastActivationOptions s_foreground = new(null, null, SupportsUrgentScenario: true);

    [Fact]
    public void Escapes_all_content()
    {
        var notification = new LocalNotification
        {
            Id = "id<&>\"'",
            Title = "<b>Tom & \"Jerry\"</b>",
            Body = "1 < 2 && 3 > 2 'quoted' ]]>",
            Subtitle = "attribution & co",
            Route = "/orders/42?x=1&y=<2>",
            Data = new Dictionary<string, string> { ["k&=y"] = "v<&>?#" },
        };

        var document = Parse(ToastXmlBuilder.Build(notification, s_foreground));
        var texts = document.SelectNodes("/toast/visual/binding/text")!;
        Assert.Equal(3, texts.Count);
        Assert.Equal(notification.Title, texts[0]!.InnerText);
        Assert.Equal(notification.Body, texts[1]!.InnerText);
        Assert.Equal(notification.Subtitle, texts[2]!.InnerText);
        Assert.Equal("attribution", texts[2]!.Attributes!["placement"]!.Value);

        var toast = document.DocumentElement!;
        Assert.Equal("foreground", toast.GetAttribute("activationType"));
        Assert.True(ToastLaunchArguments.TryParseArguments(toast.GetAttribute("launch"), out var payload));
        Assert.Equal(notification.Id, payload.NotificationId);
        Assert.Null(payload.ActionId);
        Assert.Equal(notification.Route, payload.Route);
        Assert.Equal("v<&>?#", payload.Data["k&=y"]);
    }

    [Fact]
    public void Removes_characters_xml_cannot_carry()
    {
        var notification = new LocalNotification { Id = "x", Title = "bell\u0007 nul\u0000 ok \U0001F600", Body = "lone \uD800 surrogate" };
        var document = Parse(ToastXmlBuilder.Build(notification, s_foreground));
        var texts = document.SelectNodes("/toast/visual/binding/text")!;
        Assert.Equal("bell nul ok \U0001F600", texts[0]!.InnerText);
        Assert.Equal("lone  surrogate", texts[1]!.InnerText);
    }

    [Fact]
    public void Writes_actions_with_text_input_and_protocol_activation()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var notification = new LocalNotification
        {
            Id = "chat-7",
            Title = "New message",
            Route = "/chat/7",
            Actions =
            [
                new NotificationAction("reply", "Reply") { AcceptsText = true, TextPlaceholder = "Type a <reply>" },
                new NotificationAction("archive", "Archive & close"),
            ],
        };

        var document = Parse(ToastXmlBuilder.Build(notification, new ToastActivationOptions("bnstest", key, SupportsUrgentScenario: false)));
        var toast = document.DocumentElement!;
        Assert.Equal("protocol", toast.GetAttribute("activationType"));
        Assert.True(ToastLaunchArguments.TryParseProtocolUri(new Uri(toast.GetAttribute("launch")), "bnstest", key, out var launch));
        Assert.Equal("chat-7", launch.NotificationId);
        Assert.Equal("/chat/7", launch.Route);

        var input = (XmlElement)document.SelectSingleNode("/toast/actions/input")!;
        Assert.Equal("text", input.GetAttribute("type"));
        Assert.Equal("Type a <reply>", input.GetAttribute("placeHolderContent"));

        var actions = document.SelectNodes("/toast/actions/action")!.Cast<XmlElement>().ToArray();
        Assert.Equal(2, actions.Length);

        // Typed replies need in-process activation; the other button uses the protocol.
        Assert.Equal("foreground", actions[0].GetAttribute("activationType"));
        Assert.Equal(input.GetAttribute("id"), actions[0].GetAttribute("hint-inputId"));
        Assert.True(ToastLaunchArguments.TryParseArguments(actions[0].GetAttribute("arguments"), out var reply));
        Assert.Equal("reply", reply.ActionId);

        Assert.Equal("Archive & close", actions[1].GetAttribute("content"));
        Assert.Equal("protocol", actions[1].GetAttribute("activationType"));
        Assert.True(ToastLaunchArguments.TryParseProtocolUri(new Uri(actions[1].GetAttribute("arguments")), "bnstest", key, out var archive));
        Assert.Equal("archive", archive.ActionId);
        Assert.Equal("/chat/7", archive.Route);
    }

    [Theory]
    [InlineData(NotificationImportance.High, true, "urgent", false)]
    [InlineData(NotificationImportance.High, false, "", false)]
    [InlineData(NotificationImportance.Default, true, "", false)]
    [InlineData(NotificationImportance.Low, true, "", true)]
    public void Maps_importance(NotificationImportance importance, bool supportsUrgent, string scenario, bool silent)
    {
        var notification = new LocalNotification { Id = "i", Title = "t", Importance = importance };
        var document = Parse(ToastXmlBuilder.Build(notification, new ToastActivationOptions(null, null, supportsUrgent)));
        Assert.Equal(scenario, document.DocumentElement!.GetAttribute("scenario"));
        Assert.Equal(silent, document.SelectSingleNode("/toast/audio[@silent='true']") is not null);
    }

    [Fact]
    public void Limits_buttons_to_five()
    {
        var notification = new LocalNotification
        {
            Id = "i",
            Title = "t",
            Actions = [.. Enumerable.Range(0, 8).Select(i => new NotificationAction("a" + i, "A" + i))],
        };

        var document = Parse(ToastXmlBuilder.Build(notification, s_foreground));
        Assert.Equal(ToastXmlBuilder.MaxActions, document.SelectNodes("/toast/actions/action")!.Count);
    }

    [Fact]
    public void Long_ids_are_hashed_into_valid_tags()
    {
        Assert.Equal("short", WindowsNotificationService.TagFor("short"));
        var tag = WindowsNotificationService.TagFor(new string('x', 200));
        Assert.Equal(64, tag.Length);
        Assert.Equal(tag, WindowsNotificationService.TagFor(new string('x', 200)));
    }

    [Theory]
    [InlineData("com.contoso.notes", "com.contoso.notes")]
    [InlineData(" My App\tName ", "My.App.Name")]
    [InlineData("a\\b", "a.b")]
    public void Normalizes_app_user_model_ids(string input, string expected) =>
        Assert.Equal(expected, AppUserModelId.Normalize(input));

    [Fact]
    public void Truncates_app_user_model_ids_to_128_characters() =>
        Assert.Equal(128, AppUserModelId.Normalize(new string('a', 300)).Length);

    private static XmlDocument Parse(string xml)
    {
        var document = new XmlDocument();
        document.LoadXml(xml);
        return document;
    }
}

public sealed class ToastLaunchArgumentsTests
{
    private static readonly ToastActivationPayload s_payload = new(
        "order:42/é",
        "accept & go",
        "/orders/42?tab=items&sort=desc#top",
        new Dictionary<string, string> { ["user id"] = "7", ["q"] = "a=b&c=%20d", ["emoji"] = "\U0001F680" });

    [Fact]
    public void Foreground_arguments_round_trip()
    {
        var arguments = ToastLaunchArguments.ToArguments(s_payload);
        Assert.StartsWith(ToastLaunchArguments.ArgumentsPrefix, arguments, StringComparison.Ordinal);
        Assert.True(ToastLaunchArguments.TryParseArguments(arguments, out var decoded));
        AssertEqual(s_payload, decoded);
    }

    [Fact]
    public void Protocol_uris_round_trip_and_survive_normalization()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var uri = ToastLaunchArguments.ToProtocolUri("myapp", s_payload, key);
        Assert.Equal("myapp", uri.Scheme);
        Assert.Equal(ToastLaunchArguments.ProtocolHost, uri.Host);

        Assert.True(ToastLaunchArguments.TryParseProtocolUri(uri, "MyApp", key, out var decoded));
        AssertEqual(s_payload, decoded);

        // Launchers and shells may change the case of percent-escapes.
        var lowered = new Uri(uri.AbsoluteUri.Replace("%2F", "%2f", StringComparison.Ordinal));
        Assert.True(ToastLaunchArguments.TryParseProtocolUri(lowered, "myapp", key, out var normalized));
        AssertEqual(s_payload, normalized);
    }

    [Fact]
    public void Protocol_uris_reject_tampering_wrong_keys_and_wrong_schemes()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var uri = ToastLaunchArguments.ToProtocolUri("myapp", s_payload, key).AbsoluteUri;

        Assert.False(ToastLaunchArguments.TryParseProtocolUri(new Uri(uri.Replace("orders%2F42", "admin", StringComparison.Ordinal)), "myapp", key, out _));
        Assert.False(ToastLaunchArguments.TryParseProtocolUri(new Uri(uri), "myapp", RandomNumberGenerator.GetBytes(32), out _));
        Assert.False(ToastLaunchArguments.TryParseProtocolUri(new Uri(uri), "other", key, out _));
        Assert.False(ToastLaunchArguments.TryParseProtocolUri(new Uri(uri[..uri.IndexOf("&sig=", StringComparison.Ordinal)]), "myapp", key, out _));
        Assert.False(ToastLaunchArguments.TryParseProtocolUri(new Uri("myapp://bns-toast/?nid=x&route=%2Fadmin&sig=AAAA"), "myapp", key, out _));
        Assert.False(ToastLaunchArguments.TryParseProtocolUri(new Uri("myapp://elsewhere/?nid=x"), "myapp", key, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("random launch string")]
    [InlineData("bns-toast?aid=only-action")]
    [InlineData("bns-toast?broken")]
    public void Rejects_foreign_arguments(string? arguments) =>
        Assert.False(ToastLaunchArguments.TryParseArguments(arguments, out _));

    private static void AssertEqual(ToastActivationPayload expected, ToastActivationPayload actual)
    {
        Assert.Equal(expected.NotificationId, actual.NotificationId);
        Assert.Equal(expected.ActionId, actual.ActionId);
        Assert.Equal(expected.Route, actual.Route);
        Assert.Equal(expected.Data.OrderBy(p => p.Key), actual.Data.OrderBy(p => p.Key));
    }
}

public sealed class ToastActivationTranslatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bns-windows-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Converts_signed_toast_uris_into_notification_activations()
    {
        var keys = new ToastActivationKeyStore(_directory, "com.example.tests");
        var translator = new WindowsToastActivationTranslator(
            Options.Create(new WindowsNativeOptions { NotificationActivationScheme = "bnstest" }),
            keys,
            NullLogger<WindowsToastActivationTranslator>.Instance);

        var payload = new ToastActivationPayload("n1", "open", "/inbox", new Dictionary<string, string> { ["k"] = "v" });
        var uri = ToastLaunchArguments.ToProtocolUri("bnstest", payload, keys.Key);
        var converted = translator.Translate(new AppActivation { Kind = ActivationKind.DeepLink, Uri = uri, IsColdStart = true, Arguments = [uri.AbsoluteUri] });

        Assert.NotNull(converted);
        Assert.Equal(ActivationKind.Notification, converted.Kind);
        Assert.Equal("n1", converted.NotificationId);
        Assert.Equal("open", converted.ActionId);
        Assert.Equal("/inbox", converted.Route);
        Assert.Equal("v", converted.Data["k"]);
        Assert.True(converted.IsColdStart);
        Assert.Null(converted.Uri);

        // A second key store for the same app reads the persisted key (a later process signs and verifies identically).
        var reloaded = new ToastActivationKeyStore(_directory, "com.example.tests");
        Assert.True(reloaded.Key.SequenceEqual(keys.Key));

        // Forged toast links are dropped; unrelated links pass through untouched.
        var forged = new AppActivation { Kind = ActivationKind.DeepLink, Uri = new Uri("bnstest://bns-toast/?nid=n1&route=%2Fadmin&sig=AAAA") };
        Assert.Null(translator.Translate(forged));
        var other = new AppActivation { Kind = ActivationKind.DeepLink, Uri = new Uri("bnstest://orders/42") };
        Assert.Same(other, translator.Translate(other));
    }

    [Fact]
    public async Task A_key_file_that_is_briefly_locked_is_retried_not_replaced()
    {
        var original = new ToastActivationKeyStore(_directory, "com.example.tests").Key.ToArray();
        var path = Path.Combine(_directory, "toast-activation.key");
        var stored = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        var store = new ToastActivationKeyStore(_directory, "com.example.tests");
        byte[] loaded;
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var reading = Task.Run(() => store.Key.ToArray(), TestContext.Current.CancellationToken);
            await Task.Delay(120, TestContext.Current.CancellationToken);
            Assert.False(reading.IsCompleted);
            exclusive.Dispose();
            loaded = await reading.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }

        Assert.Equal(original, loaded);
        Assert.Equal(stored, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_key_file_that_stays_locked_fails_without_being_overwritten()
    {
        _ = new ToastActivationKeyStore(_directory, "com.example.tests").Key.ToArray();
        var path = Path.Combine(_directory, "toast-activation.key");
        var stored = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        var store = new ToastActivationKeyStore(_directory, "com.example.tests");
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() => store.Key.ToArray());
        }

        Assert.Equal(stored, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));

        // Not cached: once the file is readable again, the same store loads the original key.
        Assert.Equal(new ToastActivationKeyStore(_directory, "com.example.tests").Key.ToArray(), store.Key.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Definitively_invalid_key_files_are_replaced(int corruption)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "toast-activation.key");

        // Garbage that DPAPI cannot decrypt, or a valid DPAPI blob of the wrong length.
        var entropy = SHA256.HashData(Encoding.UTF8.GetBytes("BlazorNativeSdk.ToastActivation/v1\0" + "com.example.tests"));
        var invalid = corruption == 0
            ? RandomNumberGenerator.GetBytes(200)
            : ProtectedData.Protect(new byte[16], entropy, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(path, invalid, TestContext.Current.CancellationToken);

        var key = new ToastActivationKeyStore(_directory, "com.example.tests").Key.ToArray();

        Assert.Equal(32, key.Length);
        Assert.NotEqual(invalid, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(key, new ToastActivationKeyStore(_directory, "com.example.tests").Key.ToArray());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
