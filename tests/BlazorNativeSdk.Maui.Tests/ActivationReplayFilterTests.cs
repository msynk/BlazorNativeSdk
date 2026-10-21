using Microsoft.Extensions.Time.Testing;

namespace BlazorNativeSdk.Maui.Tests;

/// <summary>
/// docs/adapters/maui.md: iOS delivers launch-option URLs, activities and quick actions again through the delegate callbacks,
/// and MAUI replays a scene's launch quick action; the integration publishes each one once and flags the right one as the cold
/// start. The kinds and keys below mirror AppleLifecycleWiring.
/// </summary>
public sealed class ActivationReplayFilterTests
{
    private const string Url = "url";
    private const string Activity = "activity";
    private const string Shortcut = "shortcut";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Deliveries_without_a_mark_are_normal() =>
        Assert.Equal(ActivationDelivery.Normal, new ActivationReplayFilter(_time).Classify(Url, "contoso://orders/42"));

    [Fact]
    public void The_launch_URL_is_the_cold_start_once()
    {
        var filter = new ActivationReplayFilter(_time);
        filter.ExpectColdStart(Url, "contoso://orders/42");

        Assert.Equal(ActivationDelivery.ColdStart, filter.Classify(Url, "contoso://orders/42"));
        Assert.Equal(ActivationDelivery.Normal, filter.Classify(Url, "contoso://orders/42"));   // the user opens the link again
    }

    [Fact]
    public void A_scene_launch_quick_action_replayed_by_MAUI_is_skipped_once()
    {
        var filter = new ActivationReplayFilter(_time);
        filter.SuppressReplay(Shortcut, "com.contoso.new-order");

        Assert.Equal(ActivationDelivery.Replay, filter.Classify(Shortcut, "com.contoso.new-order"));
        Assert.Equal(ActivationDelivery.Normal, filter.Classify(Shortcut, "com.contoso.new-order"));
    }

    [Fact]
    public void Marks_only_match_their_kind_and_key()
    {
        var filter = new ActivationReplayFilter(_time);
        filter.ExpectColdStart(Url, "contoso://a");

        Assert.Equal(ActivationDelivery.Normal, filter.Classify(Url, "contoso://b"));
        Assert.Equal(ActivationDelivery.Normal, filter.Classify(Shortcut, "contoso://a"));
        Assert.Equal(ActivationDelivery.Normal, filter.Classify(Url, null));
        Assert.Equal(ActivationDelivery.Normal, filter.Classify("URL", "contoso://a"));
        Assert.Equal(ActivationDelivery.Normal, filter.Classify(Url, "CONTOSO://A"));

        // Non-matching deliveries do not consume the mark.
        Assert.Equal(ActivationDelivery.ColdStart, filter.Classify(Url, "contoso://a"));
    }

    [Theory]
    [InlineData("NSUserActivityTypeBrowsingWeb|https://contoso.com/orders/42")]
    [InlineData(null)]
    public void A_mark_without_a_key_matches_any_delivery_of_its_kind(string? key)
    {
        var filter = new ActivationReplayFilter(_time);
        filter.ExpectColdStart(Activity, null);   // the launch options did not name the activity type

        Assert.Equal(ActivationDelivery.ColdStart, filter.Classify(Activity, key));
        Assert.Equal(ActivationDelivery.Normal, filter.Classify(Activity, key));
    }

    [Fact]
    public void A_launch_user_activity_matches_on_its_type_after_the_full_key_missed()
    {
        // HandleActivity classifies by "type|url" first, then by the type alone (launch options only carry the type).
        var filter = new ActivationReplayFilter(_time);
        filter.ExpectColdStart(Activity, "NSUserActivityTypeBrowsingWeb");

        Assert.Equal(ActivationDelivery.Normal, filter.Classify(Activity, "NSUserActivityTypeBrowsingWeb|https://contoso.com/orders/42"));
        Assert.Equal(ActivationDelivery.ColdStart, filter.Classify(Activity, "NSUserActivityTypeBrowsingWeb"));
    }

    [Fact]
    public void Marks_are_consumed_in_the_order_they_were_added()
    {
        var filter = new ActivationReplayFilter(_time);
        filter.ExpectColdStart(Url, "contoso://a");
        filter.SuppressReplay(Url, "contoso://a");

        Assert.Equal(ActivationDelivery.ColdStart, filter.Classify(Url, "contoso://a"));
        Assert.Equal(ActivationDelivery.Replay, filter.Classify(Url, "contoso://a"));
        Assert.Equal(ActivationDelivery.Normal, filter.Classify(Url, "contoso://a"));
    }

    [Fact]
    public void Marks_expire_after_ten_seconds_by_default()
    {
        var filter = new ActivationReplayFilter(_time);
        filter.ExpectColdStart(Url, "contoso://a");
        filter.SuppressReplay(Shortcut, "s");

        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(ActivationDelivery.ColdStart, filter.Classify(Url, "contoso://a"));   // exactly at the limit: still valid

        _time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(ActivationDelivery.Normal, filter.Classify(Shortcut, "s"));
    }

    [Fact]
    public void The_lifetime_is_configurable()
    {
        var filter = new ActivationReplayFilter(_time, TimeSpan.FromSeconds(2));
        filter.SuppressReplay(Shortcut, "s");

        _time.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(ActivationDelivery.Normal, filter.Classify(Shortcut, "s"));
    }

    [Fact]
    public void Expired_marks_never_match_later_deliveries()
    {
        var filter = new ActivationReplayFilter(_time);
        filter.ExpectColdStart(Url, "contoso://a");
        _time.Advance(TimeSpan.FromMinutes(1));
        filter.SuppressReplay(Url, "contoso://a");

        Assert.Equal(ActivationDelivery.Replay, filter.Classify(Url, "contoso://a"));
        Assert.Equal(ActivationDelivery.Normal, filter.Classify(Url, "contoso://a"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_kind_is_required(string? kind)
    {
        var filter = new ActivationReplayFilter(_time);

        Assert.ThrowsAny<ArgumentException>(() => filter.ExpectColdStart(kind!, "k"));
        Assert.ThrowsAny<ArgumentException>(() => filter.SuppressReplay(kind!, "k"));
    }

    [Fact]
    public void Each_mark_is_consumed_exactly_once_under_concurrency()
    {
        const int marks = 500;
        var filter = new ActivationReplayFilter(_time);
        for (var i = 0; i < marks; i++)
        {
            filter.SuppressReplay(Shortcut, "s");
        }

        var results = new ActivationDelivery[marks * 2];
        Parallel.For(0, results.Length, i => results[i] = filter.Classify(Shortcut, "s"));

        Assert.Equal(marks, results.Count(r => r == ActivationDelivery.Replay));
        Assert.Equal(marks, results.Count(r => r == ActivationDelivery.Normal));
    }
}
