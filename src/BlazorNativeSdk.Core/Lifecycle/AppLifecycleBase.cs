using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlazorNativeSdk.Lifecycle;

/// <summary>
/// Shared implementation of <see cref="IAppLifecycle"/> for adapters. Adapters translate platform events into
/// <see cref="ReportState"/> calls and override <see cref="RequestBackgroundTimeCoreAsync"/> when the platform grants
/// background time.
/// </summary>
public abstract partial class AppLifecycleBase : IAppLifecycle
{
    private readonly Lock _gate = new();
    private readonly List<Func<AppLifecycleChangedEventArgs, CancellationToken, ValueTask>> _handlers = [];
    private AppLifecycleState _state = AppLifecycleState.Launching;

    /// <summary>Initializes the base class.</summary>
    protected AppLifecycleBase(TimeProvider? timeProvider = null, ILogger? logger = null)
    {
        TimeProvider = timeProvider ?? TimeProvider.System;
        Logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.Lifecycle;

    /// <summary>The adapter name used in <see cref="CapabilityStatus.Adapter"/>.</summary>
    protected abstract string AdapterName { get; }

    /// <summary>The background guarantee this adapter provides.</summary>
    public abstract BackgroundExecutionGuarantee BackgroundGuarantee { get; }

    /// <summary>The time provider.</summary>
    protected TimeProvider TimeProvider { get; }

    /// <summary>The logger.</summary>
    protected ILogger Logger { get; }

    /// <inheritdoc />
    public AppLifecycleState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>The completion of the handlers started by the most recent transition (useful in tests).</summary>
    public Task LastTransitionCompletion { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public event EventHandler<AppLifecycleChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public event EventHandler? MemoryPressure;

    /// <inheritdoc />
    public virtual ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Available(Capability, AdapterName, BackgroundGuarantee.ToString()));

    /// <inheritdoc />
    public IDisposable RegisterTransitionHandler(Func<AppLifecycleChangedEventArgs, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _handlers.Add(handler);
        }

        return new Registration(this, handler);
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<IBackgroundTimeLease>> RequestBackgroundTimeAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return RequestBackgroundTimeCoreAsync(name, cancellationToken);
    }

    /// <summary>
    /// Reports a platform lifecycle event. Duplicate states are ignored. Transition handlers run asynchronously; when
    /// moving to the background they run inside a background time lease.
    /// </summary>
    /// <returns><see langword="true"/> when the state changed.</returns>
    public bool ReportState(AppLifecycleState state, string? reason = null)
    {
        AppLifecycleChangedEventArgs args;
        Func<AppLifecycleChangedEventArgs, CancellationToken, ValueTask>[] handlers;
        lock (_gate)
        {
            if (_state == state)
            {
                return false;
            }

            args = new AppLifecycleChangedEventArgs(_state, state, TimeProvider.GetUtcNow(), reason);
            _state = state;
            handlers = [.. _handlers];
        }

        LogTransition(Logger, args.Previous, args.Current, reason);

        try
        {
            StateChanged?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            LogHandlerFailed(Logger, ex);
        }

        if (handlers.Length > 0)
        {
            LastTransitionCompletion = RunHandlersAsync(args, handlers);
        }

        return true;
    }

    /// <summary>Reports that the OS signalled memory pressure.</summary>
    public void ReportMemoryPressure()
    {
        try
        {
            MemoryPressure?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            LogHandlerFailed(Logger, ex);
        }
    }

    /// <summary>
    /// Requests background time from the platform. The default implementation returns a lease whose guarantee is
    /// <see cref="BackgroundGuarantee"/> and that never expires on its own.
    /// </summary>
    protected virtual ValueTask<NativeResult<IBackgroundTimeLease>> RequestBackgroundTimeCoreAsync(string name, CancellationToken cancellationToken)
    {
        IBackgroundTimeLease lease = new BackgroundTimeLease(name, BackgroundGuarantee);
        return ValueTask.FromResult(NativeResult.Success(lease));
    }

    private async Task RunHandlersAsync(AppLifecycleChangedEventArgs args, Func<AppLifecycleChangedEventArgs, CancellationToken, ValueTask>[] handlers)
    {
        await Task.Yield();
        IBackgroundTimeLease? lease = null;
        try
        {
            if (args.IsBackgrounding)
            {
                var result = await RequestBackgroundTimeCoreAsync("lifecycle-transition", CancellationToken.None).ConfigureAwait(false);
                lease = result.IsSuccess ? result.Value : null;
            }

            var token = lease?.ExpirationToken ?? CancellationToken.None;
            foreach (var handler in handlers)
            {
                try
                {
                    await handler(args, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    LogLeaseExpired(Logger);
                    break;
                }
                catch (Exception ex)
                {
                    LogHandlerFailed(Logger, ex);
                }
            }
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void Unregister(Func<AppLifecycleChangedEventArgs, CancellationToken, ValueTask> handler)
    {
        lock (_gate)
        {
            _handlers.Remove(handler);
        }
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug, Message = "Lifecycle {Previous} -> {Current} ({Reason})")]
    private static partial void LogTransition(ILogger logger, AppLifecycleState previous, AppLifecycleState current, string? reason);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Error, Message = "A lifecycle handler failed.")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning, Message = "Background time expired before all lifecycle handlers completed.")]
    private static partial void LogLeaseExpired(ILogger logger);

    private sealed class Registration(AppLifecycleBase owner, Func<AppLifecycleChangedEventArgs, CancellationToken, ValueTask> handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Unregister(handler);
            }
        }
    }
}

/// <summary>
/// A general-purpose <see cref="IBackgroundTimeLease"/>. Adapters supply an expiration source and a callback that ends the
/// platform grant (for example <c>UIApplication.EndBackgroundTask</c>).
/// </summary>
public sealed class BackgroundTimeLease : IBackgroundTimeLease
{
    private readonly CancellationTokenSource _expiration = new();
    private readonly Func<TimeSpan?>? _remainingTime;
    private readonly Func<ValueTask>? _onDispose;
    private int _disposed;

    /// <summary>Creates a lease.</summary>
    /// <param name="name">The lease name.</param>
    /// <param name="guarantee">The guarantee.</param>
    /// <param name="remainingTime">Reports the remaining time.</param>
    /// <param name="onDispose">Ends the platform grant.</param>
    public BackgroundTimeLease(string name, BackgroundExecutionGuarantee guarantee, Func<TimeSpan?>? remainingTime = null, Func<ValueTask>? onDispose = null)
    {
        Name = name;
        Guarantee = guarantee;
        _remainingTime = remainingTime;
        _onDispose = onDispose;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public BackgroundExecutionGuarantee Guarantee { get; }

    /// <inheritdoc />
    public TimeSpan? RemainingTime => _remainingTime?.Invoke();

    /// <inheritdoc />
    public CancellationToken ExpirationToken => _expiration.Token;

    /// <summary>Signals that the platform is about to revoke the grant.</summary>
    public void Expire()
    {
        try
        {
            _expiration.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_onDispose is not null)
        {
            await _onDispose().ConfigureAwait(false);
        }

        _expiration.Dispose();
    }
}

/// <summary>
/// The lifecycle used when no adapter reports platform events. It reports <see cref="AppLifecycleState.Active"/> and an
/// <see cref="CapabilityAvailability.Unknown"/> status. Hosts can drive it manually with <see cref="AppLifecycleBase.ReportState"/>.
/// </summary>
public sealed class ManualAppLifecycle : AppLifecycleBase
{
    /// <summary>Creates the lifecycle.</summary>
    public ManualAppLifecycle(TimeProvider? timeProvider = null, ILogger<ManualAppLifecycle>? logger = null)
        : base(timeProvider, logger)
    {
        ReportState(AppLifecycleState.Active, "default");
    }

    /// <inheritdoc />
    protected override string AdapterName => "manual";

    /// <inheritdoc />
    public override BackgroundExecutionGuarantee BackgroundGuarantee => BackgroundExecutionGuarantee.BestEffort;

    /// <inheritdoc />
    public override ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Unknown(Capability, "No adapter reports lifecycle events; the state is only updated manually.", AdapterName));
}
