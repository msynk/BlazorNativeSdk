namespace BlazorNativeSdk.Devices;

/// <summary>
/// A named group of acceptable file types. Both MIME types and extensions are supplied because platforms
/// filter differently: Android and browsers prefer MIME types, Windows uses extensions, Apple platforms map
/// either to Uniform Type Identifiers.
/// </summary>
public sealed record FileTypeFilter
{
    /// <summary>Creates a filter.</summary>
    /// <param name="description">A user-facing description such as "Images".</param>
    /// <param name="mimeTypes">MIME types, wildcards such as <c>image/*</c> are allowed.</param>
    /// <param name="extensions">File extensions including the dot, such as <c>.png</c>.</param>
    public FileTypeFilter(string description, IReadOnlyList<string> mimeTypes, IReadOnlyList<string> extensions)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(mimeTypes);
        ArgumentNullException.ThrowIfNull(extensions);
        Description = description;
        MimeTypes = mimeTypes;
        Extensions = extensions.Select(e => e.StartsWith('.') ? e : "." + e).ToArray();
    }

    /// <summary>A user-facing description.</summary>
    public string Description { get; }

    /// <summary>Accepted MIME types.</summary>
    public IReadOnlyList<string> MimeTypes { get; }

    /// <summary>Accepted extensions, each including the leading dot.</summary>
    public IReadOnlyList<string> Extensions { get; }

    /// <summary>Images.</summary>
    public static FileTypeFilter Images { get; } = new("Images", ["image/*"], [".png", ".jpg", ".jpeg", ".gif", ".webp", ".heic", ".bmp"]);

    /// <summary>Videos.</summary>
    public static FileTypeFilter Videos { get; } = new("Videos", ["video/*"], [".mp4", ".mov", ".webm", ".m4v", ".3gp"]);

    /// <summary>PDF documents.</summary>
    public static FileTypeFilter Pdf { get; } = new("PDF documents", ["application/pdf"], [".pdf"]);

    /// <summary>Plain text files.</summary>
    public static FileTypeFilter Text { get; } = new("Text files", ["text/plain"], [".txt"]);

    /// <summary>JSON files.</summary>
    public static FileTypeFilter Json { get; } = new("JSON files", ["application/json"], [".json"]);

    /// <summary>Creates a filter from extensions; MIME types are inferred where known.</summary>
    public static FileTypeFilter FromExtensions(string description, params string[] extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        var mimes = extensions
            .Select(e => BlazorNativeSdk.Devices.MimeTypes.FromFileName(e.StartsWith('.') ? e : "." + e))
            .Where(m => m is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new FileTypeFilter(description, mimes!, extensions);
    }
}

/// <summary>Options for <see cref="IFilePickerService.PickFilesAsync"/>.</summary>
public sealed record FilePickOptions
{
    /// <summary>Allow selecting more than one file.</summary>
    public bool AllowMultiple { get; init; }

    /// <summary>Accepted file types; empty means any file.</summary>
    public IReadOnlyList<FileTypeFilter> FileTypes { get; init; } = [];

    /// <summary>A dialog title where the platform supports one.</summary>
    public string? Title { get; init; }
}

/// <summary>A request to save content to a user-chosen location.</summary>
public sealed record FileSaveRequest
{
    /// <summary>The suggested file name including extension.</summary>
    public required string SuggestedFileName { get; init; }

    /// <summary>The content to save. The stream is read once and is not disposed by the service.</summary>
    public required Stream Content { get; init; }

    /// <summary>The MIME type; inferred from <see cref="SuggestedFileName"/> when omitted.</summary>
    public string? ContentType { get; init; }

    /// <summary>File types offered in the save dialog.</summary>
    public IReadOnlyList<FileTypeFilter> FileTypes { get; init; } = [];

    /// <summary>A dialog title where the platform supports one.</summary>
    public string? Title { get; init; }
}

/// <summary>How a file was saved by <see cref="IFilePickerService.SaveFileAsync"/>.</summary>
public enum FileSaveMethod
{
    /// <summary>The user picked the location in a native save dialog.</summary>
    SaveDialog,

    /// <summary>The file was handed to the browser's download manager; the final location is unknown.</summary>
    Download,

    /// <summary>The file was exported through the share/export sheet (iOS "Save to Files").</summary>
    ExportSheet,
}

/// <summary>The result of saving a file.</summary>
/// <param name="FileName">The final file name when known.</param>
/// <param name="FullPath">The final path when the platform reveals it.</param>
/// <param name="Method">How the file was saved.</param>
public sealed record SavedFile(string FileName, string? FullPath, FileSaveMethod Method)
{
    /// <summary>
    /// A platform URI for the saved document when it has no file-system path (an Android <c>content://</c> URI, a
    /// security-scoped <c>file://</c> URL on Apple platforms).
    /// </summary>
    public Uri? Uri { get; init; }
}

/// <summary>A folder chosen by the user.</summary>
/// <param name="Name">The display name.</param>
/// <param name="FullPath">The path when the folder is accessible through the file system.</param>
public sealed record PickedFolder(string Name, string? FullPath)
{
    /// <summary>
    /// A platform URI for the folder when it is not addressable by path (an Android Storage Access Framework tree URI).
    /// Access may be temporary; persist it with the platform's mechanism when you need it across launches.
    /// </summary>
    public Uri? Uri { get; init; }
}

/// <summary>Optional features reported by <see cref="IFilePickerService"/>.</summary>
public static class FilePickerFeatures
{
    /// <summary>Selecting multiple files.</summary>
    public const string Multiple = "multiple";

    /// <summary>A native save dialog with a user-chosen location.</summary>
    public const string SaveDialog = "save-dialog";

    /// <summary>Saving through a download (location chosen by the browser).</summary>
    public const string Download = "download";

    /// <summary>Picking a folder.</summary>
    public const string FolderPicker = "folder-picker";
}

/// <summary>Opens and saves user files through the platform's native pickers.</summary>
public interface IFilePickerService : INativeCapability
{
    /// <summary>Shows the native open dialog.</summary>
    /// <returns>The selected files, or <see cref="NativeResultStatus.Cancelled"/> when the user dismissed the picker.</returns>
    ValueTask<NativeResult<IReadOnlyList<NativeFile>>> PickFilesAsync(FilePickOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Saves content to a location chosen by the user (or via download in browsers without a save dialog).</summary>
    ValueTask<NativeResult<SavedFile>> SaveFileAsync(FileSaveRequest request, CancellationToken cancellationToken = default);

    /// <summary>Shows a folder picker. Check the <see cref="FilePickerFeatures.FolderPicker"/> feature first.</summary>
    ValueTask<NativeResult<PickedFolder>> PickFolderAsync(string? title = null, CancellationToken cancellationToken = default);
}

/// <summary>Convenience methods for <see cref="IFilePickerService"/>.</summary>
public static class FilePickerServiceExtensions
{
    /// <summary>Picks a single file.</summary>
    public static async ValueTask<NativeResult<NativeFile>> PickFileAsync(
        this IFilePickerService service,
        params FileTypeFilter[] fileTypes)
    {
        ArgumentNullException.ThrowIfNull(service);
        var result = await service.PickFilesAsync(new FilePickOptions { FileTypes = fileTypes }).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result.Error;
        }

        return result.Value.Count > 0
            ? NativeResult.Success(result.Value[0])
            : NativeError.Cancelled(service.Capability);
    }
}
