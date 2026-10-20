using BlazorNativeSdk.Desktop.Interop;
using BlazorNativeSdk.Desktop.Internal;
using BlazorNativeSdk.Desktop.Shell;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk.Desktop;

/// <summary>Shell file operations for Windows, macOS and Linux.</summary>
/// <remarks>
/// <list type="table">
/// <listheader><term>Operation</term><description>Windows / macOS / Linux</description></listheader>
/// <item><term>Reveal</term><description><c>SHOpenFolderAndSelectItems</c> / <c>open -R</c> /
/// <c>org.freedesktop.FileManager1.ShowItems</c> via <c>gdbus</c>, else <c>xdg-open</c> on the parent folder.</description></item>
/// <item><term>Open path / URI</term><description><c>ShellExecuteEx</c> / <c>open</c> / <c>xdg-open</c>.</description></item>
/// <item><term>Trash</term><description><c>SHFileOperationW</c> with undo / <c>NSFileManager trashItemAtURL</c> /
/// <c>gio trash</c>, else the FreeDesktop.org Trash specification.</description></item>
/// </list>
/// Helper tools are never started through a shell; each argument is passed separately. Opening executables, scripts and
/// app bundles is refused unless <see cref="DesktopNativeOptions.AllowOpeningExecutables"/> is set, and
/// <see cref="OpenUriAsync"/> only launches allow-listed schemes.
/// </remarks>
public sealed partial class DesktopFileOperations : IDesktopFileOperations
{
    private static readonly TimeSpan s_launchWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_toolTimeout = TimeSpan.FromSeconds(30);
    private readonly DesktopNativeOptions _options;
    private readonly ILogger<DesktopFileOperations> _logger;

    /// <summary>Creates the service.</summary>
    public DesktopFileOperations(IOptions<DesktopNativeOptions> options, ILogger<DesktopFileOperations> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.DesktopFiles;

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            return ValueTask.FromResult(CapabilityStatus.Available(Capability, DesktopAdapter.Name, "reveal", "open-path", "open-uri", "trash"));
        }

        if (OperatingSystem.IsLinux())
        {
            if (ProcessRunner.FindExecutable("xdg-open") is not null)
            {
                return ValueTask.FromResult(CapabilityStatus.Available(Capability, DesktopAdapter.Name, "reveal", "open-path", "open-uri", "trash"));
            }

            var reveal = ProcessRunner.FindExecutable("gdbus") is not null ? new[] { "reveal", "trash" } : ["trash"];
            return ValueTask.FromResult(CapabilityStatus.Available(Capability, DesktopAdapter.Name, reveal) with
            {
                ReasonCode = DesktopErrorCodes.ToolMissing,
                Reason = "xdg-open is not installed (package xdg-utils); opening paths and URIs is unavailable.",
            });
        }

        return ValueTask.FromResult(CapabilityStatus.Unsupported(Capability, "Shell file operations are implemented for Windows, macOS and Linux.", DesktopAdapter.Name));
    }

    /// <inheritdoc />
    public async ValueTask<NativeResult> RevealInFileManagerAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ShellPolicy.TryValidateExistingPath(Capability, path, out var fullPath, out _, out var invalid))
        {
            return invalid;
        }

        return await NativeOperation.RunAsync(Capability, async token =>
        {
            if (OperatingSystem.IsWindows())
            {
                return await RevealWindowsAsync(fullPath).ConfigureAwait(false);
            }

            if (OperatingSystem.IsMacOS())
            {
                return ToResult(await ProcessRunner.RunAsync("/usr/bin/open", ["-R", fullPath], s_toolTimeout, token).ConfigureAwait(false), "open -R");
            }

            if (OperatingSystem.IsLinux())
            {
                return await RevealLinuxAsync(fullPath, token).ConfigureAwait(false);
            }

            return NativeError.Unsupported(Capability);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<NativeResult> OpenPathAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ShellPolicy.TryValidateExistingPath(Capability, path, out var fullPath, out var isDirectory, out var invalid))
        {
            return invalid;
        }

        if (!_options.AllowOpeningExecutables && ShellPolicy.IsExecutableLike(fullPath, isDirectory))
        {
            LogExecutableBlocked(_logger, Path.GetExtension(fullPath));
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument,
                "The path is an executable, script, shortcut or application bundle; opening it would run it. " +
                "Set DesktopNativeOptions.AllowOpeningExecutables to allow this.");
        }

        return await LaunchAsync(fullPath, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<NativeResult> OpenUriAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ShellPolicy.TryValidateUri(Capability, uri, _options.AllowedUriSchemes, out var launchText, out var invalid))
        {
            LogUriBlocked(_logger, uri.IsAbsoluteUri ? uri.Scheme : "(relative)");
            return invalid;
        }

        return await LaunchAsync(launchText, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<NativeResult> MoveToTrashAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ShellPolicy.TryValidateExistingPath(Capability, path, out var fullPath, out var isDirectory, out var invalid))
        {
            return invalid;
        }

        if (Path.GetPathRoot(fullPath) == fullPath)
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "A volume root cannot be moved to the trash.");
        }

        return await NativeOperation.RunAsync(Capability, async token =>
        {
            if (OperatingSystem.IsWindows())
            {
                return await TrashWindowsAsync(fullPath).ConfigureAwait(false);
            }

            if (OperatingSystem.IsMacOS())
            {
                return TrashMac(fullPath, isDirectory);
            }

            if (OperatingSystem.IsLinux())
            {
                return await TrashLinuxAsync(fullPath, isDirectory, token).ConfigureAwait(false);
            }

            return NativeError.Unsupported(Capability);
        }, cancellationToken).ConfigureAwait(false);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Task<NativeResult> RevealWindowsAsync(string fullPath) => StaThread.RunAsync(() => WindowsShell.Reveal(fullPath));

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Task<NativeResult> TrashWindowsAsync(string fullPath) => StaThread.RunAsync(() => WindowsShell.MoveToRecycleBin(fullPath));

    private async ValueTask<NativeResult> LaunchAsync(string target, CancellationToken cancellationToken)
    {
        return await NativeOperation.RunAsync(Capability, async token =>
        {
            if (OperatingSystem.IsWindows())
            {
                return WindowsShell.ShellExecute(target);
            }

            if (OperatingSystem.IsMacOS())
            {
                return ToResult(await ProcessRunner.RunAsync("/usr/bin/open", [target], s_toolTimeout, token).ConfigureAwait(false), "open");
            }

            if (OperatingSystem.IsLinux())
            {
                if (ProcessRunner.FindExecutable("xdg-open") is not { } xdgOpen)
                {
                    return NativeError.Unavailable(Capability, DesktopErrorCodes.ToolMissing, "xdg-open is not installed (package xdg-utils).");
                }

                return ToResult(await ProcessRunner.LaunchAsync(xdgOpen, [target], s_launchWait, token).ConfigureAwait(false), "xdg-open");
            }

            return NativeError.Unsupported(Capability);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<NativeResult> RevealLinuxAsync(string fullPath, CancellationToken cancellationToken)
    {
        if (ProcessRunner.FindExecutable("gdbus") is { } gdbus)
        {
            // The file URI is fully percent-encoded, so it cannot break out of the GVariant string literal.
            var outcome = await ProcessRunner.RunAsync(gdbus,
            [
                "call", "--session",
                "--dest", "org.freedesktop.FileManager1",
                "--object-path", "/org/freedesktop/FileManager1",
                "--method", "org.freedesktop.FileManager1.ShowItems",
                "['" + ShellPolicy.ToFileUri(fullPath) + "']",
                "''",
            ], TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            if (outcome.Succeeded)
            {
                return NativeResult.Ok;
            }

            LogRevealFallback(_logger, outcome.ExitCode, outcome.Error);
        }

        if (ProcessRunner.FindExecutable("xdg-open") is not { } xdgOpen)
        {
            return NativeError.Unavailable(Capability, DesktopErrorCodes.ToolMissing,
                "Neither a FileManager1 D-Bus service nor xdg-open is available to reveal the item.");
        }

        var parent = Path.GetDirectoryName(fullPath) ?? fullPath;
        return ToResult(await ProcessRunner.LaunchAsync(xdgOpen, [parent], s_launchWait, cancellationToken).ConfigureAwait(false), "xdg-open");
    }

    private NativeResult TrashMac(string fullPath, bool isDirectory)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return NativeError.Unsupported(Capability);
        }

        try
        {
            var error = MacInterop.TrashItem(fullPath, isDirectory);
            return error is null
                ? NativeResult.Ok
                : NativeError.Failed(Capability, DesktopErrorCodes.TrashUnavailable, error);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            LogTrashFallback(_logger, ex.Message);
            return MoveIntoMacUserTrash(fullPath, isDirectory);
        }
    }

    private NativeResult MoveIntoMacUserTrash(string fullPath, bool isDirectory)
    {
        var trash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash");
        if (!Directory.Exists(trash))
        {
            return NativeError.Failed(Capability, DesktopErrorCodes.TrashUnavailable, "The user's Trash folder does not exist.");
        }

        var name = Path.GetFileName(fullPath);
        var destination = Path.Combine(trash, name);
        for (var i = 2; Path.Exists(destination); i++)
        {
            destination = Path.Combine(trash, $"{Path.GetFileNameWithoutExtension(name)} {i}{Path.GetExtension(name)}");
        }

        try
        {
            if (isDirectory)
            {
                Directory.Move(fullPath, destination);
            }
            else
            {
                File.Move(fullPath, destination);
            }

            return NativeResult.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return NativeError.Failed(Capability, DesktopErrorCodes.TrashUnavailable, $"The item could not be moved to the Trash: {ex.Message}", ex);
        }
    }

    private async Task<NativeResult> TrashLinuxAsync(string fullPath, bool isDirectory, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return NativeError.Unsupported(Capability);
        }

        if (ProcessRunner.FindExecutable("gio") is { } gio)
        {
            var outcome = await ProcessRunner.RunAsync(gio, ["trash", "--", fullPath], s_toolTimeout, cancellationToken).ConfigureAwait(false);
            if (outcome.Succeeded)
            {
                return NativeResult.Ok;
            }

            LogGioTrashFailed(_logger, outcome.ExitCode, outcome.Error);
        }

        return FreeDesktopTrash.MoveToTrash(fullPath, isDirectory);
    }

    private NativeResult ToResult(ProcessOutcome outcome, string tool)
    {
        if (!outcome.Started)
        {
            return NativeError.Unavailable(Capability, DesktopErrorCodes.ToolMissing, $"'{tool}' could not be started: {outcome.Error}");
        }

        // A launcher that is still running at the deadline has handed the request to the target application.
        if (outcome.ExitCode is null or 0)
        {
            return NativeResult.Ok;
        }

        return NativeError.Failed(Capability, NativeErrorCodes.PlatformError, $"'{tool}' failed: {outcome.Describe()}");
    }

    [LoggerMessage(EventId = 5301, Level = LogLevel.Warning, Message = "Refused to open an executable item (extension '{Extension}'); set AllowOpeningExecutables to allow it.")]
    private static partial void LogExecutableBlocked(ILogger logger, string extension);

    [LoggerMessage(EventId = 5302, Level = LogLevel.Warning, Message = "Refused to open a URI with the non-allow-listed scheme '{Scheme}'.")]
    private static partial void LogUriBlocked(ILogger logger, string scheme);

    [LoggerMessage(EventId = 5303, Level = LogLevel.Debug, Message = "FileManager1.ShowItems failed (exit code {ExitCode}: {Error}); opening the parent folder instead.")]
    private static partial void LogRevealFallback(ILogger logger, int? exitCode, string error);

    [LoggerMessage(EventId = 5305, Level = LogLevel.Debug, Message = "gio trash failed (exit code {ExitCode}: {Error}); using the FreeDesktop.org trash implementation.")]
    private static partial void LogGioTrashFailed(ILogger logger, int? exitCode, string error);

    [LoggerMessage(EventId = 5304, Level = LogLevel.Debug, Message = "Native trash failed ({Reason}); using the fallback implementation.")]
    private static partial void LogTrashFallback(ILogger logger, string reason);
}
