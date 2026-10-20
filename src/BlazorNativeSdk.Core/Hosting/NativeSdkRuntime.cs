using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BlazorNativeSdk;

/// <summary>A component that needs to run once when the SDK starts (restore persisted work, attach to platform events, ...).</summary>
public interface INativeSdkStartupTask
{
    /// <summary>Starts the component. Called once per application.</summary>
    ValueTask StartAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Runs <see cref="INativeSdkStartupTask"/> instances exactly once. Started automatically by host adapters (MAUI, WPF) and by
/// the <c>NativeActivationOutlet</c> component, or explicitly with <see cref="NativeSdkServiceProviderExtensions.StartBlazorNativeSdkAsync"/>.
/// </summary>
public sealed partial class NativeSdkRuntime
{
    private readonly IServiceProvider _services;
    private readonly ILogger<NativeSdkRuntime> _logger;
    private readonly Lock _gate = new();
    private Task? _startTask;

    /// <summary>Creates the runtime.</summary>
    public NativeSdkRuntime(IServiceProvider services, ILogger<NativeSdkRuntime> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <summary><see langword="true"/> once startup has begun.</summary>
    public bool IsStarted => _startTask is not null;

    /// <summary>Starts the SDK once; subsequent calls return the same task.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return _startTask ??= StartCoreAsync(cancellationToken);
        }
    }

    /// <summary>Starts the SDK without waiting; failures are logged.</summary>
    public void EnsureStarted() => _ = StartAsync();

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        foreach (var task in _services.GetServices<INativeSdkStartupTask>())
        {
            try
            {
                await task.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogStartupTaskFailed(_logger, task.GetType().Name, ex);
            }
        }
    }

    [LoggerMessage(EventId = 1101, Level = LogLevel.Error, Message = "BlazorNativeSdk startup task {Task} failed.")]
    private static partial void LogStartupTaskFailed(ILogger logger, string task, Exception exception);
}

/// <summary>Extensions for <see cref="IServiceProvider"/>.</summary>
public static class NativeSdkServiceProviderExtensions
{
    /// <summary>Starts SDK background components (persisted work, transfers, platform listeners).</summary>
    public static Task StartBlazorNativeSdkAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetRequiredService<NativeSdkRuntime>().StartAsync(cancellationToken);
    }
}
