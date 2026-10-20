namespace BlazorNativeSdk.ApplePlatform;

/// <summary>Pure helpers for building activation payloads.</summary>
internal static class AppleActivationMapping
{
    /// <summary>The data key that carries a quick action's type.</summary>
    public const string ShortcutTypeKey = "type";

    /// <summary>The userInfo key a quick action can use to name an app route.</summary>
    public const string ShortcutRouteKey = "route";

    /// <summary>The data key that carries a user activity's type.</summary>
    public const string ActivityTypeKey = "activityType";

    /// <summary>Parses a URL query (with or without the leading <c>?</c>); the first value of a repeated key wins.</summary>
    public static Dictionary<string, string> ParseQuery(string? query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query))
        {
            return values;
        }

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            var key = Decode(separator < 0 ? part : part[..separator]);
            var value = separator < 0 ? string.Empty : Decode(part[(separator + 1)..]);
            if (key.Length > 0)
            {
                values.TryAdd(key, value);
            }
        }

        return values;
    }

    /// <summary>Builds the data of a quick action activation and extracts an optional app route.</summary>
    public static (Dictionary<string, string> Data, string? Route) ForShortcut(string type, IReadOnlyDictionary<string, string> userInfo)
    {
        var data = new Dictionary<string, string>(userInfo, StringComparer.Ordinal) { [ShortcutTypeKey] = type };
        var route = userInfo.TryGetValue(ShortcutRouteKey, out var candidate) && candidate.StartsWith('/') ? candidate : null;
        return (data, route);
    }

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return value;
        }
    }
}
