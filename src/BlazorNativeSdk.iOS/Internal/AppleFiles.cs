using BlazorNativeSdk.Devices;
using Microsoft.Maui.Storage;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>Temporary file helpers shared by the camera, picker and share services.</summary>
internal static class AppleFiles
{
    private const string TempFolderName = "bns";

    /// <summary>The app's temporary directory (<c>NSTemporaryDirectory()</c>).</summary>
    public static string TemporaryDirectory => NSFileManager.TemporaryDirectory ?? Path.GetTempPath();

    /// <summary>
    /// Creates a unique folder below the temporary directory and returns a path for <paramref name="fileName"/> inside it,
    /// so the original file name is preserved without collisions.
    /// </summary>
    public static string CreateTempFilePath(string? fileName)
    {
        var folder = Path.Combine(TemporaryDirectory, TempFolderName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, SanitizeFileName(fileName));
    }

    /// <summary>Replaces characters that are not valid in file names and falls back to a generic name.</summary>
    public static string SanitizeFileName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty).Trim();
        if (name.Length == 0 || name is "." or "..")
        {
            return "file";
        }

        var invalid = Path.GetInvalidFileNameChars();
        return new string([.. name.Select(c => invalid.Contains(c) || c == ':' ? '_' : c)]);
    }

    /// <summary>Returns whether <paramref name="path"/> lives in a location the OS may purge (tmp, Caches).</summary>
    public static bool IsTemporary(string path)
    {
        var full = Path.GetFullPath(path);
        return IsUnder(full, TemporaryDirectory) || IsUnder(full, FileSystem.CacheDirectory);
    }

    /// <summary>Copies a stream to a new temporary file and returns a file that deletes itself on disposal.</summary>
    public static async Task<NativeFile> CopyToTempAsync(Stream content, string? fileName, string? contentType, CancellationToken cancellationToken)
    {
        var path = CreateTempFilePath(fileName);
        try
        {
            var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await using (target.ConfigureAwait(false))
            {
                await content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            TryDelete(path);
            throw;
        }

        return new PhysicalNativeFile(path, contentType, deleteOnDispose: true);
    }

    /// <summary>
    /// Converts an Essentials result into a <see cref="PhysicalNativeFile"/>. Files that already exist on disk are used in
    /// place (and deleted on disposal when they are temporary copies); provider-backed results (PHPicker) are copied.
    /// </summary>
    public static async Task<NativeFile> FromFileResultAsync(FileResult result, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(result.FullPath) && File.Exists(result.FullPath))
        {
            return new PhysicalNativeFile(result.FullPath, result.ContentType, deleteOnDispose: IsTemporary(result.FullPath));
        }

        var stream = await result.OpenReadAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await CopyToTempAsync(stream, result.FileName, result.ContentType, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Returns a path for <paramref name="file"/> on disk, copying it to a temporary file when it has none.</summary>
    /// <returns>The path and whether the caller owns (and must delete) it.</returns>
    public static async Task<(string Path, bool IsTemporaryCopy)> MaterializeAsync(NativeFile file, CancellationToken cancellationToken)
    {
        if (file.FullPath is { } existing && File.Exists(existing))
        {
            return (existing, false);
        }

        var stream = await file.OpenReadAsync(long.MaxValue, cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var path = CreateTempFilePath(file.Name);
            try
            {
                var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                await using (target.ConfigureAwait(false))
                {
                    await stream.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                TryDelete(path);
                throw;
            }

            return (path, true);
        }
    }

    /// <summary>Deletes a file and its per-file temporary folder, ignoring failures.</summary>
    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            var folder = Path.GetDirectoryName(path);
            if (folder is not null && IsUnder(folder, Path.Combine(TemporaryDirectory, TempFolderName)) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsUnder(string path, string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.Ordinal);
    }
}
