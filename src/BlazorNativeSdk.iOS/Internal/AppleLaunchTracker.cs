using System.Diagnostics;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>
/// Decides whether a notification response launched the process. iOS does not say so explicitly for
/// <c>UNUserNotificationCenter</c> responses, so the first response that arrives before, or within a short window after,
/// the app first becomes active is treated as the cold-start activation.
/// </summary>
internal static class AppleLaunchTracker
{
    private static readonly TimeSpan s_coldStartWindow = TimeSpan.FromSeconds(3);
    private static readonly Lock s_gate = new();
    private static NSObject? s_activeObserver;
    private static long? s_firstActiveTimestamp;
    private static bool s_coldStartConsumed;
    private static bool s_started;

    /// <summary>Starts tracking. <paramref name="duringLaunch"/> is <see langword="false"/> when called after launch completed.</summary>
    public static void Start(bool duringLaunch)
    {
        lock (s_gate)
        {
            if (s_started)
            {
                return;
            }

            s_started = true;
            if (!duringLaunch)
            {
                // The app has been running for a while: later responses are never cold starts.
                s_firstActiveTimestamp = Stopwatch.GetTimestamp() - (long)(s_coldStartWindow.TotalSeconds * Stopwatch.Frequency * 2);
                return;
            }

            s_activeObserver = UIApplication.Notifications.ObserveDidBecomeActive(OnDidBecomeActive);
        }
    }

    /// <summary>Returns <see langword="true"/> once, for the activation that most likely launched the process.</summary>
    public static bool TryConsumeColdStart()
    {
        lock (s_gate)
        {
            if (s_coldStartConsumed)
            {
                return false;
            }

            s_coldStartConsumed = true;
            return s_firstActiveTimestamp is not { } first || Stopwatch.GetElapsedTime(first) <= s_coldStartWindow;
        }
    }

    /// <summary>Marks the cold start as consumed by another activation (deep link, shortcut).</summary>
    public static void MarkColdStartConsumed()
    {
        lock (s_gate)
        {
            s_coldStartConsumed = true;
        }
    }

    private static void OnDidBecomeActive(object? sender, NSNotificationEventArgs e)
    {
        NSObject? observer;
        lock (s_gate)
        {
            s_firstActiveTimestamp ??= Stopwatch.GetTimestamp();
            observer = s_activeObserver;
            s_activeObserver = null;
        }

        observer?.Dispose();
    }
}
