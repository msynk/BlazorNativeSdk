using BlazorNativeSdk.Desktop.Internal;
using BlazorNativeSdk.Desktop.Shell;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk.Desktop.Tests;

public sealed class DesktopFileOperationsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DesktopFileOperations Create(Action<DesktopNativeOptions>? configure = null)
    {
        var options = new DesktopNativeOptions();
        configure?.Invoke(options);
        return new DesktopFileOperations(Options.Create(options), NullLogger<DesktopFileOperations>.Instance);
    }

    [Theory]
    [InlineData("ftp://example.com/file")]
    [InlineData("myapp://do-something")]
    [InlineData("ms-settings:privacy")]
    [InlineData("search-ms:query=x")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    public async Task OpenUri_rejects_schemes_that_are_not_allowed_without_launching(string uri)
    {
        var operations = Create();

        var result = await operations.OpenUriAsync(new Uri(uri), Ct);

        Assert.Equal(NativeResultStatus.Failed, result.Status);
        Assert.Equal(NativeErrorCodes.InvalidArgument, result.Error!.Code);
    }

    [Fact]
    public void Configured_schemes_are_allowed_but_dangerous_schemes_never_are()
    {
        var allowed = new HashSet<string>(["vscode", "file", "javascript"], StringComparer.OrdinalIgnoreCase);

        Assert.True(ShellPolicy.TryValidateUri(NativeCapabilities.DesktopFiles, new Uri("vscode://file/x"), allowed, out var text, out _));
        Assert.Equal("vscode://file/x", text);
        Assert.True(ShellPolicy.TryValidateUri(NativeCapabilities.DesktopFiles, new Uri("https://example.com/a b"), allowed, out var escaped, out _));
        Assert.Equal("https://example.com/a%20b", escaped);
        Assert.True(ShellPolicy.TryValidateUri(NativeCapabilities.DesktopFiles, new Uri("mailto:someone@example.com"), [], out _, out _));
        Assert.False(ShellPolicy.TryValidateUri(NativeCapabilities.DesktopFiles, new Uri("file:///etc/passwd"), allowed, out _, out _));
        Assert.False(ShellPolicy.TryValidateUri(NativeCapabilities.DesktopFiles, new Uri("javascript:alert(1)"), allowed, out _, out _));
        Assert.False(ShellPolicy.TryValidateUri(NativeCapabilities.DesktopFiles, new Uri("/relative", UriKind.Relative), allowed, out _, out _));
    }

    [Fact]
    public async Task Operations_on_missing_paths_report_not_found()
    {
        var operations = Create();
        var missing = Path.Combine(Path.GetTempPath(), "bns-missing-" + Guid.NewGuid().ToString("N"), "nothing.txt");

        Assert.Equal(NativeErrorCodes.NotFound, (await operations.RevealInFileManagerAsync(missing, Ct)).Error!.Code);
        Assert.Equal(NativeErrorCodes.NotFound, (await operations.OpenPathAsync(missing, Ct)).Error!.Code);
        Assert.Equal(NativeErrorCodes.NotFound, (await operations.MoveToTrashAsync(missing, Ct)).Error!.Code);
        Assert.Equal(NativeErrorCodes.InvalidArgument, (await operations.OpenPathAsync("", Ct)).Error!.Code);
    }

    [Fact]
    public async Task OpenPath_refuses_executables_unless_allowed()
    {
        var directory = TestSupport.CreateTempDirectory();
        var script = Path.Combine(directory, OperatingSystem.IsWindows() ? "payload.cmd" : "payload.sh");
        await File.WriteAllTextAsync(script, "echo pwned", Ct);

        var result = await Create().OpenPathAsync(script, Ct);

        Assert.Equal(NativeErrorCodes.InvalidArgument, result.Error!.Code);
        Assert.True(ShellPolicy.IsExecutableLike(Path.Combine(directory, "Setup.EXE"), isDirectory: false) || !OperatingSystem.IsWindows());
        Assert.False(ShellPolicy.IsExecutableLike(directory, isDirectory: true));
    }

    [Theory]
    [InlineData(@"\\bns-unreachable.invalid\share\doc.txt")]
    [InlineData(@"//bns-unreachable.invalid/share/doc.txt")]
    [InlineData(@"\/bns-unreachable.invalid/share")]
    [InlineData(@"\\?\UNC\bns-unreachable.invalid\share\doc.txt")]
    [InlineData(@"\\?\C:\Windows\notepad.exe")]
    [InlineData(@"\\.\C:\Windows")]
    [InlineData(@"\??\UNC\bns-unreachable.invalid\share")]
    [InlineData("NUL")]
    [InlineData("com1")]
    public async Task Unc_and_device_paths_are_rejected_before_any_probe_on_windows(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("UNC and device paths are Windows concepts.");
        }

        var operations = Create();

        foreach (var result in new[]
        {
            await operations.OpenPathAsync(path, Ct),
            await operations.RevealInFileManagerAsync(path, Ct),
            await operations.MoveToTrashAsync(path, Ct),
        })
        {
            Assert.Equal(NativeResultStatus.Failed, result.Status);
            Assert.Equal(NativeErrorCodes.InvalidArgument, result.Error!.Code);
        }
    }

    [Fact]
    public async Task Relative_paths_cannot_reach_a_unc_working_directory()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("UNC paths are a Windows concept.");
        }

        Assert.True(PathSafety.IsRemoteOrDevicePath(Path.GetFullPath("doc.txt", @"\\bns-unreachable.invalid\share")));
        var result = await Create().OpenPathAsync(@"\\bns-unreachable.invalid\share\..\..\other\doc.txt", Ct);
        Assert.Equal(NativeErrorCodes.InvalidArgument, result.Error!.Code);
    }

    [Theory]
    [InlineData(@"\\server\share", true)]
    [InlineData(@"//server/share", true)]
    [InlineData(@"/\server", true)]
    [InlineData(@"  \\server\share", true)]
    [InlineData(@"\\?\UNC\server\share", true)]
    [InlineData(@"\\.\pipe\x", true)]
    [InlineData(@"\??\C:\x", true)]
    [InlineData(@"C:\Users\x\doc.txt", false)]
    [InlineData(@"\Users\x", false)]
    [InlineData(@"relative\doc.txt", false)]
    [InlineData(@"?\x", false)]
    [InlineData("", false)]
    public void Windows_remote_and_device_path_detection(string path, bool expected) =>
        Assert.Equal(expected, PathSafety.IsRemoteOrDeviceWindowsPath(path));

    [Theory]
    [InlineData(@"C:\x\run.exe. .", @"C:\x\run.exe")]
    [InlineData(@"C:\x\run.cmd ", @"C:\x\run.cmd")]
    [InlineData(@"C:\x\doc.txt", @"C:\x\doc.txt")]
    [InlineData(@"C:\x\..", @"C:\x\..")]
    [InlineData(@"C:\x\", @"C:\x\")]
    [InlineData("...", "...")]
    public void Trailing_dots_and_spaces_are_trimmed_like_win32(string path, string expected) =>
        Assert.Equal(expected, PathSafety.TrimWin32TrailingDotsAndSpaces(path));

    [Theory]
    [InlineData("setup.exe", true)]
    [InlineData("setup.exe.", true)]
    [InlineData("setup.exe. . ", true)]
    [InlineData("SCRIPT.CMD  ", true)]
    [InlineData("run.bat...", true)]
    [InlineData("notes.txt", false)]
    [InlineData("notes.txt.", false)]
    [InlineData("archive", false)]
    [InlineData("archive.", false)]
    public void Windows_executable_names_ignore_trailing_dots_and_spaces(string name, bool expected) =>
        Assert.Equal(expected, ShellPolicy.IsWindowsExecutableName(name));

    [Fact]
    public async Task OpenPath_refuses_executables_named_with_trailing_dots_or_spaces()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Win32 path normalization.");
        }

        var directory = TestSupport.CreateTempDirectory();
        var script = Path.Combine(directory, "payload.cmd");
        await File.WriteAllTextAsync(script, "echo pwned", Ct);
        var operations = Create();

        foreach (var variant in new[] { script + ".", script + ". .", script + "  " })
        {
            var result = await operations.OpenPathAsync(variant, Ct);
            Assert.Equal(NativeErrorCodes.InvalidArgument, result.Error!.Code);
        }

        // Alternate data stream syntax opens the main stream of an executable.
        Assert.Equal(NativeErrorCodes.InvalidArgument, (await operations.OpenPathAsync(script + "::$DATA", Ct)).Error!.Code);
    }

    [Fact]
    public async Task Volume_roots_cannot_be_trashed()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;

        var result = await Create().MoveToTrashAsync(root, Ct);

        Assert.Equal(NativeErrorCodes.InvalidArgument, result.Error!.Code);
    }

    [Fact]
    public async Task MoveToTrash_recycles_a_file_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Exercises SHFileOperationW.");
        }

        var directory = TestSupport.CreateTempDirectory();
        var file = Path.Combine(directory, "bns-desktop-test-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(file, "trash me", Ct);

        var result = await Create().MoveToTrashAsync(file, Ct);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task FreeDesktop_trash_moves_the_item_and_writes_trash_info()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The FreeDesktop.org trash is only used on Linux.");
            return;
        }

        var dataHome = TestSupport.CreateTempDirectory();
        var directory = TestSupport.CreateTempDirectory();
        var file = Path.Combine(directory, "my file.txt");
        await File.WriteAllTextAsync(file, "x", Ct);
        await File.WriteAllTextAsync(Path.Combine(directory, "second"), "y", Ct);

        Assert.True(FreeDesktopTrash.MoveToTrash(file, isDirectory: false, dataHome).IsSuccess);
        await File.WriteAllTextAsync(file, "again", Ct);
        Assert.True(FreeDesktopTrash.MoveToTrash(file, isDirectory: false, dataHome).IsSuccess);

        Assert.False(File.Exists(file));
        Assert.True(File.Exists(Path.Combine(dataHome, "Trash", "files", "my file.txt")));
        Assert.True(File.Exists(Path.Combine(dataHome, "Trash", "files", "my file.2.txt")));
        var info = await File.ReadAllTextAsync(Path.Combine(dataHome, "Trash", "info", "my file.txt.trashinfo"), Ct);
        Assert.Contains("Path=" + FreeDesktopTrash.EncodePath(file), info, StringComparison.Ordinal);
    }

    [Fact]
    public void Trash_info_follows_the_specification_format()
    {
        var info = FreeDesktopTrash.BuildTrashInfo("/home/ada/My Docs/r\u00e9sum\u00e9 #1.txt", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local));

        Assert.Equal(
            "[Trash Info]\nPath=/home/ada/My%20Docs/r%C3%A9sum%C3%A9%20%231.txt\nDeletionDate=2026-01-02T03:04:05\n",
            info);
    }

    [Fact]
    public void File_uris_are_fully_percent_encoded()
    {
        Assert.Equal("file:///tmp/it%27s%20a%20%5Btest%5D", ShellPolicy.ToFileUri("/tmp/it's a [test]"));
    }

    [Fact]
    public async Task Status_reports_the_supported_operations()
    {
        var status = await Create().GetStatusAsync(Ct);

        Assert.True(status.IsAvailable);
        Assert.True(status.Supports("trash"));
    }
}
