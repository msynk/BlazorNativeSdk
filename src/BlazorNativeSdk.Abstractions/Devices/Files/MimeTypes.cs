using System.Collections.Frozen;

namespace BlazorNativeSdk.Devices;

/// <summary>A small, dependency-free MIME type map for the file types most commonly exchanged with native pickers.</summary>
public static class MimeTypes
{
    /// <summary>Generic binary content.</summary>
    public const string OctetStream = "application/octet-stream";

    private static readonly FrozenDictionary<string, string> s_byExtension = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".heic"] = "image/heic",
        [".heif"] = "image/heif",
        [".bmp"] = "image/bmp",
        [".svg"] = "image/svg+xml",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
        [".mp4"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".webm"] = "video/webm",
        [".3gp"] = "video/3gpp",
        [".m4v"] = "video/x-m4v",
        [".mp3"] = "audio/mpeg",
        [".m4a"] = "audio/mp4",
        [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg",
        [".pdf"] = "application/pdf",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".md"] = "text/markdown",
        [".zip"] = "application/zip",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".ics"] = "text/calendar",
        [".vcf"] = "text/vcard",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the MIME type for a file name or extension, or <see langword="null"/> when unknown.</summary>
    public static string? FromFileName(string? fileNameOrExtension)
    {
        if (string.IsNullOrEmpty(fileNameOrExtension))
        {
            return null;
        }

        var extension = fileNameOrExtension.StartsWith('.') ? fileNameOrExtension : Path.GetExtension(fileNameOrExtension);
        return extension.Length > 0 && s_byExtension.TryGetValue(extension, out var mime) ? mime : null;
    }

    /// <summary>Returns the preferred extension (including the dot) for a MIME type, or <see langword="null"/> when unknown.</summary>
    public static string? GetExtension(string? mimeType)
    {
        if (string.IsNullOrEmpty(mimeType))
        {
            return null;
        }

        foreach (var pair in s_byExtension)
        {
            if (string.Equals(pair.Value, mimeType, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Key;
            }
        }

        return null;
    }
}
