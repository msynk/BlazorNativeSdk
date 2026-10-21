using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.LifecycleEvents;
#if IOS || MACCATALYST
using BlazorNativeSdk.ApplePlatform;
using Microsoft.Extensions.Options;
#endif

namespace BlazorNativeSdk.Maui;

/// <summary>
/// Per-app state shared between the MAUI lifecycle handlers (registered while the app is configured, before the service
/// provider exists) and the services. The initializer attaches the root service provider while the MAUI app is built, which
/// happens before any lifecycle handler that needs it runs.
/// </summary>
internal sealed class MauiIntegration
{
    private IServiceProvider? _services;
    private MauiWindowTracker? _tracker;
    private int _firstSceneClaimed;

    /// <summary>The window tracker, once the service provider is attached.</summary>
    public MauiWindowTracker? Tracker => Volatile.Read(ref _tracker);

    /// <summary>Correlates iOS launch options with the callbacks that deliver them.</summary>
    public ActivationReplayFilter ReplayFilter { get; } = new();

    /// <summary>Finds the intent that launched the process (Android).</summary>
    public AndroidLaunchTracker AndroidLaunches { get; } = new();

    /// <summary>Attaches the application's root service provider. Called on the UI thread while the MAUI app is built.</summary>
    public void Attach(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Volatile.Write(ref _services, services);
        services.GetRequiredService<MauiUIDispatcher>().CaptureCurrentThread();
        var tracker = services.GetRequiredService<MauiWindowTracker>();
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacCatalyst())
        {
            // Desktop targets always track windows (window manager, host bridge); phones only for the fallback lifecycle.
            tracker.Enable();
        }

        Volatile.Write(ref _tracker, tracker);
    }

    /// <summary>Returns <see langword="true"/> once per process: for the first scene that connects (iOS / Mac Catalyst).</summary>
    public bool TryClaimFirstScene() => Interlocked.Exchange(ref _firstSceneClaimed, 1) == 0;

    /// <summary>Registers the platform lifecycle handlers for the current target.</summary>
    public void ConfigureLifecycleEvents(ILifecycleBuilder events)
    {
#if ANDROID
        events.AddAndroid(android => AndroidLifecycleWiring.Configure(android, this));
#elif IOS || MACCATALYST
        events.AddiOS(ios => AppleLifecycleWiring.Configure(ios, this));
#elif WINDOWS
        events.AddWindows(windows => WindowsLifecycleWiring.Configure(windows, this));
#else
        _ = events;
#endif
    }

#if IOS || MACCATALYST
    /// <summary>
    /// Initializes the Apple adapter with the background task identifiers from <see cref="AppleNativeOptions"/>: installs the
    /// notification delegate and registers the BGTaskScheduler launch handlers. Must run before launch completes; idempotent.
    /// </summary>
    public void InitializeAppleSdk()
    {
        var options = Volatile.Read(ref _services)?.GetService<IOptions<AppleNativeOptions>>()?.Value;
        AppleNativeSdk.Initialize(options?.RefreshTaskIdentifier, options?.ProcessingTaskIdentifier);
    }
#endif
}
