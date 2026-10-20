using System.Collections.Concurrent;
using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk.Desktop.Tests;

internal static class TestSupport
{
    public static NativeSdkOptions SdkOptions(string? appId = null) => new() { AppId = appId ?? "bns-test-" + Guid.NewGuid().ToString("N")[..12] };

    public static DesktopIpcService CreateIpc(NativeSdkOptions sdk, Action<DesktopNativeOptions>? configure = null)
    {
        var options = new DesktopNativeOptions();
        configure?.Invoke(options);
        return new DesktopIpcService(Options.Create(sdk), Options.Create(options), NullLogger<DesktopIpcService>.Instance);
    }

    public static string NewChannel() => "test-" + Guid.NewGuid().ToString("N")[..8];

    public static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "bns-desktop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken, int timeoutMs = 5000)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}

/// <summary>Records published activations.</summary>
internal sealed class RecordingPublisher : IActivationPublisher
{
    private readonly Lock _gate = new();
    private readonly List<AppActivation> _activations = [];

    public IReadOnlyList<AppActivation> Activations
    {
        get
        {
            lock (_gate)
            {
                return [.. _activations];
            }
        }
    }

    public ValueTask PublishAsync(AppActivation activation, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _activations.Add(activation);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>Records log entries.</summary>
internal sealed class RecordingLogger : ILogger
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries => [.. _entries];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _entries.Enqueue((logLevel, formatter(state, exception)));
}
