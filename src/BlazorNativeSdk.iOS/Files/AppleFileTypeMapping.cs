using BlazorNativeSdk.Devices;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>
/// Maps <see cref="FileTypeFilter"/>s to Uniform Type Identifiers. Pure: the <c>UTType</c> lookups are passed in, so the
/// mapping can be unit tested without the Apple bindings.
/// </summary>
internal static class AppleFileTypeMapping
{
    /// <summary>The UTI for a MIME wildcard such as <c>image/*</c>, or <see langword="null"/> when it is not a wildcard.</summary>
    public static string? FromMimeWildcard(string mimeType) => mimeType.Trim().ToLowerInvariant() switch
    {
        "*/*" or "*" => "public.item",
        "image/*" => "public.image",
        "video/*" => "public.movie",
        "audio/*" => "public.audio",
        "text/*" => "public.text",
        "application/*" => "public.data",
        _ => null,
    };

    /// <summary>
    /// The distinct type identifiers for the filters; empty means "any file". Extensions without a registered type map to the
    /// dynamic identifier the system derives for them, which still filters by extension.
    /// </summary>
    /// <param name="filters">The filters.</param>
    /// <param name="fromMimeType">Resolves a concrete MIME type (<c>UTType.CreateFromMimeType(...)?.Identifier</c>).</param>
    /// <param name="fromExtension">Resolves an extension without the dot (<c>UTType.CreateFromExtension(...)?.Identifier</c>).</param>
    public static string[] ToTypeIdentifiers(IReadOnlyList<FileTypeFilter> filters, Func<string, string?> fromMimeType, Func<string, string?> fromExtension)
    {
        var identifiers = new List<string>();
        void Add(string? identifier)
        {
            if (!string.IsNullOrEmpty(identifier) && !identifiers.Contains(identifier, StringComparer.Ordinal))
            {
                identifiers.Add(identifier);
            }
        }

        foreach (var filter in filters)
        {
            foreach (var mime in filter.MimeTypes)
            {
                Add(FromMimeWildcard(mime) ?? (mime.Contains('*', StringComparison.Ordinal) ? null : fromMimeType(mime)));
            }

            foreach (var extension in filter.Extensions)
            {
                var bare = extension.TrimStart('.');
                if (bare.Length > 0)
                {
                    Add(fromExtension(bare));
                }
            }
        }

        return [.. identifiers];
    }
}
