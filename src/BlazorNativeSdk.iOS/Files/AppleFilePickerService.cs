using System.Collections.Concurrent;
using BlazorNativeSdk.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;
using UniformTypeIdentifiers;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary><see cref="IFilePickerService"/> based on <c>UIDocumentPickerViewController</c>.</summary>
/// <remarks>
/// <para>
/// Opened files are copied into the app's temporary directory by the system and deleted when the returned
/// <see cref="NativeFile"/> is disposed. Saving presents the export picker: on iOS it is the "Save to Files" sheet
/// (<see cref="FileSaveMethod.ExportSheet"/>), on Mac Catalyst the native save panel (<see cref="FileSaveMethod.SaveDialog"/>).
/// </para>
/// <para>
/// A picked folder lives outside the app sandbox. The adapter starts security-scoped access to it and keeps it for the lifetime
/// of the process (or until <see cref="ReleaseFolderAccess"/>), so <see cref="PickedFolder.FullPath"/> can be used with
/// <see cref="System.IO"/> APIs. Access does not survive an app restart; persist a security-scoped bookmark if you need that.
/// </para>
/// </remarks>
public sealed partial class AppleFilePickerService : IFilePickerService, IDisposable, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, NSUrl> _folderAccess = new(StringComparer.Ordinal);
    private readonly ILogger<AppleFilePickerService> _logger;
    private int _disposed;

    /// <summary>Creates the service.</summary>
    public AppleFilePickerService(ILogger<AppleFilePickerService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.Files;

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Available(Capability, AppleHost.AdapterName,
            FilePickerFeatures.Multiple, FilePickerFeatures.FolderPicker, FilePickerFeatures.SaveDialog));

    /// <inheritdoc />
    public ValueTask<NativeResult<IReadOnlyList<NativeFile>>> PickFilesAsync(FilePickOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new FilePickOptions();
        return NativeOperation.RunAsync<IReadOnlyList<NativeFile>>(Capability, async token =>
        {
            var identifiers = AppleFileTypeMapping.ToTypeIdentifiers(
                options.FileTypes,
                static mimeType => UTType.CreateFromMimeType(mimeType)?.Identifier,
                static extension => UTType.CreateFromExtension(extension)?.Identifier);
            var pickOptions = new PickOptions
            {
                PickerTitle = options.Title,
                FileTypes = identifiers.Length == 0
                    ? null
                    : new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                    {
                        [DevicePlatform.iOS] = identifiers,
                        [DevicePlatform.MacCatalyst] = identifiers,
                    }),
            };

            var picked = new List<FileResult>();
            try
            {
                if (options.AllowMultiple)
                {
                    var many = await AppleUI.OnMainThreadAsync(() => FilePicker.Default.PickMultipleAsync(pickOptions)).WaitAsync(token).ConfigureAwait(false);
                    picked.AddRange(many?.OfType<FileResult>() ?? []);
                }
                else if (await AppleUI.OnMainThreadAsync(() => FilePicker.Default.PickAsync(pickOptions)).WaitAsync(token).ConfigureAwait(false) is { } single)
                {
                    picked.Add(single);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                await AppleUI.DismissTopIfAsync<UIDocumentPickerViewController>().ConfigureAwait(false);
                throw;
            }

            if (picked.Count == 0)
            {
                return NativeError.Cancelled(Capability);
            }

            var files = new List<NativeFile>(picked.Count);
            try
            {
                foreach (var result in picked)
                {
                    files.Add(await AppleFiles.FromFileResultAsync(result, token).ConfigureAwait(false));
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
    public ValueTask<NativeResult<SavedFile>> SaveFileAsync(FileSaveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return NativeOperation.RunAsync<SavedFile>(Capability, async token =>
        {
            var fileName = AppleFiles.SanitizeFileName(request.SuggestedFileName);
            var exportPath = AppleFiles.CreateTempFilePath(fileName);
            try
            {
                var target = new FileStream(exportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                await using (target.ConfigureAwait(false))
                {
                    await request.Content.CopyToAsync(target, token).ConfigureAwait(false);
                }

                using var exportUrl = NSUrl.FromFilename(exportPath);
                var urls = await PickDocumentsAsync(() => new UIDocumentPickerViewController([exportUrl], asCopy: true), token).ConfigureAwait(false);
                if (!urls.IsSuccess)
                {
                    return urls.Error;
                }

                if (urls.Value is not [var destination, ..])
                {
                    return NativeError.Cancelled(Capability);
                }

                var method = OperatingSystem.IsMacCatalyst() ? FileSaveMethod.SaveDialog : FileSaveMethod.ExportSheet;
                return new SavedFile(destination.LastPathComponent ?? fileName, destination.Path, method);
            }
            finally
            {
                AppleFiles.TryDelete(exportPath);
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<PickedFolder>> PickFolderAsync(string? title = null, CancellationToken cancellationToken = default) =>
        NativeOperation.RunAsync<PickedFolder>(Capability, async token =>
        {
            var urls = await PickDocumentsAsync(() => new UIDocumentPickerViewController([UTTypes.Folder]), token).ConfigureAwait(false);
            if (!urls.IsSuccess)
            {
                return urls.Error;
            }

            if (urls.Value is not [var folder, ..] || folder.Path is not { } path)
            {
                return NativeError.Cancelled(Capability);
            }

            if (!folder.StartAccessingSecurityScopedResource())
            {
                LogFolderAccessNotStarted(_logger);
            }
            else if (!_folderAccess.TryAdd(path, folder))
            {
                // Access is already held for this path: balance the extra start call.
                folder.StopAccessingSecurityScopedResource();
            }

            return new PickedFolder(folder.LastPathComponent ?? Path.GetFileName(path), path);
        }, cancellationToken);

    /// <summary>Stops the security-scoped access that <see cref="PickFolderAsync"/> started for <paramref name="path"/>.</summary>
    /// <returns><see langword="true"/> when access was held for the path.</returns>
    public bool ReleaseFolderAccess(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!_folderAccess.TryRemove(path, out var url))
        {
            return false;
        }

        url.StopAccessingSecurityScopedResource();
        url.Dispose();
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var path in _folderAccess.Keys)
        {
            ReleaseFolderAccess(path);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<NativeResult<NSUrl[]>> PickDocumentsAsync(Func<UIDocumentPickerViewController> create, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<NSUrl[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pickerDelegate = new DocumentPickerDelegate(completion);
        var picker = await AppleUI.OnMainThreadAsync(async () =>
        {
            if (AppleUI.GetTopViewController() is not { } presenter)
            {
                return null;
            }

            var controller = create();
            controller.Delegate = pickerDelegate;
            controller.AllowsMultipleSelection = false;
            AppleUI.AnchorPopover(controller, presenter);
            await presenter.PresentViewControllerAsync(controller, true).ConfigureAwait(true);
            return controller;
        }).ConfigureAwait(false);

        if (picker is null)
        {
            return AppleUI.NoPresenter(Capability);
        }

        try
        {
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await AppleUI.OnMainThreadAsync(() => AppleUI.DismissAsync(picker)).ConfigureAwait(false);
            throw;
        }
        finally
        {
            // The picker references its delegate weakly; keep both alive until the user finished. UIKit objects are released
            // on the main thread.
            GC.KeepAlive(pickerDelegate);
            AppleUI.ReleaseOnMainThread(picker);
        }
    }

    [LoggerMessage(EventId = 3501, Level = LogLevel.Warning, Message = "Security-scoped access to the picked folder could not be started; the path may not be accessible.")]
    private static partial void LogFolderAccessNotStarted(ILogger logger);

    private sealed class DocumentPickerDelegate(TaskCompletionSource<NSUrl[]> completion) : UIDocumentPickerDelegate
    {
        public override void DidPickDocument(UIDocumentPickerViewController controller, NSUrl[] urls) => completion.TrySetResult(urls ?? []);

        public override void WasCancelled(UIDocumentPickerViewController controller) => completion.TrySetResult([]);
    }
}
