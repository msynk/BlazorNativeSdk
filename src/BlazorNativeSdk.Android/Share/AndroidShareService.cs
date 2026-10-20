using BlazorNativeSdk.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>
/// <see cref="IShareService"/> for Android based on Microsoft.Maui.Essentials <see cref="Share"/> (the system chooser).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Android does not report what the user picked in the chooser: success means the chooser was shown.</item>
/// <item>Files are shared through the Essentials <c>FileProvider</c>. Files without a local path (in-memory or picked
/// content) are copied to <c>{cache}/bns-share</c> first; copies older than a day are purged on the next share.</item>
/// <item>When files are shared, <see cref="ShareRequest.Text"/> and <see cref="ShareRequest.Uri"/> are not attached
/// (Essentials shares files without text); share them in a separate request.</item>
/// </list>
/// </remarks>
public sealed partial class AndroidShareService : IShareService
{
    private const string AdapterName = "android";
    private const string CacheArea = "bns-share";
    private readonly ILogger<AndroidShareService> _logger;

    /// <summary>Creates the service.</summary>
    public AndroidShareService(ILogger<AndroidShareService> logger) => _logger = logger;

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.Share;

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Available(Capability, AdapterName, ShareFeatures.Text, ShareFeatures.Uri, ShareFeatures.Files));

    /// <inheritdoc />
    public ValueTask<NativeResult> ShareAsync(ShareRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return NativeOperation.RunAsync(Capability, async ct =>
        {
            if (request.Files.Count > 0)
            {
                if (request.Text is not null || request.Uri is not null)
                {
                    LogTextDropped(_logger);
                }

                CacheFiles.PurgeOlderThan(CacheArea, TimeSpan.FromDays(1));
                var shareFiles = new List<ShareFile>(request.Files.Count);
                foreach (var file in request.Files)
                {
                    shareFiles.Add(await ToShareFileAsync(file, ct).ConfigureAwait(false));
                }

                var filesRequest = new ShareMultipleFilesRequest(request.Title ?? string.Empty, shareFiles);
                await MainThread.InvokeOnMainThreadAsync(() => Share.Default.RequestAsync(filesRequest)).WaitAsync(ct).ConfigureAwait(false);
                return NativeResult.Ok;
            }

            if (string.IsNullOrEmpty(request.Text) && request.Uri is null)
            {
                return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "Nothing to share: set Text, Uri or Files.");
            }

            var textRequest = new ShareTextRequest
            {
                Title = request.Title,
                Subject = request.Title,
                Text = request.Text,
                Uri = request.Uri?.AbsoluteUri,
            };
            await MainThread.InvokeOnMainThreadAsync(() => Share.Default.RequestAsync(textRequest)).WaitAsync(ct).ConfigureAwait(false);
            return NativeResult.Ok;
        }, cancellationToken);
    }

    private static async Task<ShareFile> ToShareFileAsync(NativeFile file, CancellationToken cancellationToken)
    {
        if (file.FullPath is { } path && File.Exists(path))
        {
            return new ShareFile(path, file.ContentType ?? MimeTypes.FromFileName(path) ?? MimeTypes.OctetStream);
        }

        var source = await file.OpenReadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var copy = await CacheFiles.CopyToCacheAsync(source, CacheArea, file.Name, file.ContentType, cancellationToken).ConfigureAwait(false);
            return new ShareFile(copy, file.ContentType ?? MimeTypes.FromFileName(copy) ?? MimeTypes.OctetStream);
        }
    }

    [LoggerMessage(EventId = 3601, Level = LogLevel.Debug, Message = "Text and URI are not attached when sharing files on Android.")]
    private static partial void LogTextDropped(ILogger logger);
}
