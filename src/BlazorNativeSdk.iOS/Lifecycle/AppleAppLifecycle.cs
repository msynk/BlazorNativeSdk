using System.Diagnostics;
using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.Logging;
using MainThread = Microsoft.Maui.ApplicationModel.MainThread;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>
/// <see cref="IAppLifecycle"/> driven by <c>UIApplication</c> notifications, with background time from
/// <c>UIApplication.BeginBackgroundTask</c>.
/// </summary>
/// <remarks>
/// <list type="table">
/// <listheader><term>Notification</term><description>State</description></listheader>
/// <item><term>DidBecomeActive</term><description><see cref="AppLifecycleState.Active"/></description></item>
/// <item><term>WillResignActive, WillEnterForeground</term><description><see cref="AppLifecycleState.Inactive"/></description></item>
/// <item><term>DidEnterBackground</term><description><see cref="AppLifecycleState.Background"/></description></item>
/// <item><term>WillTerminate</term><description><see cref="AppLifecycleState.Terminating"/> (not delivered when the system suspends and later kills the app)</description></item>
/// <item><term>DidReceiveMemoryWarning</term><description><see cref="IAppLifecycle.MemoryPressure"/></description></item>
/// </list>
/// Background time leases end with <see cref="IBackgroundTimeLease.ExpirationToken"/> shortly before iOS revokes the grant
/// (typically about 30 seconds after the app moves to the background); dispose them as soon as the work is done.
/// </remarks>
public sealed partial class AppleAppLifecycle : AppLifecycleBase, IDisposable, IAsyncDisposable
{
    private readonly NSObject[] _observers;
    private int _disposed;

    /// <summary>Creates the lifecycle and starts observing the application.</summary>
    public AppleAppLifecycle(TimeProvider timeProvider, ILogger<AppleAppLifecycle> logger)
        : base(timeProvider, logger)
    {
        _observers =
        [
            UIApplication.Notifications.ObserveDidBecomeActive((_, _) => ReportState(AppLifecycleState.Active, "DidBecomeActive")),
            UIApplication.Notifications.ObserveWillResignActive((_, _) => ReportState(AppLifecycleState.Inactive, "WillResignActive")),
            UIApplication.Notifications.ObserveDidEnterBackground((_, _) => ReportState(AppLifecycleState.Background, "DidEnterBackground")),
            UIApplication.Notifications.ObserveWillEnterForeground((_, _) => ReportState(AppLifecycleState.Inactive, "WillEnterForeground")),
            UIApplication.Notifications.ObserveWillTerminate((_, _) => ReportState(AppLifecycleState.Terminating, "WillTerminate")),
            UIApplication.Notifications.ObserveDidReceiveMemoryWarning((_, _) => ReportMemoryPressure()),
        ];

        if (NSThread.IsMain)
        {
            ReportInitialState();
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(ReportInitialState);
        }
    }

    /// <inheritdoc />
    public override BackgroundExecutionGuarantee BackgroundGuarantee => BackgroundExecutionGuarantee.OsGranted;

    /// <inheritdoc />
    protected override string AdapterName => AppleHost.AdapterName;

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var observer in _observers)
        {
            observer.Dispose();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override async ValueTask<NativeResult<IBackgroundTimeLease>> RequestBackgroundTimeCoreAsync(string name, CancellationToken cancellationToken)
    {
        return await AppleUI.OnMainThreadAsync(() =>
        {
            var application = UIApplication.SharedApplication;
            var grant = new BackgroundTaskGrant();
            BackgroundTimeLease? lease = null;
            var identifier = application.BeginBackgroundTask(name, () =>
            {
                // Called on the main thread shortly before iOS revokes the time: signal the owner, then end the task.
                LogLeaseExpiring(Logger, name);
                lease?.Expire();
                grant.End();
            });

            if (identifier == UIApplication.BackgroundTaskInvalid)
            {
                return NativeResult.Failure<IBackgroundTimeLease>(NativeError.Unavailable(Capability, NativeErrorCodes.BackgroundExecutionDenied,
                    "iOS refused to grant background time (the app may already be out of background time)."));
            }

            grant.Identifier = identifier;
            grant.Observe(application.BackgroundTimeRemaining);
            lease = new BackgroundTimeLease(name, BackgroundGuarantee, grant.GetRemainingTime, () =>
            {
                grant.End();
                return ValueTask.CompletedTask;
            });
            return NativeResult.Success<IBackgroundTimeLease>(lease);
        }).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ReportInitialState()
    {
        if (State != AppLifecycleState.Launching)
        {
            return;
        }

        ReportState(UIApplication.SharedApplication.ApplicationState switch
        {
            UIApplicationState.Active => AppLifecycleState.Active,
            UIApplicationState.Background => AppLifecycleState.Background,
            _ => AppLifecycleState.Inactive,
        }, "initial");
    }

    [LoggerMessage(EventId = 3901, Level = LogLevel.Information, Message = "Background time for '{Name}' is expiring.")]
    private static partial void LogLeaseExpiring(ILogger logger, string name);

    /// <summary>One <c>BeginBackgroundTask</c> grant; ends exactly once from any thread.</summary>
    private sealed class BackgroundTaskGrant
    {
        private readonly Lock _gate = new();
        private double _remainingSeconds = double.MaxValue;
        private long _observedAt = Stopwatch.GetTimestamp();
        private int _ended;

        public nint Identifier { get; set; }

        public void Observe(double remainingSeconds)
        {
            lock (_gate)
            {
                _remainingSeconds = remainingSeconds;
                _observedAt = Stopwatch.GetTimestamp();
            }
        }

        /// <summary>The remaining time while the app is in the background; <see langword="null"/> while it is in the foreground.</summary>
        public TimeSpan? GetRemainingTime()
        {
            if (NSThread.IsMain)
            {
                Observe(UIApplication.SharedApplication.BackgroundTimeRemaining);
            }

            lock (_gate)
            {
                // UIKit reports DBL_MAX while the app is in the foreground.
                if (_remainingSeconds > TimeSpan.FromDays(1).TotalSeconds)
                {
                    return null;
                }

                var remaining = TimeSpan.FromSeconds(_remainingSeconds) - Stopwatch.GetElapsedTime(_observedAt);
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        public void End()
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0)
            {
                return;
            }

            if (NSThread.IsMain)
            {
                UIApplication.SharedApplication.EndBackgroundTask(Identifier);
            }
            else
            {
                MainThread.BeginInvokeOnMainThread(() => UIApplication.SharedApplication.EndBackgroundTask(Identifier));
            }
        }
    }
}
