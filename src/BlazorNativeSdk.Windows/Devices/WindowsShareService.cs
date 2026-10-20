using BlazorNativeSdk.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;

namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>
/// <see cref="IShareService"/> over the Windows share UI, opened for the host's main window through
/// <c>IDataTransferManagerInterop</c>. The share UI does not report what the user did, so success means "presented".
/// </summary>
/// <remarks>
/// Files that are not on disk are copied to a temporary folder first (share targets read them after the call returns);
/// those copies are removed on later shares once they are older than an hour.
/// </remarks>
internal sealed partial class WindowsShareService : IShareService, IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan s_tempRetention = TimeSpan.FromHours(1);
    private readonly IWindowsHostBridge _bridge;
    private readonly NativeSdkOptions _sdkOptions;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private PendingShare? _pending;
    private int _disposed;

    public WindowsShareService(IWindowsHostBridge bridge, IOptions<NativeSdkOptions> sdkOptions, ILogger<WindowsShareService> logger)
    {
        _bridge = bridge;
        _sdkOptions = sdkOptions.Value;
        _logger = logger;
    }

    public CapabilityId Capability => NativeCapabilities.Share;

    private static string TempRoot => Path.Combine(Path.GetTempPath(), "BlazorNativeSdk", "share");

    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return ValueTask.FromResult(DataTransferManager.IsSupported()
                ? CapabilityStatus.Available(Capability, WindowsAdapter.Name, ShareFeatures.Text, ShareFeatures.Uri, ShareFeatures.Files)
                : CapabilityStatus.Unsupported(Capability, "The Windows share UI is not available on this system.", WindowsAdapter.Name));
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(CapabilityStatus.FromError(NativeError.FromException(Capability, ex), WindowsAdapter.Name));
        }
    }

    public ValueTask<NativeResult> ShareAsync(ShareRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return NativeOperation.RunAsync(Capability, ct => ShareCoreAsync(request, ct), cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        PendingShare? pending;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
        }

        pending?.Detach();
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private async ValueTask<NativeResult> ShareCoreAsync(ShareRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (string.IsNullOrEmpty(request.Text) && request.Uri is null && request.Files.Count == 0)
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "Nothing to share: set Text, Uri or Files.");
        }

        if (request.Uri is { IsAbsoluteUri: false })
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "Shared links must be absolute URIs.");
        }

        if (!DataTransferManager.IsSupported())
        {
            return NativeError.Unsupported(Capability, "The Windows share UI is not available on this system.");
        }

        var hwnd = _bridge.MainWindowHandle;
        if (hwnd == 0)
        {
            return NativeError.Unavailable(Capability, NativeErrorCodes.HostNotReady, "The share UI needs a main window, and the application has none yet.");
        }

        CleanupOldTempFiles();
        var items = new List<IStorageItem>(request.Files.Count);
        foreach (var file in request.Files)
        {
            var path = file.FullPath ?? await CopyToTempAsync(file, cancellationToken).ConfigureAwait(false);
            items.Add(await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken).ConfigureAwait(false));
        }

        var content = new ShareContent(
            string.IsNullOrWhiteSpace(request.Title) ? _sdkOptions.DisplayName : request.Title,
            request.Text,
            request.Uri,
            items);

        await _bridge.InvokeOnUIThreadAsync(() =>
        {
            var manager = WinRTInterop.GetDataTransferManagerForWindow(hwnd);
            var pending = new PendingShare(manager, content, _logger);
            PendingShare? previous;
            lock (_gate)
            {
                previous = _pending;
                _pending = pending;
            }

            // A previous request whose DataRequested never fired must not answer this one with stale data.
            previous?.Detach();
            pending.Attach();
            try
            {
                WinRTInterop.ShowShareUIForWindow(hwnd);
            }
            catch
            {
                pending.Detach();
                throw;
            }

            return Task.CompletedTask;
        }).ConfigureAwait(false);

        return NativeResult.Ok;
    }

    private static async Task<string> CopyToTempAsync(NativeFile file, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, WindowsAdapter.ToFolderName(file.Name));
        var source = await file.OpenReadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await using (destination.ConfigureAwait(false))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }
        }

        return path;
    }

    private void CleanupOldTempFiles()
    {
        try
        {
            if (!Directory.Exists(TempRoot))
            {
                return;
            }

            var cutoff = DateTime.UtcNow - s_tempRetention;
            foreach (var directory in Directory.EnumerateDirectories(TempRoot))
            {
                if (Directory.GetCreationTimeUtc(directory) < cutoff)
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCleanupFailed(_logger, ex);
        }
    }

    [LoggerMessage(EventId = 3701, Level = LogLevel.Debug, Message = "Old shared temporary files could not be removed.")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3702, Level = LogLevel.Error, Message = "Filling the share request failed.")]
    private static partial void LogDataRequestFailed(ILogger logger, Exception exception);

    private sealed record ShareContent(string Title, string? Text, Uri? Uri, IReadOnlyList<IStorageItem> Items);

    /// <summary>A one-shot DataRequested subscription for one share.</summary>
    private sealed class PendingShare
    {
        private readonly DataTransferManager _manager;
        private readonly ShareContent _content;
        private readonly ILogger _logger;
        private readonly TypedEventHandler<DataTransferManager, DataRequestedEventArgs> _handler;
        private int _detached;

        public PendingShare(DataTransferManager manager, ShareContent content, ILogger logger)
        {
            _manager = manager;
            _content = content;
            _logger = logger;
            _handler = OnDataRequested;
        }

        public void Attach() => _manager.DataRequested += _handler;

        public void Detach()
        {
            if (Interlocked.Exchange(ref _detached, 1) == 0)
            {
                _manager.DataRequested -= _handler;
            }
        }

        private void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
        {
            Detach();
            var content = _content;
            try
            {
                var data = args.Request.Data;
                data.Properties.Title = content.Title;
                if (!string.IsNullOrEmpty(content.Text))
                {
                    data.SetText(content.Text);
                }

                if (content.Uri is { } uri)
                {
                    if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                    {
                        data.SetWebLink(uri);
                    }
                    else
                    {
                        data.SetApplicationLink(uri);
                    }
                }

                if (content.Items.Count > 0)
                {
                    data.SetStorageItems(content.Items);
                }
            }
            catch (Exception ex)
            {
                LogDataRequestFailed(_logger, ex);
                args.Request.FailWithDisplayText("The content could not be shared.");
            }
        }
    }
}
