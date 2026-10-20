using BlazorNativeSdk.Devices;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>Creates uniquely named files inside the app cache directory.</summary>
internal static class CacheFiles
{
    /// <summary>Returns a path for <paramref name="fileName"/> in a new unique folder under <c>{cache}/{area}</c>.</summary>
    public static string CreateUniquePath(string area, string? fileName, string? contentType = null)
    {
        var folder = Path.Combine(AndroidContext.CacheDirectory, area, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, SanitizeFileName(fileName, contentType));
    }

    /// <summary>Copies a stream into a new cache file and returns its path. Deletes the partial file on failure.</summary>
    public static async Task<string> CopyToCacheAsync(Stream source, string area, string? fileName, string? contentType, CancellationToken cancellationToken)
    {
        var path = CreateUniquePath(area, fileName, contentType);
        try
        {
            var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await using (target.ConfigureAwait(false))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            return path;
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    /// <summary>Deletes sub-folders of <c>{cache}/{area}</c> older than <paramref name="maxAge"/> (best effort).</summary>
    public static void PurgeOlderThan(string area, TimeSpan maxAge)
    {
        try
        {
            var root = new DirectoryInfo(Path.Combine(AndroidContext.CacheDirectory, area));
            if (!root.Exists)
            {
                return;
            }

            var threshold = DateTime.UtcNow - maxAge;
            foreach (var folder in root.EnumerateDirectories())
            {
                if (folder.LastWriteTimeUtc < threshold)
                {
                    folder.Delete(recursive: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the OS also clears the cache under storage pressure.
        }
    }

    /// <summary>Deletes a file and its (empty) unique parent folder, ignoring failures.</summary>
    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            var folder = Path.GetDirectoryName(path);
            if (folder is not null && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Produces a safe file name, adding an extension inferred from <paramref name="contentType"/> when missing.</summary>
    public static string SanitizeFileName(string? fileName, string? contentType = null)
    {
        var name = Path.GetFileName(fileName ?? string.Empty);
        var invalid = Path.GetInvalidFileNameChars();
        name = new string([.. name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c)]).Trim();
        if (name.Length == 0 || name is "." or "..")
        {
            name = "file";
        }

        if (name.Length > 120)
        {
            var extension = Path.GetExtension(name);
            name = name[..(120 - Math.Min(extension.Length, 20))] + (extension.Length <= 20 ? extension : string.Empty);
        }

        if (!Path.HasExtension(name) && MimeTypes.GetExtension(contentType) is { } inferred)
        {
            name += inferred;
        }

        return name;
    }
}
