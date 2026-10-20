using BlazorNativeSdk.Lifecycle;

namespace BlazorNativeSdk.Desktop.Tests;

public sealed class ActivationArgumentsTests
{
    private static DeepLinkOptions DeepLinks()
    {
        var options = new NativeSdkOptions();
        options.DeepLinks.Schemes.Add("myapp");
        options.DeepLinks.Hosts.Add("contoso.com");
        return options.DeepLinks;
    }

    [Theory]
    [InlineData("myapp://orders/42")]
    [InlineData("MYAPP:settings")]
    [InlineData("https://contoso.com/orders/1")]
    [InlineData("https://CONTOSO.com/x?y=1")]
    public void Allow_listed_urls_are_deep_links(string argument)
    {
        var activation = Assert.Single(ActivationArguments.Parse([argument], DeepLinks()));

        Assert.Equal(ActivationKind.DeepLink, activation.Kind);
        Assert.Equal(new Uri(argument), activation.Uri);
        Assert.Equal([argument], activation.Arguments);
        Assert.Equal(ActivationArguments.Source, activation.Source);
    }

    [Theory]
    [InlineData("other://orders/42")]
    [InlineData("http://contoso.com/insecure")]
    [InlineData("https://evil.example/contoso.com")]
    [InlineData("https://contoso.com.evil.example/")]
    [InlineData(" myapp://leading-space")]
    [InlineData("javascript:alert(1)")]
    public void Other_urls_are_not_deep_links(string argument)
    {
        var activation = Assert.Single(ActivationArguments.Parse([argument], DeepLinks()));

        Assert.Equal(ActivationKind.SecondaryInstance, activation.Kind);
        Assert.Null(activation.Uri);
        Assert.Equal([argument], activation.Arguments);
    }

    [Fact]
    public void Existing_files_become_one_file_activation()
    {
        var directory = TestSupport.CreateTempDirectory();
        var first = Path.Combine(directory, "a.txt");
        var second = Path.Combine(directory, "b.png");
        File.WriteAllText(first, "a");
        File.WriteAllText(second, "b");

        var activation = Assert.Single(ActivationArguments.Parse([first, "--flag", second, Path.Combine(directory, "missing.txt")], DeepLinks()));

        Assert.Equal(ActivationKind.File, activation.Kind);
        Assert.Equal([first, second], activation.Files.Select(f => f.FullPath));
        Assert.Equal("image/png", activation.Files[1].ContentType);
        Assert.Equal(4, activation.Arguments.Count);
    }

    [Fact]
    public void Relative_paths_resolve_against_the_given_working_directory()
    {
        var directory = TestSupport.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "notes.md"), "#");

        var activation = Assert.Single(ActivationArguments.Parse(["notes.md"], DeepLinks(), workingDirectory: directory));

        Assert.Equal(ActivationKind.File, activation.Kind);
        Assert.Equal(Path.Combine(directory, "notes.md"), Assert.Single(activation.Files).FullPath);
    }

    [Fact]
    public void Directories_are_not_files()
    {
        var directory = TestSupport.CreateTempDirectory();

        var activation = Assert.Single(ActivationArguments.Parse([directory], DeepLinks()));

        Assert.Equal(ActivationKind.SecondaryInstance, activation.Kind);
    }

    [Fact]
    public void Deep_links_and_files_can_arrive_together()
    {
        var directory = TestSupport.CreateTempDirectory();
        var file = Path.Combine(directory, "doc.txt");
        File.WriteAllText(file, "x");

        var activations = ActivationArguments.Parse([file, "myapp://open", "https://contoso.com/second"], DeepLinks(), isColdStart: true);

        Assert.Equal([ActivationKind.DeepLink, ActivationKind.DeepLink, ActivationKind.File], activations.Select(a => a.Kind));
        Assert.All(activations, a => Assert.True(a.IsColdStart));
    }

    [Fact]
    public void Protocol_launches_ignore_arguments_after_the_url()
    {
        // "myapp://x" "C:\...\doc.txt" --flag is what a crafted URL with injected quotes produces through "exe" "%1".
        var directory = TestSupport.CreateTempDirectory();
        var file = Path.Combine(directory, "doc.txt");
        File.WriteAllText(file, "x");
        var logger = new RecordingLogger();

        var activation = Assert.Single(ActivationArguments.Parse(["myapp://x", file, "--flag", "myapp://second"], DeepLinks(), logger: logger));

        Assert.Equal(ActivationKind.DeepLink, activation.Kind);
        Assert.Equal(new Uri("myapp://x"), activation.Uri);
        Assert.Equal(["myapp://x"], activation.Arguments);
        Assert.Empty(activation.Files);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, entry.Level);
        Assert.Contains("3", entry.Message, StringComparison.Ordinal);

        // A lone URL logs nothing; an https link first is an ordinary command line, not a protocol launch.
        _ = ActivationArguments.Parse(["myapp://y"], DeepLinks(), logger: logger);
        Assert.Single(logger.Entries);
        var https = ActivationArguments.Parse(["https://contoso.com/a", file], DeepLinks());
        Assert.Equal([ActivationKind.DeepLink, ActivationKind.File], https.Select(a => a.Kind));
    }

    [Theory]
    [InlineData(@"\\bns-unreachable.invalid\share\doc.txt")]
    [InlineData(@"//bns-unreachable.invalid/share/doc.txt")]
    [InlineData(@"\\?\UNC\bns-unreachable.invalid\share\doc.txt")]
    [InlineData(@"\\.\pipe\bns-test")]
    [InlineData(@"\??\UNC\bns-unreachable.invalid\share\doc.txt")]
    public void Unc_and_device_paths_are_plain_arguments_on_windows(string argument)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("UNC paths only reach the network on Windows.");
        }

        var activation = Assert.Single(ActivationArguments.Parse([argument], DeepLinks()));

        Assert.Equal(ActivationKind.SecondaryInstance, activation.Kind);
        Assert.Empty(activation.Files);
        Assert.Equal([argument], activation.Arguments);
    }

    [Fact]
    public void Relative_paths_are_not_resolved_onto_a_unc_working_directory()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("UNC paths only reach the network on Windows.");
        }

        var activation = Assert.Single(ActivationArguments.Parse(["doc.txt"], DeepLinks(), workingDirectory: @"\\bns-unreachable.invalid\share"));

        Assert.Equal(ActivationKind.SecondaryInstance, activation.Kind);
        Assert.Empty(activation.Files);
    }

    [Fact]
    public void Plain_arguments_produce_a_single_fallback_activation()
    {
        var launch = Assert.Single(ActivationArguments.Parse(["--safe-mode", "-v"], DeepLinks(), ActivationKind.Launch, isColdStart: true));
        var empty = Assert.Single(ActivationArguments.Parse([], DeepLinks()));

        Assert.Equal(ActivationKind.Launch, launch.Kind);
        Assert.True(launch.IsColdStart);
        Assert.Equal(["--safe-mode", "-v"], launch.Arguments);
        Assert.Equal(ActivationKind.SecondaryInstance, empty.Kind);
        Assert.Empty(empty.Arguments);
    }
}
