namespace BlazorNativeSdk.Maui;

/// <summary>Which intent of a MAUI activity <see cref="AndroidLaunchTracker"/> treats as the cold start.</summary>
internal enum AndroidLaunchKind
{
    /// <summary>Not the first activity of the process: handle the launch intent as a normal activation.</summary>
    Normal,

    /// <summary>The first activity of the process started fresh: its launch intent is the cold start.</summary>
    ColdStart,

    /// <summary>
    /// The first activity of the process was re-created from saved state after the OS killed the process. Its launch intent
    /// must not be replayed; the cold start is the next intent delivered before the activity resumes, if any.
    /// </summary>
    Restored,
}

/// <summary>
/// Decides which Android intent launched the process (the cold start). Pure, so it can be unit tested.
/// </summary>
/// <remarks>
/// When the OS killed the process while an activity was in the background and the user then taps a notification or opens a
/// link, Android re-creates the activity from its saved state (whose launch intent is the old one) and delivers the new
/// intent to <c>OnNewIntent</c> before the activity resumes. That intent started the process, so it is the cold start. A
/// restored activity that resumes without a new intent was reopened from recents: the process launch is a plain cold start.
/// </remarks>
internal sealed class AndroidLaunchTracker
{
    private const int NoActivity = 0;
    private const int AwaitingRestoredIntent = 1;
    private const int Claimed = 2;
    private int _state;

    /// <summary>Called from <c>OnCreate</c> of a MAUI activity.</summary>
    /// <param name="restored">Whether the activity has saved state.</param>
    public AndroidLaunchKind OnCreate(bool restored)
    {
        if (Interlocked.CompareExchange(ref _state, restored ? AwaitingRestoredIntent : Claimed, NoActivity) != NoActivity)
        {
            return AndroidLaunchKind.Normal;
        }

        return restored ? AndroidLaunchKind.Restored : AndroidLaunchKind.ColdStart;
    }

    /// <summary>Called from <c>OnNewIntent</c>; returns <see langword="true"/> when the intent re-created a restored first activity.</summary>
    public bool OnNewIntent() => TryClaimRestored();

    /// <summary>
    /// Called from <c>OnResume</c>; returns <see langword="true"/> when a restored first activity resumed without a new
    /// intent, so a plain cold-start launch is still to be published.
    /// </summary>
    public bool OnResume() => TryClaimRestored();

    private bool TryClaimRestored() => Interlocked.CompareExchange(ref _state, Claimed, AwaitingRestoredIntent) == AwaitingRestoredIntent;
}
