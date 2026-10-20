using BlazorNativeSdk.Devices;
using Microsoft.Maui.Storage;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>Helpers for <c>content://</c> URIs returned by pickers and the Storage Access Framework.</summary>
internal static class ContentUriFiles
{
    private const string DisplayNameColumn = "_display_name";
    private const string SizeColumn = "_size";

    /// <summary>Reads the display name and size of a document (best effort).</summary>
    public static (string? DisplayName, long? Size) Query(Android.Net.Uri uri)
    {
        try
        {
            using var cursor = AndroidContext.App.ContentResolver?.Query(uri, [DisplayNameColumn, SizeColumn], null, null, null);
            if (cursor is null || !cursor.MoveToFirst())
            {
                return (null, null);
            }

            var nameIndex = cursor.GetColumnIndex(DisplayNameColumn);
            var sizeIndex = cursor.GetColumnIndex(SizeColumn);
            var name = nameIndex >= 0 && !cursor.IsNull(nameIndex) ? cursor.GetString(nameIndex) : null;
            long? size = sizeIndex >= 0 && !cursor.IsNull(sizeIndex) ? cursor.GetLong(sizeIndex) : null;
            return (name, size);
        }
        catch (Java.Lang.Exception)
        {
            // Some providers do not support OpenableColumns queries.
            return (null, null);
        }
    }

    /// <summary>Copies a document into the app cache and returns it as a file that is deleted on disposal.</summary>
    public static async Task<NativeFile> CopyToCacheAsync(Android.Net.Uri uri, string area, CancellationToken cancellationToken)
    {
        var resolver = AndroidContext.App.ContentResolver ?? throw new InvalidOperationException("No ContentResolver is available.");
        var contentType = resolver.GetType(uri);
        var (name, _) = Query(uri);
        var input = await Task.Run(() => resolver.OpenInputStream(uri), cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("The content provider returned no stream for the selected item.");
        await using (input.ConfigureAwait(false))
        {
            var path = await CacheFiles.CopyToCacheAsync(input, area, name ?? "media", contentType, cancellationToken).ConfigureAwait(false);
            return new PhysicalNativeFile(path, contentType, deleteOnDispose: true);
        }
    }

    /// <summary>
    /// Converts an Essentials <see cref="FileResult"/> to a <see cref="NativeFile"/>. Files inside the app cache (copies made
    /// by Essentials) are deleted on disposal; inaccessible paths are copied into the cache first.
    /// </summary>
    public static async Task<NativeFile> ToNativeFileAsync(FileResult file, string area, CancellationToken cancellationToken)
    {
        var path = file.FullPath;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            return new PhysicalNativeFile(path, file.ContentType, deleteOnDispose: AndroidContext.IsInCacheDirectory(path));
        }

        var source = await file.OpenReadAsync().ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var copy = await CacheFiles.CopyToCacheAsync(source, area, file.FileName, file.ContentType, cancellationToken).ConfigureAwait(false);
            return new PhysicalNativeFile(copy, file.ContentType, deleteOnDispose: true);
        }
    }
}
