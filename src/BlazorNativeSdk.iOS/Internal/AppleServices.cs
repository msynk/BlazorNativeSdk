using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>
/// Bridge between static native entry points (app delegate callbacks, the notification center delegate, background task
/// launch handlers) and the dependency injection container. Activations that arrive before the container is attached
/// (cold start) are buffered and published, in order, once it is.
/// </summary>
internal static class AppleServices
{
    private const int MaxPendingActivations = 32;
    private static readonly Lock s_gate = new();
    private static readonly List<AppActivation> s_pending = [];
    private static TaskCompletionSource<IServiceProvider> s_ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static IServiceProvider? s_services;
    private static ILogger s_logger = NullLogger.Instance;

    /// <summary>The attached service provider, if any.</summary>
    public static IServiceProvider? Current => Volatile.Read(ref s_services);

    /// <summary>A logger for static entry points (a no-op logger until the container is attached).</summary>
    public static ILogger Logger => Volatile.Read(ref s_logger);

    /// <summary>Attaches the container and flushes buffered activations.</summary>
    public static void Attach(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        lock (s_gate)
        {
            if (ReferenceEquals(s_services, services))
            {
                return;
            }

            Volatile.Write(ref s_services, services);
            Volatile.Write(ref s_logger, services.GetService<ILoggerFactory>()?.CreateLogger("BlazorNativeSdk.ApplePlatform") ?? NullLogger.Instance);
            if (!s_ready.TrySetResult(services))
            {
                var ready = new TaskCompletionSource<IServiceProvider>(TaskCreationOptions.RunContinuationsAsynchronously);
                ready.SetResult(services);
                s_ready = ready;
            }

            // Publishing under the lock keeps buffered activations ahead of any that arrive concurrently.
            foreach (var activation in s_pending)
            {
                PublishCore(services, activation);
            }

            AppleLog.FlushedActivations(s_logger, s_pending.Count);
            s_pending.Clear();
        }
    }

    /// <summary>Waits until a container is attached.</summary>
    public static Task<IServiceProvider> WaitAsync(CancellationToken cancellationToken)
    {
        Task<IServiceProvider> ready;
        lock (s_gate)
        {
            ready = s_ready.Task;
        }

        return ready.WaitAsync(cancellationToken);
    }

    /// <summary>Publishes an activation now, or buffers it until the container is attached.</summary>
    public static void PublishActivation(AppActivation activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        lock (s_gate)
        {
            if (s_services is { } services)
            {
                PublishCore(services, activation);
                return;
            }

            if (s_pending.Count >= MaxPendingActivations)
            {
                s_pending.RemoveAt(0);
            }

            s_pending.Add(activation);
        }
    }

    private static void PublishCore(IServiceProvider services, AppActivation activation)
    {
        try
        {
            if (services.GetService<IActivationPublisher>() is not { } publisher)
            {
                AppleLog.NoActivationPublisher(s_logger, activation.Kind);
                return;
            }

            var pending = publisher.PublishAsync(activation);
            if (!pending.IsCompletedSuccessfully)
            {
                _ = ObserveAsync(pending, activation.Kind);
            }
        }
        catch (Exception ex)
        {
            AppleLog.ActivationPublishFailed(s_logger, activation.Kind, ex);
        }
    }

    private static async Task ObserveAsync(ValueTask pending, ActivationKind kind)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppleLog.ActivationPublishFailed(Logger, kind, ex);
        }
    }
}
