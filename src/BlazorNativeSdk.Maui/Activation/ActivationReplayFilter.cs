namespace BlazorNativeSdk.Maui;

/// <summary>How a platform activation callback should be published.</summary>
internal enum ActivationDelivery
{
    /// <summary>Publish normally (the app was already running).</summary>
    Normal,

    /// <summary>Publish as the activation that launched the app.</summary>
    ColdStart,

    /// <summary>Skip: the platform replays an activation that was already published.</summary>
    Replay,
}

/// <summary>
/// Correlates launch information with the callbacks that deliver it. iOS reports a launch URL, user activity or quick action
/// in the launch options and then delivers it again through <c>openURL</c>, <c>continueUserActivity</c> or
/// <c>performActionForShortcutItem</c>; MAUI replays a scene's launch quick action when the scene activates. The filter marks
/// the matching callback as the cold start, or as a replay of something already published. Marks are consumed once and expire.
/// Thread-safe and pure.
/// </summary>
internal sealed class ActivationReplayFilter
{
    private readonly Lock _gate = new();
    private readonly List<Mark> _marks = [];
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _lifetime;

    /// <summary>Creates the filter.</summary>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="lifetime">How long a mark stays valid. Default: 10 seconds.</param>
    public ActivationReplayFilter(TimeProvider? timeProvider = null, TimeSpan? lifetime = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lifetime = lifetime ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>Marks the next delivery of <paramref name="kind"/> (with <paramref name="key"/>, or any key when <see langword="null"/>) as the cold start.</summary>
    public void ExpectColdStart(string kind, string? key) => Add(kind, key, ActivationDelivery.ColdStart);

    /// <summary>Marks the next delivery of <paramref name="kind"/> with <paramref name="key"/> as a replay to skip.</summary>
    public void SuppressReplay(string kind, string key) => Add(kind, key, ActivationDelivery.Replay);

    /// <summary>Classifies a delivery and consumes the mark that matched it.</summary>
    public ActivationDelivery Classify(string kind, string? key)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            _marks.RemoveAll(m => now - m.CreatedAt > _lifetime);
            for (var i = 0; i < _marks.Count; i++)
            {
                var mark = _marks[i];
                if (string.Equals(mark.Kind, kind, StringComparison.Ordinal) &&
                    (mark.Key is null || string.Equals(mark.Key, key, StringComparison.Ordinal)))
                {
                    _marks.RemoveAt(i);
                    return mark.Delivery;
                }
            }
        }

        return ActivationDelivery.Normal;
    }

    private void Add(string kind, string? key, ActivationDelivery delivery)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        lock (_gate)
        {
            _marks.Add(new Mark(kind, key, delivery, _timeProvider.GetUtcNow()));
        }
    }

    private sealed record Mark(string Kind, string? Key, ActivationDelivery Delivery, DateTimeOffset CreatedAt);
}
