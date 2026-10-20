using BlazorNativeSdk.Browser.Interop;
using BlazorNativeSdk.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace BlazorNativeSdk.Browser.Services;

/// <summary>
/// Opens files with <c>showOpenFilePicker</c> (File System Access API) or a hidden file input, and saves files with
/// <c>showSaveFilePicker</c> or as a download. Content is streamed between .NET and the browser.
/// </summary>
internal sealed partial class BrowserFilePickerService : IFilePickerService
{
    internal const string FolderUnsupportedReason =
        "Browsers do not expose folder paths. showDirectoryPicker returns a sandboxed directory handle that is not an " +
        "addressable path and is only available in Chromium-based browsers, so folder picking is not offered.";

    private readonly BrowserInterop _interop;
    private readonly ILogger<BrowserFilePickerService> _logger;

    public BrowserFilePickerService(BrowserInterop interop, ILogger<BrowserFilePickerService> logger)
    {
        _interop = interop;
        _logger = logger;
    }

    public CapabilityId Capability => NativeCapabilities.Files;

    public async ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var probe = await _interop.ProbeAsync(Capability, "files", cancellationToken).ConfigureAwait(false);
        if (!probe.IsSuccess)
        {
            return BrowserInteropExtensions.ToStatus(Capability, probe.Error);
        }

        var features = new List<string> { FilePickerFeatures.Multiple };
        features.Add(probe.Value.SaveFilePicker && _interop.Options.UseFileSystemAccessApi ? FilePickerFeatures.SaveDialog : FilePickerFeatures.Download);
        return CapabilityStatus.Available(Capability, BrowserConstants.AdapterName, features);
    }

    public ValueTask<NativeResult<IReadOnlyList<NativeFile>>> PickFilesAsync(FilePickOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new FilePickOptions();
        return NativeOperation.RunAsync<IReadOnlyList<NativeFile>>(Capability, async ct =>
        {
            var request = new FilePickRequestDto
            {
                Multiple = options.AllowMultiple,
                Types = BrowserMediaMapper.ToFileTypes(options.FileTypes),
                Accept = BrowserMediaMapper.ToAccept(options.FileTypes),
                UseFileSystemAccess = _interop.Options.UseFileSystemAccessApi,
            };

            var operationId = BrowserInteropExtensions.NewOperationId();
            var result = await _interop.InvokeInteractiveAsync(
                Capability, "pickFiles", operationId, [BrowserInterop.Serialize(request, BrowserJsonContext.Default.FilePickRequestDto)], ct).ConfigureAwait(false);
            var files = BrowserInterop.Read(Capability, result, BrowserJsonContext.Default.BrowserFileDtoArray);
            if (!files.IsSuccess)
            {
                LogFailed(_logger, "open", files.Error.Code);
                return files.Error;
            }

            if (files.Value.Length == 0)
            {
                return NativeError.Cancelled(Capability);
            }

            return await _interop.TakeFilesAsync(Capability, operationId, files.Value, "file", ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public ValueTask<NativeResult<SavedFile>> SaveFileAsync(FileSaveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SuggestedFileName) || request.Content is not { CanRead: true })
        {
            return ValueTask.FromResult(NativeResult.Failure<SavedFile>(NativeError.Failed(
                Capability, NativeErrorCodes.InvalidArgument, "A file name and a readable content stream are required.")));
        }

        return NativeOperation.RunAsync<SavedFile>(Capability, async ct =>
        {
            var dto = new FileSaveRequestDto
            {
                SuggestedName = Path.GetFileName(request.SuggestedFileName),
                ContentType = request.ContentType ?? MimeTypes.FromFileName(request.SuggestedFileName) ?? MimeTypes.OctetStream,
                Types = BrowserMediaMapper.ToFileTypes(request.FileTypes),
                UseFileSystemAccess = _interop.Options.UseFileSystemAccessApi,
            };

            // The stream is read by the browser only after the user picked a location, so the save dialog still has the
            // user activation of the click that started the save. leaveOpen: the caller owns the stream.
            using var content = new DotNetStreamReference(request.Content, leaveOpen: true);
            var result = await _interop.InvokeInteractiveAsync(
                Capability, "saveFile", BrowserInteropExtensions.NewOperationId(),
                [BrowserInterop.Serialize(dto, BrowserJsonContext.Default.FileSaveRequestDto), content], ct).ConfigureAwait(false);
            var saved = BrowserInterop.Read(Capability, result, BrowserJsonContext.Default.SavedFileDto);
            if (!saved.IsSuccess)
            {
                LogFailed(_logger, "save", saved.Error.Code);
                return saved.Error;
            }

            var method = saved.Value.Method == "dialog" ? FileSaveMethod.SaveDialog : FileSaveMethod.Download;
            return new SavedFile(string.IsNullOrWhiteSpace(saved.Value.Name) ? dto.SuggestedName : saved.Value.Name, null, method);
        }, cancellationToken);
    }

    public ValueTask<NativeResult<PickedFolder>> PickFolderAsync(string? title = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(NativeResult.Failure<PickedFolder>(NativeError.Unsupported(Capability, FolderUnsupportedReason)));

    [LoggerMessage(EventId = 3301, Level = LogLevel.Debug, Message = "Browser file {Operation} did not complete: {Code}.")]
    private static partial void LogFailed(ILogger logger, string operation, string code);
}
