using BlazorNativeSdk.Devices;
using UniformTypeIdentifiers;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>Maps <see cref="FileTypeFilter"/>s to Uniform Type Identifiers.</summary>
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
    public static string[] ToTypeIdentifiers(IReadOnlyList<FileTypeFilter> filters)
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
                Add(FromMimeWildcard(mime) ?? (mime.Contains('*', StringComparison.Ordinal) ? null : UTType.CreateFromMimeType(mime)?.Identifier));
            }

            foreach (var extension in filter.Extensions)
            {
                var bare = extension.TrimStart('.');
                if (bare.Length > 0)
                {
                    Add(UTType.CreateFromExtension(bare)?.Identifier);
                }
            }
        }

        return [.. identifiers];
    }
}
