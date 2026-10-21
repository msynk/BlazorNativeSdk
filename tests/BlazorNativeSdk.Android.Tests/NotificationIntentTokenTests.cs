using System.Security.Cryptography;
using System.Text;

namespace BlazorNativeSdk.AndroidPlatform.Tests;

/// <summary>
/// The launcher activity is exported, so notification intents are authenticated with an HMAC-SHA256 token over id, action,
/// route and data (docs/adapters/android.md, "Security"). Forged or tampered extras must never verify.
/// </summary>
public sealed class NotificationIntentTokenTests
{
    private static readonly byte[] s_key = [.. Enumerable.Range(1, NotificationIntentToken.KeyLength).Select(i => (byte)i)];

    [Theory]
    [InlineData("n1", null, null, null)]
    [InlineData("n1", "reply", "/orders/42", "{\"k\":\"v\"}")]
    [InlineData("n1", "", "", "")]
    [InlineData("übung-✓", "ação", "/caf%C3%A9", "{\"emoji\":\"🎉\"}")]
    public void A_signed_intent_verifies(string id, string? action, string? route, string? data)
    {
        var token = NotificationIntentToken.Compute(s_key, id, action, route, data);

        Assert.True(NotificationIntentToken.Verify(s_key, token, id, action, route, data));
    }

    [Fact]
    public void The_token_is_a_base64_hmac_sha256_over_length_prefixed_fields()
    {
        // Pins the wire format: posted and scheduled notifications outlive app updates, so a format change would silently
        // invalidate every outstanding notification.
        var expected = Convert.ToBase64String(HMACSHA256.HashData(s_key, Encoding.UTF8.GetBytes("2:n1\u001F5:reply\u001F7:/orders\u001F-")));

        Assert.Equal(expected, NotificationIntentToken.Compute(s_key, "n1", "reply", "/orders", null));
        Assert.Equal(44, expected.Length);
    }

    public static TheoryData<string, string?, string?, string?> TamperedFields => new()
    {
        { "n2", "reply", "/orders/42", "{\"k\":\"v\"}" },          // other notification id
        { "n1", null, "/orders/42", "{\"k\":\"v\"}" },             // action removed (button press turned into a tap)
        { "n1", "delete", "/orders/42", "{\"k\":\"v\"}" },         // other action
        { "n1", "reply", "/admin", "{\"k\":\"v\"}" },              // route redirected
        { "n1", "reply", null, "{\"k\":\"v\"}" },                  // route removed
        { "n1", "reply", "/orders/42", "{\"k\":\"w\"}" },          // data changed
        { "n1", "reply", "/orders/42", null },                     // data removed
        { "N1", "reply", "/orders/42", "{\"k\":\"v\"}" },          // case matters
        { "n1", "reply", "/orders/42 ", "{\"k\":\"v\"}" },         // trailing whitespace matters
    };

    [Theory]
    [MemberData(nameof(TamperedFields))]
    public void Tampering_with_any_field_fails_verification(string id, string? action, string? route, string? data)
    {
        var token = NotificationIntentToken.Compute(s_key, "n1", "reply", "/orders/42", "{\"k\":\"v\"}");

        Assert.False(NotificationIntentToken.Verify(s_key, token, id, action, route, data));
    }

    [Fact]
    public void Adding_fields_to_an_unsigned_slot_fails_verification()
    {
        var token = NotificationIntentToken.Compute(s_key, "n1", null, null, null);

        Assert.False(NotificationIntentToken.Verify(s_key, token, "n1", "reply", null, null));
        Assert.False(NotificationIntentToken.Verify(s_key, token, "n1", null, "/admin", null));
        Assert.False(NotificationIntentToken.Verify(s_key, token, "n1", null, null, "{}"));
    }

    [Fact]
    public void Null_and_empty_fields_are_distinct()
    {
        Assert.NotEqual(
            NotificationIntentToken.Compute(s_key, "n1", null, null, null),
            NotificationIntentToken.Compute(s_key, "n1", string.Empty, null, null));
        Assert.NotEqual(
            NotificationIntentToken.Compute(s_key, "n1", null, null, null),
            NotificationIntentToken.Compute(s_key, "n1", null, null, "-"));
    }

    [Fact]
    public void Embedded_separators_cannot_shift_field_boundaries()
    {
        // Without length prefixes both would serialize to "a␟b␟c␟d␟-".
        var original = NotificationIntentToken.Compute(s_key, "a\u001Fb", "c", "d", null);

        Assert.False(NotificationIntentToken.Verify(s_key, original, "a", "b", "c", "d\u001F-"));
        Assert.False(NotificationIntentToken.Verify(s_key, original, "a\u001Fb\u001Fc", "d", null, null));
    }

    [Fact]
    public void A_token_signed_with_another_key_fails_verification()
    {
        var otherKey = (byte[])s_key.Clone();
        otherKey[0] ^= 0xFF;
        var forged = NotificationIntentToken.Compute(otherKey, "n1", null, "/orders/42", null);

        Assert.False(NotificationIntentToken.Verify(s_key, forged, "n1", null, "/orders/42", null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a token")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    public void Missing_or_malformed_tokens_fail_verification(string? token) =>
        Assert.False(NotificationIntentToken.Verify(s_key, token, "n1", null, null, null));

    [Fact]
    public void Truncated_extended_and_case_changed_tokens_fail_verification()
    {
        var token = NotificationIntentToken.Compute(s_key, "n1", null, null, null);

        Assert.False(NotificationIntentToken.Verify(s_key, token[..^1], "n1", null, null, null));
        Assert.False(NotificationIntentToken.Verify(s_key, token + "A", "n1", null, null, null));
        Assert.False(NotificationIntentToken.Verify(s_key, SwapCase(token), "n1", null, null, null));
    }

    [Fact]
    public void A_missing_notification_id_never_verifies()
    {
        var token = NotificationIntentToken.Compute(s_key, string.Empty, null, null, null);

        Assert.False(NotificationIntentToken.Verify(s_key, token, null, null, null, null));
    }

    [Fact]
    public void Tokens_are_deterministic_per_key()
    {
        Assert.Equal(
            NotificationIntentToken.Compute(s_key, "n1", "a", "/r", "d"),
            NotificationIntentToken.Compute((byte[])s_key.Clone(), "n1", "a", "/r", "d"));
    }

    [Fact]
    public void A_valid_stored_key_is_decoded()
    {
        var stored = Convert.ToBase64String(s_key);

        Assert.Equal(s_key, NotificationIntentToken.TryDecodeKey(stored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("%%% not base64 %%%")]
    public void Missing_or_corrupt_keys_are_rejected(string? stored) =>
        Assert.Null(NotificationIntentToken.TryDecodeKey(stored));

    [Theory]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    public void Keys_of_the_wrong_length_are_rejected(int length) =>
        Assert.Null(NotificationIntentToken.TryDecodeKey(Convert.ToBase64String(new byte[length])));

    private static string SwapCase(string value) =>
        string.Concat(value.Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)));
}
