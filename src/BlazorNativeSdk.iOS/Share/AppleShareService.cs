using BlazorNativeSdk.Devices;
using LinkPresentation;
using Microsoft.Extensions.Logging;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary><see cref="IShareService"/> backed by <c>UIActivityViewController</c>.</summary>
/// <remarks>
/// The share sheet reports whether the user completed an activity or dismissed the sheet, so the result is
/// <see cref="NativeResultStatus.Success"/> or <see cref="NativeResultStatus.Cancelled"/>
/// (<see cref="ShareFeatures.CompletionReporting"/>). <see cref="ShareRequest.Title"/> becomes the share sheet header and the
/// subject of e-mail and similar activities. Files without a local path are copied to the temporary directory for the duration
/// of the share. On iPad and Mac the sheet is a popover anchored to the center of the current window.
/// </remarks>
public sealed partial class AppleShareService : IShareService
{
    private readonly ILogger<AppleShareService> _logger;

    /// <summary>Creates the service.</summary>
    public AppleShareService(ILogger<AppleShareService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.Share;

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Available(Capability, AppleHost.AdapterName,
            ShareFeatures.Text, ShareFeatures.Uri, ShareFeatures.Files, ShareFeatures.CompletionReporting));

    /// <inheritdoc />
    public ValueTask<NativeResult> ShareAsync(ShareRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return NativeOperation.RunAsync(Capability, async token =>
        {
            if (string.IsNullOrEmpty(request.Text) && request.Uri is null && request.Files.Count == 0)
            {
                return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "A share request needs text, a URI or at least one file.");
            }

            if (request.Uri is { IsAbsoluteUri: false })
            {
                return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "ShareRequest.Uri must be an absolute URI.");
            }

            var temporaryFiles = new List<string>();
            var items = new List<NSObject>();
            try
            {
                var title = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title;
                if (!string.IsNullOrEmpty(request.Text))
                {
                    items.Add(Wrap(new NSString(request.Text), title));
                    title = null;
                }

                if (request.Uri is { } uri)
                {
                    items.Add(Wrap(new NSUrl(uri.AbsoluteUri), title));
                    title = null;
                }

                foreach (var file in request.Files)
                {
                    var (path, isTemporaryCopy) = await AppleFiles.MaterializeAsync(file, token).ConfigureAwait(false);
                    if (isTemporaryCopy)
                    {
                        temporaryFiles.Add(path);
                    }

                    items.Add(Wrap(NSUrl.FromFilename(path), title));
                    title = null;
                }

                return await PresentAsync([.. items], token).ConfigureAwait(false);
            }
            finally
            {
                foreach (var item in items)
                {
                    item.Dispose();
                }

                foreach (var path in temporaryFiles)
                {
                    AppleFiles.TryDelete(path);
                }
            }
        }, cancellationToken);
    }

    private async Task<NativeResult> PresentAsync(NSObject[] items, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<(bool Completed, NSError? Error, string? ActivityType)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = await AppleUI.OnMainThreadAsync(async () =>
        {
            if (AppleUI.GetTopViewController() is not { } presenter)
            {
                return null;
            }

            var sheet = new UIActivityViewController(items, null)
            {
                CompletionWithItemsHandler = (activityType, completed, _, error) => completion.TrySetResult((completed, error, activityType?.ToString())),
            };
            AppleUI.AnchorPopover(sheet, presenter);
            await presenter.PresentViewControllerAsync(sheet, true).ConfigureAwait(true);
            return sheet;
        }).ConfigureAwait(false);

        if (controller is null)
        {
            return AppleUI.NoPresenter(Capability);
        }

        try
        {
            var (completed, error, activityType) = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                LogShareFailed(_logger, (long)error.Code);
                return NativeError.Failed(Capability, NativeErrorCodes.PlatformError, error.LocalizedDescription);
            }

            if (!completed)
            {
                return NativeError.Cancelled(Capability, "The user dismissed the share sheet.");
            }

            LogShared(_logger, activityType ?? "unknown");
            return NativeResult.Ok;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await AppleUI.OnMainThreadAsync(() => AppleUI.DismissAsync(controller)).ConfigureAwait(false);
            throw;
        }
        finally
        {
            AppleUI.ReleaseOnMainThread(controller);
        }
    }

    private static NSObject Wrap(NSObject item, string? title) => title is null ? item : new TitledItemSource(item, title);

    [LoggerMessage(EventId = 3851, Level = LogLevel.Debug, Message = "Content shared through {ActivityType}.")]
    private static partial void LogShared(ILogger logger, string activityType);

    [LoggerMessage(EventId = 3852, Level = LogLevel.Warning, Message = "The share activity failed with code {Code}.")]
    private static partial void LogShareFailed(ILogger logger, long code);

    /// <summary>Provides an item together with a title for the sheet header and the subject of mail-like activities.</summary>
    private sealed class TitledItemSource(NSObject item, string title) : UIActivityItemSource
    {
        public override NSObject GetPlaceholderData(UIActivityViewController activityViewController) => item;

        public override NSObject GetItemForActivity(UIActivityViewController activityViewController, NSString? activityType) => item;

        public override string GetSubjectForActivity(UIActivityViewController activityViewController, NSString? activityType) => title;

        public override LPLinkMetadata GetLinkMetadata(UIActivityViewController activityViewController)
        {
            var metadata = new LPLinkMetadata { Title = title };
            if (item is NSUrl url && !url.IsFileUrl)
            {
                metadata.OriginalUrl = url;
                metadata.Url = url;
            }

            return metadata;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                item.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
