using System.Text.Json;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;

namespace BlazorNativeSdk.AndroidPlatform.Tests;

public sealed class NotificationExtrasTests
{
    [Fact]
    public void Data_round_trips()
    {
        var data = new Dictionary<string, string> { ["orderId"] = "42", ["note"] = "ä \"quoted\" \u001F" };

        Assert.Equal(data, NotificationExtras.DecodeData(NotificationExtras.EncodeData(data)));
    }

    [Fact]
    public void Read_only_data_is_encoded_too()
    {
        IReadOnlyDictionary<string, string> data = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string> { ["k"] = "v" });

        Assert.Equal("{\"k\":\"v\"}", NotificationExtras.EncodeData(data));
    }

    [Fact]
    public void Missing_or_empty_data_is_omitted()
    {
        Assert.Null(NotificationExtras.EncodeData(null));
        Assert.Null(NotificationExtras.EncodeData(new Dictionary<string, string>()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"k\":1}")]
    public void Missing_or_corrupt_data_decodes_to_empty(string? json) =>
        Assert.Empty(NotificationExtras.DecodeData(json));

    [Fact]
    public void Creates_notification_activations()
    {
        var activation = NotificationExtras.CreateActivation("n1", "reply", "/orders/42", "{\"k\":\"v\"}", "on my way", isColdStart: true);

        Assert.NotNull(activation);
        Assert.Equal(ActivationKind.Notification, activation.Kind);
        Assert.Equal("n1", activation.NotificationId);
        Assert.Equal("reply", activation.ActionId);
        Assert.Equal("/orders/42", activation.Route);
        Assert.Equal("v", activation.Data["k"]);
        Assert.Equal("on my way", activation.UserText);
        Assert.True(activation.IsColdStart);
        Assert.Equal("android", activation.Source);
    }

    [Fact]
    public void A_plain_tap_has_no_action_and_blank_routes_are_dropped()
    {
        var activation = NotificationExtras.CreateActivation("n1", string.Empty, "  ", null, null, isColdStart: false);

        Assert.NotNull(activation);
        Assert.Null(activation.ActionId);
        Assert.Null(activation.Route);
        Assert.Empty(activation.Data);
        Assert.False(activation.IsColdStart);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Intents_without_a_notification_id_are_not_activations(string? id) =>
        Assert.Null(NotificationExtras.CreateActivation(id, "a", "/r", null, null, isColdStart: false));

    [Theory]
    [InlineData(NotificationImportance.Low, 2, -1)]      // IMPORTANCE_LOW, PRIORITY_LOW
    [InlineData(NotificationImportance.Default, 3, 0)]   // IMPORTANCE_DEFAULT, PRIORITY_DEFAULT
    [InlineData(NotificationImportance.High, 4, 1)]      // IMPORTANCE_HIGH, PRIORITY_HIGH
    public void Maps_importance_to_channel_importance_and_priority(NotificationImportance importance, int channelImportance, int priority)
    {
        Assert.Equal(channelImportance, NotificationExtras.ToChannelImportance(importance));
        Assert.Equal(priority, NotificationExtras.ToPriority(importance));
    }

    [Fact]
    public void Activation_extras_share_the_signed_prefix()
    {
        // NotificationIntentSigner documents the forged-extras threat for "bns.notification.*".
        Assert.All(
            [NotificationExtras.NotificationId, NotificationExtras.Route, NotificationExtras.Data, NotificationExtras.ActionId],
            key => Assert.StartsWith("bns.notification.", key, StringComparison.Ordinal));
    }
}

/// <summary>
/// Scheduled notifications keep their local time across DST and time-zone changes (docs/adapters/android.md). Uses a custom
/// US-Eastern-like zone so results do not depend on the host's time-zone database: DST 2026-03-08 02:00 to 2026-11-01 02:00.
/// </summary>
public sealed class NotificationScheduleCalculatorTests
{
    private static readonly TimeZoneInfo s_eastern = TimeZoneInfo.CreateCustomTimeZone(
        "Test/Eastern",
        TimeSpan.FromHours(-5),
        "Test Eastern",
        "Test Eastern Standard",
        "Test Eastern Daylight",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date,
                DateTime.MaxValue.Date,
                TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday)),
        ]);

    private static readonly TimeZoneInfo s_plusOne = TimeZoneInfo.CreateCustomTimeZone("Test/PlusOne", TimeSpan.FromHours(1), "Test +1", "Test +1");

    [Fact]
    public void One_shot_notifications_are_delivered_at_their_time()
    {
        var at = Eastern(2026, 10, 22, 9, 0);

        Assert.Equal(at, NotificationScheduleCalculator.GetFirstDelivery(Schedule(at), Eastern(2026, 10, 21, 12, 0), s_eastern));
    }

    [Fact]
    public void Past_one_shot_notifications_are_delivered_immediately()
    {
        var at = Eastern(2026, 10, 20, 9, 0);

        // A time in the past: the alarm fires right away.
        Assert.Equal(at, NotificationScheduleCalculator.GetFirstDelivery(Schedule(at), Eastern(2026, 10, 21, 12, 0), s_eastern));
    }

    [Fact]
    public void Future_repeats_start_at_their_first_time()
    {
        var at = Eastern(2026, 10, 25, 7, 15);

        Assert.Equal(at, NotificationScheduleCalculator.GetFirstDelivery(Schedule(at, NotificationRepeat.Daily), Eastern(2026, 10, 21, 12, 0), s_eastern));
    }

    [Fact]
    public void Past_daily_repeats_start_at_the_next_occurrence_of_their_local_time()
    {
        // Anchored in winter time, next occurrence in summer time: still 09:00 local.
        var first = NotificationScheduleCalculator.GetFirstDelivery(
            Schedule(Eastern(2026, 3, 1, 9, 0), NotificationRepeat.Daily), Eastern(2026, 3, 10, 12, 0), s_eastern);

        Assert.Equal(new DateTimeOffset(2026, 3, 11, 9, 0, 0, TimeSpan.FromHours(-4)), first);
    }

    [Fact]
    public void Daily_repeats_keep_their_local_time_across_spring_forward()
    {
        var next = NotificationScheduleCalculator.GetNextDelivery(
            NotificationRepeat.Daily, new DateTime(2026, 3, 7, 9, 0, 0), Eastern(2026, 3, 7, 9, 0), s_eastern);

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 9, 0, 0, TimeSpan.FromHours(-4)), next);
    }

    [Fact]
    public void Weekly_repeats_keep_their_weekday_and_local_time_across_fall_back()
    {
        var anchor = new DateTime(2026, 10, 5, 8, 30, 0);   // a Monday

        Assert.Equal(
            new DateTimeOffset(2026, 10, 26, 8, 30, 0, TimeSpan.FromHours(-4)),
            NotificationScheduleCalculator.GetNextDelivery(NotificationRepeat.Weekly, anchor, Eastern(2026, 10, 21, 12, 0), s_eastern));
        Assert.Equal(
            new DateTimeOffset(2026, 11, 2, 8, 30, 0, TimeSpan.FromHours(-5)),
            NotificationScheduleCalculator.GetNextDelivery(NotificationRepeat.Weekly, anchor, Eastern(2026, 10, 27, 12, 0), s_eastern));
    }

    [Fact]
    public void The_next_occurrence_is_strictly_after_the_reference_time()
    {
        var anchor = new DateTime(2026, 10, 21, 9, 0, 0);

        Assert.Equal(
            Eastern(2026, 10, 22, 9, 0),
            NotificationScheduleCalculator.GetNextDelivery(NotificationRepeat.Daily, anchor, Eastern(2026, 10, 21, 9, 0), s_eastern));
        Assert.Equal(
            Eastern(2026, 10, 21, 9, 0),
            NotificationScheduleCalculator.GetNextDelivery(NotificationRepeat.Daily, anchor, Eastern(2026, 10, 21, 8, 59), s_eastern));
    }

    [Fact]
    public void One_shot_schedules_have_no_next_occurrence() =>
        Assert.Null(NotificationScheduleCalculator.GetNextDelivery(NotificationRepeat.None, new DateTime(2026, 1, 1), DateTimeOffset.UnixEpoch, s_eastern));

    [Fact]
    public void Times_in_the_spring_forward_gap_move_to_the_first_valid_time()
    {
        // 02:30 does not exist on 2026-03-08; the alarm fires at 03:00 EDT.
        var next = NotificationScheduleCalculator.GetNextDelivery(
            NotificationRepeat.Daily, new DateTime(2026, 3, 1, 2, 30, 0), Eastern(2026, 3, 7, 12, 0), s_eastern);

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 3, 0, 0, TimeSpan.FromHours(-4)), next);
    }

    [Fact]
    public void Ambiguous_fall_back_times_use_the_earlier_instant()
    {
        // 01:30 happens twice on 2026-11-01; the first one is 01:30 EDT (05:30 UTC).
        var next = NotificationScheduleCalculator.GetNextDelivery(
            NotificationRepeat.Daily, new DateTime(2026, 10, 1, 1, 30, 0), Eastern(2026, 10, 31, 12, 0), s_eastern);

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void The_anchor_is_the_local_wall_clock_time()
    {
        var anchor = NotificationScheduleCalculator.GetAnchor(new DateTimeOffset(2026, 7, 4, 13, 0, 0, TimeSpan.Zero), s_eastern);

        Assert.Equal(new DateTime(2026, 7, 4, 9, 0, 0), anchor);
        Assert.Equal(DateTimeKind.Unspecified, anchor.Kind);
    }

    [Fact]
    public void Missed_one_shot_notifications_are_delivered_immediately_when_rearmed()
    {
        var missed = Eastern(2026, 10, 1, 9, 0);
        var record = Record(NotificationRepeat.None, missed, new DateTime(2026, 10, 1, 9, 0, 0));

        Assert.Equal(missed, NotificationScheduleCalculator.GetRearmTime(record, Eastern(2026, 10, 21, 12, 0), s_eastern));
    }

    [Fact]
    public void Missed_repeats_skip_to_their_next_occurrence_when_rearmed()
    {
        var record = Record(NotificationRepeat.Daily, Eastern(2026, 10, 1, 9, 0), new DateTime(2026, 10, 1, 9, 0, 0));

        Assert.Equal(Eastern(2026, 10, 22, 9, 0), NotificationScheduleCalculator.GetRearmTime(record, Eastern(2026, 10, 21, 12, 0), s_eastern));
    }

    [Fact]
    public void Repeats_keep_their_local_time_after_a_time_zone_change()
    {
        var record = Record(NotificationRepeat.Daily, Eastern(2026, 10, 21, 9, 0), new DateTime(2026, 10, 21, 9, 0, 0));

        var rearmed = NotificationScheduleCalculator.GetRearmTime(record, new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero), s_plusOne);

        Assert.Equal(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.FromHours(1)), rearmed);
    }

    private static DateTimeOffset Eastern(int year, int month, int day, int hour, int minute)
    {
        var local = new DateTime(year, month, day, hour, minute, 0);
        return new DateTimeOffset(local, s_eastern.GetUtcOffset(local));
    }

    private static NotificationSchedule Schedule(DateTimeOffset at, NotificationRepeat repeat = NotificationRepeat.None) =>
        new() { DeliverAt = at, Repeat = repeat };

    private static ScheduledNotificationRecord Record(NotificationRepeat repeat, DateTimeOffset nextFire, DateTime anchorLocal) => new()
    {
        Notification = new StoredNotification { Id = "n1", Title = "t" },
        Repeat = repeat,
        NextFireUnixMs = nextFire.ToUnixTimeMilliseconds(),
        AnchorLocal = anchorLocal,
    };
}

public sealed class NotificationPersistenceTests
{
    [Fact]
    public void Stored_notifications_copy_every_field()
    {
        var data = new Dictionary<string, string> { ["k"] = "v" };
        var notification = new LocalNotification
        {
            Id = "n1",
            Title = "Title",
            Body = "Body",
            Subtitle = "Sub",
            Route = "/orders/42",
            Data = data,
            Actions = [new NotificationAction("reply", "Reply") { OpensApp = false, AcceptsText = true, TextPlaceholder = "Message", IsDestructive = true }],
            ChannelId = "orders",
            Group = "g",
            Importance = NotificationImportance.High,
            BadgeNumber = 3,
            Silent = true,
        };

        var stored = StoredNotification.From(notification);
        data["k"] = "changed later";

        Assert.Equal(("n1", "Title", "Body", "Sub", "/orders/42"), (stored.Id, stored.Title, stored.Body, stored.Subtitle, stored.Route));
        Assert.Equal("v", stored.Data["k"]);
        Assert.Equal(("orders", "g", NotificationImportance.High, (int?)3, true), (stored.ChannelId, stored.Group, stored.Importance, stored.BadgeNumber, stored.Silent));
        var action = Assert.Single(stored.Actions);
        Assert.Equal(("reply", "Reply", false, true, "Message", true), (action.Id, action.Title, action.OpensApp, action.AcceptsText, action.TextPlaceholder, action.IsDestructive));
    }

    [Fact]
    public void Scheduled_records_round_trip_through_json()
    {
        var record = new ScheduledNotificationRecord
        {
            Notification = StoredNotification.From(new LocalNotification { Id = "n1", Title = "t", Importance = NotificationImportance.Low }),
            NextFireUnixMs = 1_760_000_000_000,
            Repeat = NotificationRepeat.Weekly,
            Exact = true,
            AnchorLocal = new DateTime(2026, 10, 5, 8, 30, 0),
        };

        var json = JsonSerializer.Serialize(record, AndroidJsonContext.Default.ScheduledNotificationRecord);
        var restored = AndroidJsonContext.TryDeserialize(json, AndroidJsonContext.Default.ScheduledNotificationRecord);

        Assert.Contains("\"repeat\":\"Weekly\"", json, StringComparison.Ordinal);
        Assert.Contains("\"importance\":\"Low\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"body\"", json, StringComparison.Ordinal);
        Assert.NotNull(restored);
        Assert.Equal(("n1", NotificationImportance.Low), (restored.Notification.Id, restored.Notification.Importance));
        Assert.Equal((record.NextFireUnixMs, record.Repeat, record.Exact, record.AnchorLocal), (restored.NextFireUnixMs, restored.Repeat, restored.Exact, restored.AnchorLocal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ corrupt")]
    public void Missing_or_corrupt_documents_deserialize_to_null(string? json) =>
        Assert.Null(AndroidJsonContext.TryDeserialize(json, AndroidJsonContext.Default.NotificationSettings));

    [Fact]
    public void Settings_capture_the_notification_options()
    {
        var settings = NotificationSettings.From(new AndroidNativeOptions
        {
            NotificationSmallIcon = "ic_stat_notify",
            DefaultChannelId = "general",
            DefaultChannelName = "General",
            DefaultChannelDescription = "Everything",
            DefaultChannelImportance = NotificationImportance.High,
        });

        Assert.Equal(
            ("ic_stat_notify", "general", "General", "Everything", NotificationImportance.High),
            (settings.SmallIcon, settings.DefaultChannelId, settings.DefaultChannelName, settings.DefaultChannelDescription, settings.DefaultChannelImportance));
    }

    [Fact]
    public void Settings_defaults_match_the_option_defaults()
    {
        var fromOptions = NotificationSettings.From(new AndroidNativeOptions());
        var defaults = new NotificationSettings();

        Assert.Equal((defaults.DefaultChannelId, defaults.DefaultChannelName), (fromOptions.DefaultChannelId, fromOptions.DefaultChannelName));
        Assert.Equal(("bns.default", "Notifications"), (defaults.DefaultChannelId, defaults.DefaultChannelName));
    }
}

public sealed class AndroidOptionsTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        var options = new AndroidNativeOptions();

        options.Validate();
        Assert.True(options.UseWorkManager);
        Assert.Equal(TimeSpan.FromSeconds(20), options.WorkerStartupTimeout);
    }

    [Theory]
    [InlineData("", "General", 1)]
    [InlineData(" ", "General", 1)]
    [InlineData("general", "", 1)]
    [InlineData("general", "General", -1)]
    public void Invalid_options_are_rejected(string channelId, string channelName, int startupTimeoutSeconds)
    {
        var options = new AndroidNativeOptions
        {
            DefaultChannelId = channelId,
            DefaultChannelName = channelName,
            WorkerStartupTimeout = TimeSpan.FromSeconds(startupTimeoutSeconds),
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
