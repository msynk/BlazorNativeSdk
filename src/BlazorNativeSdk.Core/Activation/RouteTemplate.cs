using System.Diagnostics.CodeAnalysis;

namespace BlazorNativeSdk.Lifecycle;

/// <summary>
/// A minimal route template matcher used by <see cref="IAppActivationService.MapRoute"/>. Supports literal segments,
/// parameters (<c>{id}</c>), optional trailing parameters (<c>{id?}</c>) and catch-all parameters (<c>{*rest}</c> or
/// <c>{**rest}</c>). Matching is case-insensitive for literals; captured values are URL-decoded.
/// </summary>
public sealed class RouteTemplate
{
    private readonly Segment[] _segments;

    private RouteTemplate(string text, Segment[] segments)
    {
        Text = text;
        _segments = segments;
    }

    /// <summary>The template text.</summary>
    public string Text { get; }

    /// <summary>Parses a template.</summary>
    /// <exception cref="FormatException">The template is invalid.</exception>
    public static RouteTemplate Parse(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var parts = template.Trim().Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var segments = new Segment[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.StartsWith('{') && part.EndsWith('}'))
            {
                var name = part[1..^1];
                var catchAll = false;
                var optional = false;
                if (name.StartsWith("**", StringComparison.Ordinal))
                {
                    name = name[2..];
                    catchAll = true;
                }
                else if (name.StartsWith('*'))
                {
                    name = name[1..];
                    catchAll = true;
                }

                if (name.EndsWith('?'))
                {
                    name = name[..^1];
                    optional = true;
                }

                // Ignore Blazor-style constraints such as {id:int}; values are matched as strings.
                var colon = name.IndexOf(':', StringComparison.Ordinal);
                if (colon >= 0)
                {
                    name = name[..colon];
                }

                if (name.Length == 0 || (catchAll && i != parts.Length - 1) || (optional && i != parts.Length - 1))
                {
                    throw new FormatException($"Invalid route template '{template}'.");
                }

                segments[i] = new Segment(name, IsParameter: true, IsCatchAll: catchAll, IsOptional: optional || catchAll);
            }
            else
            {
                if (part.Contains('{', StringComparison.Ordinal) || part.Contains('}', StringComparison.Ordinal))
                {
                    throw new FormatException($"Invalid route template '{template}'. Complex segments are not supported.");
                }

                segments[i] = new Segment(part, IsParameter: false, IsCatchAll: false, IsOptional: false);
            }
        }

        return new RouteTemplate(template, segments);
    }

    /// <summary>Matches a route (path with optional query string). Query values are added to the captured values when no route value has the same name.</summary>
    public bool TryMatch(string route, [NotNullWhen(true)] out IReadOnlyDictionary<string, string>? values)
    {
        values = null;
        ArgumentNullException.ThrowIfNull(route);

        var queryIndex = route.IndexOfAny(['?', '#']);
        var path = queryIndex >= 0 ? route[..queryIndex] : route;
        var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var i = 0;
        for (; i < _segments.Length; i++)
        {
            var segment = _segments[i];
            if (segment.IsCatchAll)
            {
                result[segment.Name] = string.Join('/', parts.Skip(i).Select(Uri.UnescapeDataString));
                i = _segments.Length;
                break;
            }

            if (i >= parts.Length)
            {
                if (segment.IsOptional)
                {
                    continue;
                }

                return false;
            }

            if (segment.IsParameter)
            {
                result[segment.Name] = Uri.UnescapeDataString(parts[i]);
            }
            else if (!string.Equals(segment.Name, parts[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var consumedAll = _segments.Length > 0 && _segments[^1].IsCatchAll || parts.Length <= _segments.Length;
        if (!consumedAll)
        {
            return false;
        }

        if (queryIndex >= 0 && route[queryIndex] == '?')
        {
            var query = route[(queryIndex + 1)..];
            var hash = query.IndexOf('#', StringComparison.Ordinal);
            if (hash >= 0)
            {
                query = query[..hash];
            }

            foreach (var pair in QueryString.Parse(query))
            {
                result.TryAdd(pair.Key, pair.Value);
            }
        }

        values = result;
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => Text;

    private readonly record struct Segment(string Name, bool IsParameter, bool IsCatchAll, bool IsOptional);
}

/// <summary>Query string helpers.</summary>
public static class QueryString
{
    /// <summary>Parses a query string (with or without the leading <c>?</c>). Repeated keys keep the first value.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string? query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query))
        {
            return result;
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Decode(eq >= 0 ? pair[..eq] : pair);
            var value = eq >= 0 ? Decode(pair[(eq + 1)..]) : string.Empty;
            if (key.Length > 0)
            {
                result.TryAdd(key, value);
            }
        }

        return result;

        static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));
    }
}
