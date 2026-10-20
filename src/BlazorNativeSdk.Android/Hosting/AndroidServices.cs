using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>
/// Process-wide bridge from Android entry points that the OS instantiates (broadcast receivers, WorkManager workers,
/// activity callbacks) to the application's service provider. The provider is attached by <see cref="AndroidStartupTask"/>
/// when the SDK starts; activations that arrive earlier are buffered and replayed in order.
/// </summary>
internal static partial class AndroidServices
{
    private const int MaxPendingActivations = 32;
    private static readonly Lock s_gate = new();
    private static readonly Queue<AppActivation> s_pending = new();
    private static IServiceProvider? s_provider;
    private static TaskCompletionSource<IServiceProvider> s_attached = NewAttachedSource();

    /// <summary>The attached service provider, or <see langword="null"/> before the SDK started.</summary>
    public static IServiceProvider? Provider
    {
        get
        {
            lock (s_gate)
            {
                return s_provider;
            }
        }
    }

    /// <summary>Attaches the application's root service provider and replays buffered activations.</summary>
    public static void Attach(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        TaskCompletionSource<IServiceProvider> attached;
        lock (s_gate)
        {
            s_provider = provider;
            attached = s_attached;
            if (s_pending.Count > 0)
            {
                var publisher = provider.GetService<IActivationPublisher>();
                var logger = CreateLoggerCore(provider);
                while (s_pending.TryDequeue(out var activation))
                {
                    Publish(publisher, activation, logger);
                }
            }
        }

        attached.TrySetResult(provider);
    }

    /// <summary>Detaches <paramref name="provider"/> when it is the attached one (the container is being disposed).</summary>
    public static void Detach(IServiceProvider provider)
    {
        lock (s_gate)
        {
            if (ReferenceEquals(s_provider, provider))
            {
                s_provider = null;
                s_attached = NewAttachedSource();
            }
        }
    }

    /// <summary>Waits until a provider is attached, or returns <see langword="null"/> after <paramref name="timeout"/>.</summary>
    public static async Task<IServiceProvider?> WaitForProviderAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task<IServiceProvider> attached;
        lock (s_gate)
        {
            if (s_provider is not null)
            {
                return s_provider;
            }

            attached = s_attached.Task;
        }

        try
        {
            return await attached.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>Publishes an activation, buffering it until the SDK has started. Safe to call from any thread.</summary>
    public static void PublishActivation(AppActivation activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        lock (s_gate)
        {
            if (s_provider is null)
            {
                if (s_pending.Count >= MaxPendingActivations)
                {
                    s_pending.Dequeue();
                }

                s_pending.Enqueue(activation);
                return;
            }

            Publish(s_provider.GetService<IActivationPublisher>(), activation, CreateLoggerCore(s_provider));
        }
    }

    /// <summary>Creates a logger from the attached provider, or a no-op logger before the SDK started.</summary>
    public static ILogger CreateLogger(string category)
    {
        var provider = Provider;
        return provider?.GetService<ILoggerFactory>()?.CreateLogger(category) ?? NullLogger.Instance;
    }

    private static ILogger CreateLoggerCore(IServiceProvider provider) =>
        provider.GetService<ILoggerFactory>()?.CreateLogger("BlazorNativeSdk.AndroidPlatform.Activation") ?? NullLogger.Instance;

    private static void Publish(IActivationPublisher? publisher, AppActivation activation, ILogger logger)
    {
        if (publisher is null)
        {
            LogNoPublisher(logger, activation.Kind);
            return;
        }

        try
        {
            var pending = publisher.PublishAsync(activation);
            if (!pending.IsCompletedSuccessfully)
            {
                _ = ObserveAsync(pending, activation.Kind, logger);
            }
        }
        catch (Exception ex)
        {
            LogPublishFailed(logger, activation.Kind, ex);
        }
    }

    private static async Task ObserveAsync(ValueTask pending, ActivationKind kind, ILogger logger)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogPublishFailed(logger, kind, ex);
        }
    }

    private static TaskCompletionSource<IServiceProvider> NewAttachedSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Warning, Message = "No IActivationPublisher is registered; a {Kind} activation was dropped.")]
    private static partial void LogNoPublisher(ILogger logger, ActivationKind kind);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Error, Message = "Publishing a {Kind} activation failed.")]
    private static partial void LogPublishFailed(ILogger logger, ActivationKind kind, Exception exception);
}

/// <summary>Attaches the service provider to <see cref="AndroidServices"/> when the SDK starts.</summary>
internal sealed class AndroidStartupTask : INativeSdkStartupTask, IDisposable, IAsyncDisposable
{
    private readonly IServiceProvider _services;
    private int _disposed;

    public AndroidStartupTask(IServiceProvider services) => _services = services;

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            AndroidServices.Attach(_services);
        }

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            AndroidServices.Detach(_services);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
