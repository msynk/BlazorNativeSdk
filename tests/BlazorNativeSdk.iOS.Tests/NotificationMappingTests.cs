using BlazorNativeSdk.Devices;

namespace BlazorNativeSdk.ApplePlatform.Tests;

public sealed class NotificationMappingTests
{
    private static readonly TimeZoneInfo s_plusTwo = TimeZoneInfo.CreateCustomTimeZone("Test/PlusTwo", TimeSpan.FromHours(2), "Test +2", "Test +2");

    [Fact]
    public void SDK_fields_round_trip_through_userInfo()
    {
        var notification = new LocalNotification
        {
            Id = "n1",
            Title = "t",
            Route = "/orders/42",
            Data = new Dictionary<string, string> { ["k"] = "v", ["aps"] = "kept for SDK payloads" },
        };

        var userInfo = AppleNotificationMapping.EncodeUserInfo(notification);
        var decoded = AppleNotificationMapping.Decode(userInfo, "request-id");

        Assert.Equal("n1", decoded.Id);
        Assert.Equal("/orders/42", decoded.Route);
        Assert.Equal(notification.Data, decoded.Data);
    }

    [Fact]
    public void Optional_fields_are_omitted()
    {
        var userInfo = AppleNotificationMapping.EncodeUserInfo(new LocalNotification { Id = "n1", Title = "t", Route = string.Empty });

        Assert.Equal([AppleNotificationMapping.IdKey], userInfo.Keys);
    }

    [Fact]
    public void SDK_payloads_without_data_have_no_data()
    {
        var decoded = AppleNotificationMapping.Decode(
            new Dictionary<string, string> { [AppleNotificationMapping.IdKey] = "n1", ["extra"] = "x" }, "request-id");

        Assert.Empty(decoded.Data);
        Assert.Null(decoded.Route);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    public void Corrupt_data_decodes_to_empty(string json)
    {
        var decoded = AppleNotificationMapping.Decode(
            new Dictionary<string, string> { [AppleNotificationMapping.IdKey] = "n1", [AppleNotificationMapping.DataKey] = json }, "request-id");

        Assert.Empty(decoded.Data);
    }

    [Fact]
    public void Foreign_notifications_keep_their_request_identifier_and_expose_top_level_values()
    {
        var decoded = AppleNotificationMapping.Decode(
            new Dictionary<string, string> { ["aps"] = "{}", ["orderId"] = "42", ["bns.route"] = "/admin", ["bns.other"] = "x" },
            "push-1");

        Assert.Equal("push-1", decoded.Id);
        Assert.Equal("/admin", decoded.Route);
        Assert.Equal(new Dictionary<string, string> { ["orderId"] = "42" }, decoded.Data);
    }

    [Fact]
    public void An_empty_stored_id_falls_back_to_the_request_identifier() =>
        Assert.Equal("request-id", AppleNotificationMapping.Decode(new Dictionary<string, string> { [AppleNotificationMapping.IdKey] = string.Empty }, "request-id").Id);

    [Fact]
    public void Notifications_without_actions_have_no_category() =>
        Assert.Null(AppleNotificationMapping.GetCategoryId([]));

    [Fact]
    public void Identical_action_sets_share_a_stable_category()
    {
        NotificationAction[] actions = [new("reply", "Reply") { AcceptsText = true, TextPlaceholder = "Message" }, new("delete", "Delete") { IsDestructive = true }];

        var category = AppleNotificationMapping.GetCategoryId(actions);

        Assert.NotNull(category);
        Assert.Matches("^bns\\.actions\\.[0-9a-f]{16}$", category);
        Assert.Equal(category, AppleNotificationMapping.GetCategoryId([.. actions.Select(a => a with { })]));
    }

    public static TheoryData<NotificationAction> ChangedActions => new()
    {
        new NotificationAction("other", "Reply"),
        new NotificationAction("reply", "Answer"),
        new NotificationAction("reply", "Reply") { OpensApp = false },
        new NotificationAction("reply", "Reply") { IsDestructive = true },
        new NotificationAction("reply", "Reply") { AcceptsText = true },
        new NotificationAction("reply", "Reply") { TextPlaceholder = "Message" },
    };

    [Theory]
    [MemberData(nameof(ChangedActions))]
    public void Any_action_difference_changes_the_category(NotificationAction changed) =>
        Assert.NotEqual(AppleNotificationMapping.GetCategoryId([new NotificationAction("reply", "Reply")]), AppleNotificationMapping.GetCategoryId([changed]));

    [Fact]
    public void Action_order_and_field_boundaries_matter()
    {
        var a = new NotificationAction("a", "A");
        var b = new NotificationAction("b", "B");

        Assert.NotEqual(AppleNotificationMapping.GetCategoryId([a, b]), AppleNotificationMapping.GetCategoryId([b, a]));
        Assert.NotEqual(
            AppleNotificationMapping.GetCategoryId([new NotificationAction("ab", "c")]),
            AppleNotificationMapping.GetCategoryId([new NotificationAction("a", "bc")]));
    }

    [Theory]
    [InlineData(-3600, NotificationRepeat.None, true)]
    [InlineData(0, NotificationRepeat.None, true)]
    [InlineData(0.5, NotificationRepeat.None, true)]
    [InlineData(1, NotificationRepeat.None, false)]
    [InlineData(60, NotificationRepeat.None, false)]
    [InlineData(-3600, NotificationRepeat.Daily, false)]
    [InlineData(-3600, NotificationRepeat.Weekly, false)]
    public void One_time_notifications_due_within_a_second_are_delivered_immediately(double secondsFromNow, NotificationRepeat repeat, bool expected)
    {
        var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        var schedule = new NotificationSchedule { DeliverAt = now.AddSeconds(secondsFromNow), Repeat = repeat };

        Assert.Equal(expected, AppleNotificationMapping.IsImmediate(schedule, now));
    }

    [Fact]
    public void One_time_triggers_use_the_full_local_date_and_time()
    {
        var components = AppleNotificationMapping.GetCalendarComponents(Schedule(NotificationRepeat.None), s_plusTwo);

        Assert.Equal(new NotificationCalendarComponents(2026, 10, 25, null, 0, 30, 15, false), components);
    }

    [Fact]
    public void Daily_triggers_match_the_local_time_of_day()
    {
        var components = AppleNotificationMapping.GetCalendarComponents(Schedule(NotificationRepeat.Daily), s_plusTwo);

        Assert.Equal(new NotificationCalendarComponents(null, null, null, null, 0, 30, 15, true), components);
    }

    [Fact]
    public void Weekly_triggers_match_the_local_weekday_and_time()
    {
        // 2026-10-24 22:30:15 UTC is Sunday 2026-10-25 00:30:15 at UTC+2; NSCalendar weekdays start at 1 = Sunday.
        var components = AppleNotificationMapping.GetCalendarComponents(Schedule(NotificationRepeat.Weekly), s_plusTwo);

        Assert.Equal(new NotificationCalendarComponents(null, null, null, 1, 0, 30, 15, true), components);
    }

    private static NotificationSchedule Schedule(NotificationRepeat repeat) =>
        new() { DeliverAt = new DateTimeOffset(2026, 10, 24, 22, 30, 15, TimeSpan.Zero), Repeat = repeat };
}
