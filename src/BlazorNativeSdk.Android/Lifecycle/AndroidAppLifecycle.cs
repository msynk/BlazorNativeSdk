using Android.App;
using Android.Content;
using Android.Content.Res;
using Android.OS;
using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>
/// <see cref="IAppLifecycle"/> for Android, driven by <see cref="Application.IActivityLifecycleCallbacks"/> (activity
/// counts) and <see cref="IComponentCallbacks2"/> (memory pressure).
/// </summary>
/// <remarks>
/// <list type="table">
/// <item><term><see cref="AppLifecycleState.Active"/></term><description>at least one activity is resumed.</description></item>
/// <item><term><see cref="AppLifecycleState.Inactive"/></term><description>an activity is started (visible) but none is resumed (dialogs, multi-window).</description></item>
/// <item><term><see cref="AppLifecycleState.Background"/></term><description>no activity is started.</description></item>
/// <item><term><see cref="AppLifecycleState.Terminating"/></term><description>the last activity was destroyed while finishing
/// (back press or <c>finish()</c>). The OS kills background processes without notice, so this is best effort.</description></item>
/// </list>
/// Android grants no extra time in the background: <see cref="BackgroundGuarantee"/> is
/// <see cref="BackgroundExecutionGuarantee.BestEffort"/> and leases never expire on their own. Use WorkManager
/// (<see cref="Lifecycle.IBackgroundWorkScheduler"/>) for work that must complete.
/// </remarks>
public sealed partial class AndroidAppLifecycle : AppLifecycleBase, INativeSdkStartupTask, IDisposable, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly HashSet<int> _created = [];
    private readonly HashSet<int> _started = [];
    private readonly HashSet<int> _resumed = [];
    private Callbacks? _callbacks;
    private Application? _application;
    private int _disposed;

    /// <summary>Creates the lifecycle.</summary>
    public AndroidAppLifecycle(ILogger<AndroidAppLifecycle> logger, TimeProvider? timeProvider = null)
        : base(timeProvider, logger)
    {
    }

    /// <inheritdoc />
    protected override string AdapterName => "android";

    /// <inheritdoc />
    public override BackgroundExecutionGuarantee BackgroundGuarantee => BackgroundExecutionGuarantee.BestEffort;

    /// <summary>Registers the platform callbacks and reports the initial state.</summary>
    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_callbacks is not null || Volatile.Read(ref _disposed) != 0)
            {
                return ValueTask.CompletedTask;
            }

            _application = (AndroidContext.App.ApplicationContext ?? AndroidContext.App) as Application;
            if (_application is null)
            {
                LogNoApplication(Logger);
                return ValueTask.CompletedTask;
            }

            _callbacks = new Callbacks(this);
            _application.RegisterActivityLifecycleCallbacks(_callbacks);
            _application.RegisterComponentCallbacks(_callbacks);
            SeedFromCurrentActivity();
        }

        ReportCurrent("initial");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Callbacks? callbacks;
        Application? application;
        lock (_gate)
        {
            callbacks = _callbacks;
            application = _application;
            _callbacks = null;
            _application = null;
        }

        if (callbacks is not null && application is not null)
        {
            application.UnregisterActivityLifecycleCallbacks(callbacks);
            application.UnregisterComponentCallbacks(callbacks);
            callbacks.Dispose();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void SeedFromCurrentActivity()
    {
        var activity = Platform.CurrentActivity;
        if (activity is null)
        {
            // No UI yet: either the activity is about to be created (foreground launch) or the OS started the process for a
            // receiver or worker.
            var info = new ActivityManager.RunningAppProcessInfo();
            ActivityManager.GetMyMemoryState(info);
            if ((int)info.Importance > AndroidLifecycleMapper.ImportanceForeground)
            {
                ReportState(AppLifecycleState.Background, "no-activity");
            }

            return;
        }

        var id = Identify(activity);
        _created.Add(id);
        var state = (activity as AndroidX.Lifecycle.ILifecycleOwner)?.Lifecycle.CurrentState;
        if (state is null)
        {
            // Not an AndroidX activity: assume it is in the foreground since Essentials tracks it as current.
            _started.Add(id);
            _resumed.Add(id);
            return;
        }

        if (state.IsAtLeast(AndroidX.Lifecycle.Lifecycle.State.Started!))
        {
            _started.Add(id);
        }

        if (state.IsAtLeast(AndroidX.Lifecycle.Lifecycle.State.Resumed!))
        {
            _resumed.Add(id);
        }
    }

    private void OnActivityEvent(Activity activity, ActivityEvent kind)
    {
        var id = Identify(activity);
        var terminating = false;
        lock (_gate)
        {
            switch (kind)
            {
                case ActivityEvent.Created:
                    _created.Add(id);
                    break;
                case ActivityEvent.Started:
                    _created.Add(id);
                    _started.Add(id);
                    break;
                case ActivityEvent.Resumed:
                    _created.Add(id);
                    _started.Add(id);
                    _resumed.Add(id);
                    break;
                case ActivityEvent.Paused:
                    _resumed.Remove(id);
                    break;
                case ActivityEvent.Stopped:
                    _resumed.Remove(id);
                    _started.Remove(id);
                    break;
                case ActivityEvent.Destroyed:
                    _resumed.Remove(id);
                    _started.Remove(id);
                    _created.Remove(id);
                    terminating = _created.Count == 0 && activity.IsFinishing && !activity.IsChangingConfigurations;
                    break;
            }
        }

        if (terminating)
        {
            ReportState(AppLifecycleState.Terminating, "OnDestroy");
        }
        else if (kind != ActivityEvent.Created)
        {
            ReportCurrent("On" + kind);
        }
    }

    private void ReportCurrent(string reason)
    {
        AppLifecycleState state;
        lock (_gate)
        {
            if (_created.Count == 0 && _started.Count == 0)
            {
                return;
            }

            state = AndroidLifecycleMapper.MapCounts(_started.Count, _resumed.Count);
        }

        ReportState(state, reason);
    }

    private static int Identify(Activity activity) => Java.Lang.JavaSystem.IdentityHashCode(activity);

    [LoggerMessage(EventId = 3951, Level = LogLevel.Warning, Message = "The Android Application object is not available; lifecycle events are not reported.")]
    private static partial void LogNoApplication(ILogger logger);

    private enum ActivityEvent
    {
        Created,
        Started,
        Resumed,
        Paused,
        Stopped,
        Destroyed,
    }

    private sealed class Callbacks : Java.Lang.Object, Application.IActivityLifecycleCallbacks, IComponentCallbacks2
    {
        private readonly WeakReference<AndroidAppLifecycle> _owner;

        public Callbacks(AndroidAppLifecycle owner) => _owner = new WeakReference<AndroidAppLifecycle>(owner);

        public void OnActivityCreated(Activity activity, Bundle? savedInstanceState) => Dispatch(activity, ActivityEvent.Created);

        public void OnActivityStarted(Activity activity) => Dispatch(activity, ActivityEvent.Started);

        public void OnActivityResumed(Activity activity) => Dispatch(activity, ActivityEvent.Resumed);

        public void OnActivityPaused(Activity activity) => Dispatch(activity, ActivityEvent.Paused);

        public void OnActivityStopped(Activity activity) => Dispatch(activity, ActivityEvent.Stopped);

        public void OnActivityDestroyed(Activity activity) => Dispatch(activity, ActivityEvent.Destroyed);

        public void OnActivitySaveInstanceState(Activity activity, Bundle outState)
        {
        }

        public void OnConfigurationChanged(Configuration newConfig)
        {
        }

        public void OnLowMemory()
        {
            if (_owner.TryGetTarget(out var owner))
            {
                owner.ReportMemoryPressure();
            }
        }

        public void OnTrimMemory(TrimMemory level)
        {
            if (AndroidLifecycleMapper.IsMemoryPressure((int)level) && _owner.TryGetTarget(out var owner))
            {
                owner.ReportMemoryPressure();
            }
        }

        private void Dispatch(Activity activity, ActivityEvent kind)
        {
            if (_owner.TryGetTarget(out var owner))
            {
                owner.OnActivityEvent(activity, kind);
            }
        }
    }
}
