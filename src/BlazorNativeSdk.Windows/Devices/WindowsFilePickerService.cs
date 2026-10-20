using System.Runtime.InteropServices;
using BlazorNativeSdk.Devices;
using Microsoft.Extensions.Logging;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>
/// <see cref="IFilePickerService"/> over the WinRT <see cref="FileOpenPicker"/>, <see cref="FileSavePicker"/> and
/// <see cref="FolderPicker"/>, parented to the host's main window with <c>InitializeWithWindow</c> and shown on its UI thread.
/// </summary>
/// <remarks>
/// The WinRT pickers cannot be used from an elevated (administrator) process; the service reports
/// <see cref="NativeResultStatus.Unavailable"/> there. Dialog titles are not supported by these pickers and are ignored.
/// Files without a file-system path (virtual or cloud items) are copied to a temporary file that is deleted when the
/// returned <see cref="NativeFile"/> is disposed.
/// </remarks>
internal sealed partial class WindowsFilePickerService : IFilePickerService
{
    private const int ERROR_INVALID_WINDOW_HANDLE = unchecked((int)0x80070578);
    private readonly IWindowsHostBridge _bridge;
    private readonly ILogger _logger;

    public WindowsFilePickerService(IWindowsHostBridge bridge, ILogger<WindowsFilePickerService> logger)
    {
        _bridge = bridge;
        _logger = logger;
    }

    public CapabilityId Capability => NativeCapabilities.Files;

    internal static bool IsElevated => Environment.IsPrivilegedProcess;

    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(IsElevated
            ? CapabilityStatus.Unavailable(Capability, WindowsAdapter.ElevatedProcessCode, ElevatedMessage, WindowsAdapter.Name)
            : CapabilityStatus.Available(Capability, WindowsAdapter.Name, FilePickerFeatures.Multiple, FilePickerFeatures.SaveDialog, FilePickerFeatures.FolderPicker));

    public ValueTask<NativeResult<IReadOnlyList<NativeFile>>> PickFilesAsync(FilePickOptions? options = null, CancellationToken cancellationToken = default) =>
        PickFilesCoreAsync(Capability, options ?? new FilePickOptions(), PickerLocationId.DocumentsLibrary, cancellationToken);

    /// <summary>Shared with the camera service, which picks media through the same picker.</summary>
    internal ValueTask<NativeResult<IReadOnlyList<NativeFile>>> PickFilesCoreAsync(CapabilityId capability, FilePickOptions options, PickerLocationId startLocation, CancellationToken cancellationToken) =>
        RunPickerAsync<IReadOnlyList<NativeFile>>(capability, async (hwnd, ct) =>
        {
            var extensions = CollectExtensions(options.FileTypes);
            var files = await _bridge.InvokeOnUIThreadAsync<IReadOnlyList<StorageFile>>(async () =>
            {
                var picker = new FileOpenPicker
                {
                    SuggestedStartLocation = startLocation,
                    ViewMode = startLocation is PickerLocationId.PicturesLibrary or PickerLocationId.VideosLibrary ? PickerViewMode.Thumbnail : PickerViewMode.List,
                };
                InitializeWithWindow.Initialize(picker, hwnd);
                foreach (var extension in extensions)
                {
                    picker.FileTypeFilter.Add(extension);
                }

                if (options.AllowMultiple)
                {
                    return await picker.PickMultipleFilesAsync().AsTask(ct).ConfigureAwait(true);
                }

                var single = await picker.PickSingleFileAsync().AsTask(ct).ConfigureAwait(true);
                return single is null ? [] : [single];
            }).ConfigureAwait(false);

            if (files is null || files.Count == 0)
            {
                return NativeError.Cancelled(capability, "The file picker was dismissed.");
            }

            var result = new List<NativeFile>(files.Count);
            try
            {
                foreach (var file in files)
                {
                    result.Add(await ToNativeFileAsync(file, ct).ConfigureAwait(false));
                }
            }
            catch
            {
                foreach (var created in result)
                {
                    await created.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }

            return NativeResult.Success<IReadOnlyList<NativeFile>>(result);
        }, cancellationToken);

    public ValueTask<NativeResult<SavedFile>> SaveFileAsync(FileSaveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SuggestedFileName);
        ArgumentNullException.ThrowIfNull(request.Content);

        return RunPickerAsync<SavedFile>(Capability, async (hwnd, ct) =>
        {
            var choices = BuildSaveChoices(request);
            var file = await _bridge.InvokeOnUIThreadAsync<StorageFile?>(async () =>
            {
                var picker = new FileSavePicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    SuggestedFileName = Path.GetFileNameWithoutExtension(request.SuggestedFileName),
                };
                InitializeWithWindow.Initialize(picker, hwnd);
                foreach (var (description, extensions) in choices)
                {
                    picker.FileTypeChoices.Add(description, extensions);
                }

                return await picker.PickSaveFileAsync().AsTask(ct).ConfigureAwait(true);
            }).ConfigureAwait(false);

            if (file is null)
            {
                return NativeError.Cancelled(Capability, "The save dialog was dismissed.");
            }

            var path = string.IsNullOrEmpty(file.Path) ? null : file.Path;
            if (path is not null)
            {
                var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                await using (stream.ConfigureAwait(false))
                {
                    await request.Content.CopyToAsync(stream, ct).ConfigureAwait(false);
                }
            }
            else
            {
                var stream = await file.OpenStreamForWriteAsync().ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    stream.SetLength(0);
                    await request.Content.CopyToAsync(stream, ct).ConfigureAwait(false);
                }
            }

            return new SavedFile(file.Name, path, FileSaveMethod.SaveDialog);
        }, cancellationToken);
    }

    public ValueTask<NativeResult<PickedFolder>> PickFolderAsync(string? title = null, CancellationToken cancellationToken = default) =>
        RunPickerAsync<PickedFolder>(Capability, async (hwnd, ct) =>
        {
            var folder = await _bridge.InvokeOnUIThreadAsync<StorageFolder?>(async () =>
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                InitializeWithWindow.Initialize(picker, hwnd);
                picker.FileTypeFilter.Add("*");
                return await picker.PickSingleFolderAsync().AsTask(ct).ConfigureAwait(true);
            }).ConfigureAwait(false);

            return folder is null
                ? NativeError.Cancelled(Capability, "The folder picker was dismissed.")
                : new PickedFolder(folder.Name, string.IsNullOrEmpty(folder.Path) ? null : folder.Path);
        }, cancellationToken);

    /// <summary>The <c>FileTypeFilter</c> entries: distinct lowercase extensions, or <c>*</c> when no filter is given.</summary>
    internal static List<string> CollectExtensions(IReadOnlyList<FileTypeFilter> filters)
    {
        var extensions = filters
            .SelectMany(static f => f.Extensions)
            .Where(static e => e.Length > 1 && e.StartsWith('.') && !e.Contains('*'))
            .Select(static e => e.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return extensions.Count > 0 ? extensions : ["*"];
    }

    /// <summary>Save dialog choices: one per (merged) filter description, or the suggested name's extension.</summary>
    internal static List<(string Description, List<string> Extensions)> BuildSaveChoices(FileSaveRequest request)
    {
        var choices = new List<(string Description, List<string> Extensions)>();
        foreach (var filter in request.FileTypes)
        {
            var extensions = filter.Extensions.Where(static e => e.Length > 1 && !e.Contains('*')).Select(static e => e.ToLowerInvariant()).ToList();
            if (extensions.Count == 0)
            {
                continue;
            }

            var description = string.IsNullOrWhiteSpace(filter.Description) ? "Files" : filter.Description;
            var existing = choices.FindIndex(c => string.Equals(c.Description, description, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                choices[existing].Extensions.AddRange(extensions.Except(choices[existing].Extensions, StringComparer.Ordinal));
            }
            else
            {
                choices.Add((description, extensions));
            }
        }

        if (choices.Count == 0)
        {
            var extension = Path.GetExtension(request.SuggestedFileName).ToLowerInvariant();
            choices.Add((extension.Length > 1 ? extension.TrimStart('.').ToUpperInvariant() + " file" : "File", [extension.Length > 1 ? extension : "."]));
        }

        return choices;
    }

    private async ValueTask<NativeResult<T>> RunPickerAsync<T>(CapabilityId capability, Func<nint, CancellationToken, Task<NativeResult<T>>> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsElevated)
        {
            return NativeError.Unavailable(capability, WindowsAdapter.ElevatedProcessCode, ElevatedMessage);
        }

        var hwnd = _bridge.MainWindowHandle;
        if (hwnd == 0)
        {
            return NativeError.Unavailable(capability, NativeErrorCodes.HostNotReady, "Pickers need a main window to attach to, and the application has none yet.");
        }

        try
        {
            return await operation(hwnd, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (COMException ex) when (ex.HResult == ERROR_INVALID_WINDOW_HANDLE)
        {
            LogPickerFailed(_logger, ex);
            return NativeError.Unavailable(capability, NativeErrorCodes.HostNotReady, "The picker could not attach to the main window (call from its UI thread or register the host adapter's IWindowsHostBridge).");
        }
        catch (Exception ex)
        {
            LogPickerFailed(_logger, ex);
            return NativeError.FromException(capability, ex);
        }
    }

    private static async Task<NativeFile> ToNativeFileAsync(StorageFile file, CancellationToken cancellationToken)
    {
        var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? null : file.ContentType;
        if (!string.IsNullOrEmpty(file.Path) && File.Exists(file.Path))
        {
            return new PhysicalNativeFile(file.Path, contentType);
        }

        // Virtual/cloud items without a local path: materialize a temporary copy owned by the returned NativeFile.
        var directory = Path.Combine(Path.GetTempPath(), "BlazorNativeSdk", "picked", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, WindowsAdapter.ToFolderName(file.Name));
        var source = await file.OpenStreamForReadAsync().ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await using (destination.ConfigureAwait(false))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }
        }

        return new PhysicalNativeFile(target, contentType, deleteOnDispose: true);
    }

    private const string ElevatedMessage =
        "Windows file pickers cannot be shown from an elevated (administrator) process. Run the app without elevation.";

    [LoggerMessage(EventId = 3601, Level = LogLevel.Warning, Message = "A file picker failed.")]
    private static partial void LogPickerFailed(ILogger logger, Exception exception);
}
