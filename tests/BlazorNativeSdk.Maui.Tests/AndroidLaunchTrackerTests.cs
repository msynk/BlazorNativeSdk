namespace BlazorNativeSdk.Maui.Tests;

/// <summary>
/// docs/adapters/maui.md: the cold start on Android is the first launch intent of the process or, when the OS killed the
/// process and the first activity is re-created from saved state, the intent that re-created it. The calls below mirror the
/// order Android uses: OnCreate, then pending new intents, then OnResume.
/// </summary>
public sealed class AndroidLaunchTrackerTests
{
    [Fact]
    public void The_first_fresh_activity_is_the_cold_start_once()
    {
        var tracker = new AndroidLaunchTracker();

        Assert.Equal(AndroidLaunchKind.ColdStart, tracker.OnCreate(restored: false));
        Assert.False(tracker.OnNewIntent());
        Assert.False(tracker.OnResume());
        Assert.Equal(AndroidLaunchKind.Normal, tracker.OnCreate(restored: false)); // a second activity in the same process
    }

    [Fact]
    public void A_restored_activity_takes_the_intent_that_re_created_it_as_the_cold_start()
    {
        var tracker = new AndroidLaunchTracker();

        Assert.Equal(AndroidLaunchKind.Restored, tracker.OnCreate(restored: true));
        Assert.True(tracker.OnNewIntent());   // the notification tap or link that started the process
        Assert.False(tracker.OnResume());     // already claimed: no extra plain launch
        Assert.False(tracker.OnNewIntent());  // later intents reach a running app
    }

    [Fact]
    public void A_restored_activity_resumed_without_an_intent_is_a_plain_cold_start()
    {
        var tracker = new AndroidLaunchTracker();

        Assert.Equal(AndroidLaunchKind.Restored, tracker.OnCreate(restored: true));
        Assert.True(tracker.OnResume());      // reopened from recents
        Assert.False(tracker.OnNewIntent());
        Assert.False(tracker.OnResume());
    }

    [Fact]
    public void Activities_restored_later_in_the_process_are_not_cold_starts()
    {
        var tracker = new AndroidLaunchTracker();
        tracker.OnCreate(restored: false);

        Assert.Equal(AndroidLaunchKind.Normal, tracker.OnCreate(restored: true));
        Assert.False(tracker.OnNewIntent());
        Assert.False(tracker.OnResume());
    }
}
