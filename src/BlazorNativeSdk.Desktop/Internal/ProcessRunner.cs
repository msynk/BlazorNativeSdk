using System.Diagnostics;
using System.Text;

namespace BlazorNativeSdk.Desktop.Internal;

/// <summary>The outcome of running a helper process.</summary>
/// <param name="Started">Whether the process started.</param>
/// <param name="ExitCode">The exit code, or <see langword="null"/> when it was still running at the deadline.</param>
/// <param name="Output">Captured standard output (bounded).</param>
/// <param name="Error">Captured standard error (bounded).</param>
internal sealed record ProcessOutcome(bool Started, int? ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;

    public string Describe() =>
        !Started ? "the process could not be started"
        : ExitCode is null ? "the process did not finish in time"
        : $"exit code {ExitCode}{(string.IsNullOrWhiteSpace(Error) ? string.Empty : ": " + Error.Trim())}";
}

/// <summary>
/// Starts helper tools (<c>open</c>, <c>xdg-open</c>, <c>gdbus</c>, <c>gio</c>, <c>xdg-mime</c>) safely: never through a
/// shell, with every argument passed separately through <see cref="ProcessStartInfo.ArgumentList"/>, resolved to an
/// absolute path from absolute <c>PATH</c> entries only, with bounded output capture and a deadline.
/// </summary>
internal static class ProcessRunner
{
    private const int MaxCapturedChars = 64 * 1024;

    /// <summary>Finds an executable in the absolute entries of <c>PATH</c> (relative entries such as <c>.</c> are ignored).</summary>
    public static string? FindExecutable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var candidates = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(OperatingSystem.IsWindows() ? [] : ["/usr/bin", "/bin", "/usr/local/bin"]);
        foreach (var directory in candidates)
        {
            if (!Path.IsPathFullyQualified(directory))
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate) && IsExecutable(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    /// <summary>Runs a tool to completion (killing it at the deadline) and captures its output.</summary>
    public static Task<ProcessOutcome> RunAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken) =>
        RunCoreAsync(fileName, arguments, timeout, capture: true, killOnTimeout: true, cancellationToken);

    /// <summary>
    /// Starts a launcher (<c>open</c>, <c>xdg-open</c>) and waits up to <paramref name="timeout"/> for its exit code. A
    /// launcher still running at the deadline is assumed to have handed off successfully and is left alone. Output is not
    /// redirected, because launched applications may inherit and hold the pipes open.
    /// </summary>
    public static Task<ProcessOutcome> LaunchAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken) =>
        RunCoreAsync(fileName, arguments, timeout, capture: false, killOnTimeout: false, cancellationToken);

    private static async Task<ProcessOutcome> RunCoreAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout, bool capture, bool killOnTimeout, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = capture,
            RedirectStandardError = capture,
            RedirectStandardInput = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new ProcessOutcome(false, null, string.Empty, string.Empty);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new ProcessOutcome(false, null, string.Empty, ex.Message);
        }

        var output = capture ? ReadBoundedAsync(process.StandardOutput) : Task.FromResult(string.Empty);
        var error = capture ? ReadBoundedAsync(process.StandardError) : Task.FromResult(string.Empty);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (killOnTimeout || cancellationToken.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessOutcome(true, null, string.Empty, string.Empty);
        }

        return new ProcessOutcome(true, process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            // Keep draining after the cap so the child never blocks on a full pipe.
            var take = Math.Min(read, MaxCapturedChars - builder.Length);
            if (take > 0)
            {
                builder.Append(buffer, 0, take);
            }
        }

        return builder.ToString();
    }

    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        const UnixFileMode Execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        return (File.GetUnixFileMode(path) & Execute) != 0;
    }
}
