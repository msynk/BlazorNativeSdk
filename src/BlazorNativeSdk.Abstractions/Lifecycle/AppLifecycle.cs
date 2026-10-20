namespace BlazorNativeSdk.Lifecycle;

/// <summary>Application lifecycle states, normalized across platforms.</summary>
/// <remarks>
/// <list type="table">
/// <listheader><term>State</term><description>Browser / Android / iOS / Desktop</description></listheader>
/// <item><term><see cref="Active"/></term><description>visible and focused / resumed / active / focused window</description></item>
/// <item><term><see cref="Inactive"/></term><description>visible, not focused / paused / inactive (overlay, call) / no focused app window</description></item>
/// <item><term><see cref="Background"/></term><description>hidden tab / stopped / background / all windows minimized or hidden</description></item>
/// <item><term><see cref="Terminating"/></term><description>pagehide / destroy / willTerminate (not guaranteed) / last window closing</description></item>
/// </list>
/// </remarks>
public enum AppLifecycleState
{
    /// <summary>The app is starting and has not reported a state yet.</summary>
    Launching,

    /// <summary>The app is in the foreground and receiving input.</summary>
    Active,

    /// <summary>The app is visible but not receiving input.</summary>
    Inactive,

    /// <summary>The app is not visible. It may be suspended or terminated by the OS without further notice.</summary>
    Background,

    /// <summary>The app is shutting down. Delivery of this state is best effort on every platform.</summary>
    Terminating,
}

/// <summary>How much background execution time an app gets.</summary>
public enum BackgroundExecutionGuarantee
{
    /// <summary>The process keeps running while in the background (desktop apps). The OS may still sleep the machine.</summary>
    NotRequired,

    /// <summary>The OS granted a bounded amount of time (iOS background task) that ends with <see cref="IBackgroundTimeLease.ExpirationToken"/>.</summary>
    OsGranted,

    /// <summary>No guarantee: the process may be suspended or killed at any time (Android without a foreground service, background browser tabs).</summary>
    BestEffort,
}

/// <summary>Describes a lifecycle transition.</summary>
public sealed class AppLifecycleChangedEventArgs : EventArgs
{
    /// <summary>Creates event data.</summary>
    public AppLifecycleChangedEventArgs(AppLifecycleState previous, AppLifecycleState current, DateTimeOffset timestamp, string? reason = null)
    {
        Previous = previous;
        Current = current;
        Timestamp = timestamp;
        Reason = reason;
    }

    /// <summary>The previous state.</summary>
    public AppLifecycleState Previous { get; }

    /// <summary>The new state.</summary>
    public AppLifecycleState Current { get; }

    /// <summary>When the transition happened.</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>The platform event that triggered the transition (diagnostics), for example <c>visibilitychange</c> or <c>OnStop</c>.</summary>
    public string? Reason { get; }

    /// <summary><see langword="true"/> when the app returned to the foreground from the background.</summary>
    public bool IsResume => Current == AppLifecycleState.Active && Previous is AppLifecycleState.Background or AppLifecycleState.Inactive;

    /// <summary><see langword="true"/> when the app moved to the background.</summary>
    public bool IsBackgrounding => Current == AppLifecycleState.Background && Previous != AppLifecycleState.Background;
}

/// <summary>A grant of execution time after the app moves to the background.</summary>
public interface IBackgroundTimeLease : IAsyncDisposable
{
    /// <summary>The name used for diagnostics.</summary>
    string Name { get; }

    /// <summary>The guarantee that the platform provides.</summary>
    BackgroundExecutionGuarantee Guarantee { get; }

    /// <summary>The remaining time when the platform reports it.</summary>
    TimeSpan? RemainingTime { get; }

    /// <summary>Cancelled shortly before the OS revokes the grant. Stop work and persist state when it fires.</summary>
    CancellationToken ExpirationToken { get; }
}

/// <summary>Observes the application lifecycle and requests background execution time.</summary>
public interface IAppLifecycle : INativeCapability
{
    /// <summary>The current state.</summary>
    AppLifecycleState State { get; }

    /// <summary>Raised after every state transition.</summary>
    event EventHandler<AppLifecycleChangedEventArgs>? StateChanged;

    /// <summary>Raised when the OS reports memory pressure. Release caches.</summary>
    event EventHandler? MemoryPressure;

    /// <summary>
    /// Registers an asynchronous transition handler. When the app moves to the background, adapters run handlers inside a
    /// background time lease where available and pass its expiration token, so handlers get a fair chance to persist state.
    /// </summary>
    IDisposable RegisterTransitionHandler(Func<AppLifecycleChangedEventArgs, CancellationToken, ValueTask> handler);

    /// <summary>
    /// Requests time to finish work after the app moves to the background (for example uploading a just-captured photo).
    /// Always dispose the lease as soon as the work completes.
    /// </summary>
    ValueTask<NativeResult<IBackgroundTimeLease>> RequestBackgroundTimeAsync(string name, CancellationToken cancellationToken = default);
}
