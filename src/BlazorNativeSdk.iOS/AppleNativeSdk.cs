using Microsoft.Extensions.DependencyInjection;
using UserNotifications;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>
/// Static entry points that must run while the app finishes launching, before the dependency injection container exists.
/// </summary>
/// <remarks>
/// Call <see cref="Initialize()"/> from <c>AppDelegate.FinishedLaunching</c> (before calling the base implementation in a MAUI
/// app), or from the MAUI <c>ConfigureLifecycleEvents(... ios.FinishedLaunching(...))</c> handler. It:
/// <list type="bullet">
/// <item>installs <see cref="NativeSdkNotificationCenterDelegate"/>, which iOS requires before launch completes to deliver the
/// notification tap that launched the app;</item>
/// <item>registers the <c>BGTaskScheduler</c> launch handlers, which iOS only accepts before launch completes (registering later
/// terminates the app, so the adapter never does).</item>
/// </list>
/// </remarks>
public static class AppleNativeSdk
{
    private static readonly Lock s_gate = new();
    private static NativeSdkNotificationCenterDelegate? s_notificationDelegate;
    private static bool s_initialized;

    /// <summary>The default <c>BGAppRefreshTask</c> identifier: <c>{CFBundleIdentifier}.bns.refresh</c>.</summary>
    public static string DefaultRefreshTaskIdentifier => AppleInfoPlist.BundleIdentifier + ".bns.refresh";

    /// <summary>The default <c>BGProcessingTask</c> identifier: <c>{CFBundleIdentifier}.bns.processing</c>.</summary>
    public static string DefaultProcessingTaskIdentifier => AppleInfoPlist.BundleIdentifier + ".bns.processing";

    /// <summary><see langword="true"/> once <see cref="Initialize()"/> ran.</summary>
    public static bool IsInitialized
    {
        get
        {
            lock (s_gate)
            {
                return s_initialized;
            }
        }
    }

    /// <summary>The notification center delegate the SDK installs.</summary>
    public static NativeSdkNotificationCenterDelegate NotificationDelegate
    {
        get
        {
            lock (s_gate)
            {
                return s_notificationDelegate ??= new NativeSdkNotificationCenterDelegate();
            }
        }
    }

    /// <summary>Initializes the SDK with the default background task identifiers. Idempotent.</summary>
    public static void Initialize() => Initialize(null, null);

    /// <summary>Initializes the SDK with custom background task identifiers. Only the first call has an effect.</summary>
    /// <param name="refreshTaskIdentifier">The <c>BGAppRefreshTask</c> identifier; <see langword="null"/> uses <see cref="DefaultRefreshTaskIdentifier"/>.</param>
    /// <param name="processingTaskIdentifier">The <c>BGProcessingTask</c> identifier; <see langword="null"/> uses <see cref="DefaultProcessingTaskIdentifier"/>.</param>
    /// <remarks>
    /// Identifiers that are not listed in Info.plist <c>BGTaskSchedulerPermittedIdentifiers</c> are skipped (registering them
    /// would terminate the app); the background work scheduler then reports <see cref="NativeErrorCodes.MissingDeclaration"/>.
    /// </remarks>
    public static void Initialize(string? refreshTaskIdentifier, string? processingTaskIdentifier)
    {
        lock (s_gate)
        {
            if (s_initialized)
            {
                return;
            }

            s_initialized = true;
        }

        AppleLaunchTracker.Start(duringLaunch: true);
        EnsureNotificationDelegate();
        AppleBackgroundTasks.Register(
            string.IsNullOrWhiteSpace(refreshTaskIdentifier) ? DefaultRefreshTaskIdentifier : refreshTaskIdentifier,
            string.IsNullOrWhiteSpace(processingTaskIdentifier) ? DefaultProcessingTaskIdentifier : processingTaskIdentifier);
    }

    /// <summary>
    /// Makes the service provider available to native callbacks immediately and starts the SDK runtime. The MAUI integration and
    /// <c>NativeSdkRuntime</c> do this automatically; call it yourself in hosts that build the container without starting the SDK,
    /// so that background launches (<c>BGTaskScheduler</c>) and buffered activations are processed.
    /// </summary>
    public static void AttachServices(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        AppleServices.Attach(services);
        services.GetService<NativeSdkRuntime>()?.EnsureStarted();
    }

    /// <summary>
    /// Installs the SDK notification delegate unless another delegate is already installed (which is left untouched).
    /// </summary>
    /// <returns><see langword="true"/> when the SDK delegate is installed.</returns>
    internal static bool EnsureNotificationDelegate()
    {
        var center = UNUserNotificationCenter.Current;
        switch (center.Delegate)
        {
            case NativeSdkNotificationCenterDelegate:
                return true;
            case { } other:
                AppleLog.ForeignNotificationDelegate(AppleServices.Logger, other.GetType().FullName ?? other.GetType().Name);
                return false;
            default:
                center.Delegate = NotificationDelegate;
                return true;
        }
    }
}
