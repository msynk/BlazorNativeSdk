using BlazorNativeSdk.Desktop.Internal;
using BlazorNativeSdk.Desktop.Ipc;
using BlazorNativeSdk.Desktop.Shell;

namespace BlazorNativeSdk.Desktop.Tests;

/// <summary>
/// Ownership, mode and symbolic-link checks for the directories and lock files the desktop adapter creates in shared
/// locations on Linux and macOS. Skipped on Windows.
/// </summary>
public sealed class UnixHardeningTests
{
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    [Fact]
    public void Socket_directories_that_are_shared_or_links_are_skipped_for_the_next_root()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Skip("Unix domain socket directories are only used on Linux and macOS.");
            return;
        }

        // Short roots under /tmp keep the socket paths within sun_path on macOS too.
        var roots = Enumerable.Range(0, 3).Select(_ => Path.Combine("/tmp", "bns-t-" + Guid.NewGuid().ToString("N")[..8])).ToArray();
        try
        {
            foreach (var root in roots)
            {
                Directory.CreateDirectory(root);
            }

            // roots[0]: a world-writable directory (as another user would leave it); roots[1]: a symbolic link to it.
            var shared = Path.Combine(roots[0], "bns-u");
            Directory.CreateDirectory(shared);
            File.SetUnixFileMode(shared, Private | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                         UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            Directory.CreateSymbolicLink(Path.Combine(roots[1], "bns-u"), shared);

            Assert.False(IpcEndpoints.IsUsableSocketDirectory(shared));
            Assert.False(IpcEndpoints.IsUsableSocketDirectory(Path.Combine(roots[1], "bns-u")));
            Assert.True(IpcEndpoints.IsUsableSocketDirectory(Path.Combine(roots[2], "bns-u")));
            Assert.Equal(Path.Combine(roots[2], "bns-u", "s.sock"), IpcEndpoints.SelectUnixSocketPath(roots, "bns-u", "s.sock"));

            // A private directory of the current user is used in place.
            File.SetUnixFileMode(shared, Private);
            Assert.Equal(Path.Combine(shared, "s.sock"), IpcEndpoints.SelectUnixSocketPath(roots, "bns-u", "s.sock"));
        }
        finally
        {
            foreach (var root in roots)
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }
    }

    [Fact]
    public void Lock_files_that_are_symbolic_links_are_refused()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Skip("Unix-only check (Windows locks are mandatory sharing modes on the opened handle).");
            return;
        }

        var directory = TestSupport.CreateTempDirectory();
        var target = Path.Combine(directory, "elsewhere");
        var link = Path.Combine(directory, "channel.lock");
        File.CreateSymbolicLink(link, target);

        Assert.Equal(FileLockOutcome.Failed, ExclusiveFileLock.TryAcquire(link, out var refused, out var failure));
        Assert.Null(refused);
        Assert.Contains("symbolic link", failure!.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(target));

        var regular = Path.Combine(directory, "regular.lock");
        Assert.Equal(FileLockOutcome.Acquired, ExclusiveFileLock.TryAcquire(regular, out var acquired, out _));
        acquired!.Dispose();
    }

    [Fact]
    public void Topdir_trash_must_be_a_private_directory_of_the_user()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The FreeDesktop.org trash is only used on Linux.");
            return;
        }

        var root = TestSupport.CreateTempDirectory();
        var fresh = Path.Combine(root, ".Trash-fresh");
        Assert.True(FreeDesktopTrash.TryPrepareTopdirTrash(fresh));
        Assert.Equal(Private, File.GetUnixFileMode(fresh));
        Assert.True(FreeDesktopTrash.TryPrepareTopdirTrash(fresh));

        var shared = Path.Combine(root, ".Trash-shared");
        Directory.CreateDirectory(shared);
        File.SetUnixFileMode(shared, Private | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        Assert.False(FreeDesktopTrash.TryPrepareTopdirTrash(shared));

        var link = Path.Combine(root, ".Trash-link");
        Directory.CreateSymbolicLink(link, fresh);
        Assert.False(FreeDesktopTrash.TryPrepareTopdirTrash(link));
    }
}
