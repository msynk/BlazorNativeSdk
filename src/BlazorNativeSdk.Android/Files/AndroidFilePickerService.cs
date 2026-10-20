using Android.App;
using Android.Content;
using Android.Provider;
using BlazorNativeSdk.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>
/// <see cref="IFilePickerService"/> for Android: Microsoft.Maui.Essentials <see cref="FilePicker"/> for opening files and
/// the Storage Access Framework (<c>ACTION_CREATE_DOCUMENT</c>) for saving.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Filters use MIME types (<see cref="FileTypeFilter.MimeTypes"/>); extension-only filters are converted with the
/// system MIME map. Picked files are copies in the app cache; dispose them to delete the copies.</item>
/// <item><see cref="SaveFileAsync"/> shows the system "Save to" UI. The saved document lives in a content provider, so
/// <see cref="SavedFile.FullPath"/> is <see langword="null"/>.</item>
/// <item><see cref="PickFolderAsync"/> returns the Storage Access Framework tree URI in <see cref="PickedFolder.Uri"/>
/// (Android grants folder access through <c>content://</c> URIs, never file-system paths, so
/// <see cref="PickedFolder.FullPath"/> is <see langword="null"/>). Access is persisted across restarts; access the folder
/// with <c>DocumentFile</c> or <c>DocumentsContract</c>. <see cref="PickFolderTreeUriAsync"/> returns the native URI.</item>
/// </list>
/// </remarks>
public sealed partial class AndroidFilePickerService : IFilePickerService
{
    private const string AdapterName = "android";
    private const string CacheArea = "bns-picked";
    private readonly ILogger<AndroidFilePickerService> _logger;

    /// <summary>Creates the service.</summary>
    public AndroidFilePickerService(ILogger<AndroidFilePickerService> logger) => _logger = logger;

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.Files;

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Available(Capability, AdapterName, FilePickerFeatures.Multiple, FilePickerFeatures.SaveDialog, FilePickerFeatures.FolderPicker));

    /// <inheritdoc />
    public ValueTask<NativeResult<IReadOnlyList<NativeFile>>> PickFilesAsync(FilePickOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new FilePickOptions();
        return NativeOperation.RunAsync<IReadOnlyList<NativeFile>>(Capability, async ct =>
        {
            var pickOptions = new PickOptions { PickerTitle = options.Title, FileTypes = ToFileType(options.FileTypes) };
            List<FileResult> results;
            try
            {
                if (options.AllowMultiple)
                {
                    var many = await MainThread.InvokeOnMainThreadAsync(() => FilePicker.Default.PickMultipleAsync(pickOptions)).WaitAsync(ct).ConfigureAwait(false);
                    results = many?.OfType<FileResult>().ToList() ?? [];
                }
                else
                {
                    var one = await MainThread.InvokeOnMainThreadAsync(() => FilePicker.Default.PickAsync(pickOptions)).WaitAsync(ct).ConfigureAwait(false);
                    results = one is null ? [] : [one];
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return EssentialsErrors.Map(Capability, ex, NativePermissions.StorageRead);
            }

            if (results.Count == 0)
            {
                return NativeError.Cancelled(Capability);
            }

            var files = new List<NativeFile>(results.Count);
            try
            {
                foreach (var file in results)
                {
                    files.Add(await ContentUriFiles.ToNativeFileAsync(file, CacheArea, ct).ConfigureAwait(false));
                }
            }
            catch
            {
                foreach (var file in files)
                {
                    await file.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }

            return files;
        }, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The content stream is copied to the chosen document on a background thread. When writing fails after the document was
    /// created, the empty document is deleted (best effort).
    /// </remarks>
    public ValueTask<NativeResult<SavedFile>> SaveFileAsync(FileSaveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return NativeOperation.RunAsync<SavedFile>(Capability, async ct =>
        {
            if (string.IsNullOrWhiteSpace(request.SuggestedFileName) || request.Content is null || !request.Content.CanRead)
            {
                return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "A file name and a readable content stream are required.");
            }

            var fileName = CacheFiles.SanitizeFileName(request.SuggestedFileName, request.ContentType);
            var contentType = request.ContentType
                ?? MimeTypes.FromFileName(fileName)
                ?? request.FileTypes.SelectMany(f => f.MimeTypes).FirstOrDefault(m => !m.Contains('*', StringComparison.Ordinal))
                ?? MimeTypes.OctetStream;

            using var intent = new Intent(Intent.ActionCreateDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType(contentType);
            intent.PutExtra(Intent.ExtraTitle, fileName);

            (Result ResultCode, Intent? Data) result;
            try
            {
                result = await ActivityResultBridge.StartActivityForResultAsync(Capability, intent, ct).ConfigureAwait(false);
            }
            catch (ActivityNotFoundException ex)
            {
                return NativeError.Unavailable(Capability, AndroidErrorCodes.NoHandler,
                    "No document provider on this device handles ACTION_CREATE_DOCUMENT.") with { Exception = ex };
            }

            using var data = result.Data;
            using var uri = data?.Data;
            if (result.ResultCode != Result.Ok || uri is null)
            {
                return NativeError.Cancelled(Capability);
            }

            try
            {
                await WriteAsync(uri, request.Content, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                LogSaveFailed(_logger, ex);
                TryDeleteDocument(uri);
                return ex is Java.Lang.SecurityException
                    ? NativeError.Failed(Capability, NativeErrorCodes.PermissionDenied, "The document provider refused write access.", ex)
                    : NativeError.FromException(Capability, ex);
            }
            catch (OperationCanceledException)
            {
                TryDeleteDocument(uri);
                throw;
            }

            var (displayName, _) = ContentUriFiles.Query(uri);
            return new SavedFile(displayName ?? fileName, null, FileSaveMethod.SaveDialog)
            {
                Uri = Uri.TryCreate(uri.ToString(), UriKind.Absolute, out var savedUri) ? savedUri : null,
            };
        }, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The result carries the persisted Storage Access Framework tree URI in <see cref="PickedFolder.Uri"/>;
    /// <see cref="PickedFolder.FullPath"/> is always <see langword="null"/> on Android.
    /// </remarks>
    public async ValueTask<NativeResult<PickedFolder>> PickFolderAsync(string? title = null, CancellationToken cancellationToken = default)
    {
        var picked = await PickFolderTreeUriAsync(persistAccess: true, cancellationToken).ConfigureAwait(false);
        if (!picked.IsSuccess)
        {
            return picked.Error;
        }

        using var treeUri = picked.Value;
        return new PickedFolder(TreeDisplayName(treeUri), null)
        {
            Uri = Uri.TryCreate(treeUri.ToString(), UriKind.Absolute, out var folderUri) ? folderUri : null,
        };
    }

    private static string TreeDisplayName(Android.Net.Uri treeUri)
    {
        try
        {
            // Tree document ids look like "primary:Documents/Reports"; show the last path segment.
            var documentId = Android.Provider.DocumentsContract.GetTreeDocumentId(treeUri) ?? string.Empty;
            var name = documentId[(documentId.LastIndexOfAny([':', '/']) + 1)..];
            return name.Length > 0 ? name : documentId;
        }
        catch (Java.Lang.IllegalArgumentException)
        {
            return treeUri.LastPathSegment ?? treeUri.ToString() ?? "folder";
        }
    }

    /// <summary>
    /// Shows the system folder picker (<c>ACTION_OPEN_DOCUMENT_TREE</c>) and returns the tree URI of the chosen folder.
    /// Android-specific companion of <see cref="PickFolderAsync"/>.
    /// </summary>
    /// <param name="persistAccess">
    /// Take a persistable read/write grant so the URI stays usable after the app restarts (release it with
    /// <c>ContentResolver.ReleasePersistableUriPermission</c> when no longer needed; apps can hold a limited number of grants).
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The tree URI, or <see cref="NativeResultStatus.Cancelled"/>.</returns>
    public ValueTask<NativeResult<Android.Net.Uri>> PickFolderTreeUriAsync(bool persistAccess = true, CancellationToken cancellationToken = default) =>
        NativeOperation.RunAsync<Android.Net.Uri>(Capability, async ct =>
        {
            using var intent = new Intent(Intent.ActionOpenDocumentTree);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantPersistableUriPermission);
            (Result ResultCode, Intent? Data) result;
            try
            {
                result = await ActivityResultBridge.StartActivityForResultAsync(Capability, intent, ct).ConfigureAwait(false);
            }
            catch (ActivityNotFoundException ex)
            {
                return NativeError.Unavailable(Capability, AndroidErrorCodes.NoHandler,
                    "No document provider on this device handles ACTION_OPEN_DOCUMENT_TREE.") with { Exception = ex };
            }

            using var data = result.Data;
            var uri = data?.Data;
            if (result.ResultCode != Result.Ok || uri is null)
            {
                return NativeError.Cancelled(Capability);
            }

            if (persistAccess)
            {
                AndroidContext.App.ContentResolver?.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
            }

            return uri;
        }, cancellationToken);

    private static async Task WriteAsync(Android.Net.Uri uri, Stream content, CancellationToken cancellationToken)
    {
        var resolver = AndroidContext.App.ContentResolver ?? throw new InvalidOperationException("No ContentResolver is available.");
        await Task.Run(async () =>
        {
            Stream? output;
            try
            {
                // "wt" truncates; some providers only support "w", which is equivalent for a document that was just created.
                output = resolver.OpenOutputStream(uri, "wt");
            }
            catch (Exception ex) when (ex is Java.Lang.IllegalArgumentException or Java.IO.FileNotFoundException or Java.Lang.UnsupportedOperationException)
            {
                output = resolver.OpenOutputStream(uri, "w");
            }

            if (output is null)
            {
                throw new IOException("The document provider returned no output stream.");
            }

            await using (output.ConfigureAwait(false))
            {
                await content.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private void TryDeleteDocument(Android.Net.Uri uri)
    {
        try
        {
            if (AndroidContext.App.ContentResolver is { } resolver)
            {
                DocumentsContract.DeleteDocument(resolver, uri);
            }
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or IOException)
        {
            LogDeleteFailed(_logger, ex);
        }
    }

    private static FilePickerFileType? ToFileType(IReadOnlyList<FileTypeFilter> filters)
    {
        if (filters.Count == 0)
        {
            return null;
        }

        var mimeTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var filter in filters)
        {
            foreach (var mime in filter.MimeTypes)
            {
                mimeTypes.Add(mime);
            }

            if (filter.MimeTypes.Count == 0)
            {
                foreach (var extension in filter.Extensions)
                {
                    var mime = MimeTypes.FromFileName(extension)
                        ?? Android.Webkit.MimeTypeMap.Singleton?.GetMimeTypeFromExtension(extension.TrimStart('.').ToLowerInvariant());
                    mimeTypes.Add(mime ?? MimeTypes.OctetStream);
                }
            }
        }

        return mimeTypes.Count == 0
            ? null
            : new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>> { [DevicePlatform.Android] = mimeTypes.ToArray() });
    }


    [LoggerMessage(EventId = 3401, Level = LogLevel.Warning, Message = "Writing the saved document failed.")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3402, Level = LogLevel.Debug, Message = "Deleting the incomplete document failed.")]
    private static partial void LogDeleteFailed(ILogger logger, Exception exception);
}
