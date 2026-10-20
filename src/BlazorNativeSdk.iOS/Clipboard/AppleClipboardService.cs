using BlazorNativeSdk.Devices;
using Microsoft.Extensions.Logging;
using MainThread = Microsoft.Maui.ApplicationModel.MainThread;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary><see cref="IClipboardService"/> backed by <c>UIPasteboard.General</c>.</summary>
/// <remarks>
/// <para>
/// On iOS 16 and later, reading text that another app copied shows the system "Allow Paste" prompt; the prompt is not shown
/// for content the app copied itself, nor on Mac Catalyst. <see cref="GetTextAsync"/> first checks <c>HasStrings</c>, which never
/// prompts, so an empty or non-text clipboard returns <see langword="null"/> without a prompt. If the user declines the prompt the
/// result is a successful <see langword="null"/>.
/// </para>
/// <para>
/// <see cref="ContentChanged"/> is raised (on the main thread) for <c>UIPasteboardChangedNotification</c>, and when the app
/// returns to the foreground after another app changed the clipboard (detected through <c>changeCount</c>, which reveals nothing
/// about the content).
/// </para>
/// </remarks>
public sealed partial class AppleClipboardService : IClipboardService, IDisposable, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly ILogger<AppleClipboardService> _logger;
    private NSObject? _changedObserver;
    private NSObject? _activeObserver;
    private nint _lastChangeCount = -1;
    private int _disposed;

    /// <summary>Creates the service.</summary>
    public AppleClipboardService(ILogger<AppleClipboardService> logger)
    {
        _logger = logger;
        _changedObserver = UIPasteboard.Notifications.ObserveChanged((_, _) => OnPossibleChange(force: true));
        _activeObserver = UIApplication.Notifications.ObserveDidBecomeActive((_, _) => OnPossibleChange(force: false));
        if (NSThread.IsMain)
        {
            _lastChangeCount = UIPasteboard.General.ChangeCount;
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(() => _lastChangeCount = UIPasteboard.General.ChangeCount);
        }
    }

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.Clipboard;

    /// <inheritdoc />
    public event EventHandler? ContentChanged;

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Available(Capability, AppleHost.AdapterName,
            ClipboardFeatures.ReadText, ClipboardFeatures.WriteText, ClipboardFeatures.ChangeNotification));

    /// <inheritdoc />
    public ValueTask<NativeResult<string?>> GetTextAsync(CancellationToken cancellationToken = default) =>
        NativeOperation.RunAsync<string?>(Capability, async token =>
        {
            var text = await AppleUI.OnMainThreadAsync(() =>
            {
                var pasteboard = UIPasteboard.General;
                return pasteboard.HasStrings ? pasteboard.String : null;
            }).WaitAsync(token).ConfigureAwait(false);
            return NativeResult.Success(text);
        }, cancellationToken);

    /// <inheritdoc />
    public ValueTask<NativeResult> SetTextAsync(string? text, CancellationToken cancellationToken = default) =>
        NativeOperation.RunAsync(Capability, async token =>
        {
            await AppleUI.OnMainThreadAsync(() =>
            {
                var pasteboard = UIPasteboard.General;
                if (text is null)
                {
                    pasteboard.Items = [];
                }
                else
                {
                    pasteboard.String = text;
                }
            }).WaitAsync(token).ConfigureAwait(false);
            return NativeResult.Ok;
        }, cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        NSObject? changed;
        NSObject? active;
        lock (_gate)
        {
            changed = _changedObserver;
            active = _activeObserver;
            _changedObserver = null;
            _activeObserver = null;
        }

        changed?.Dispose();
        active?.Dispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    // Notifications are delivered on the main thread, where UIPasteboard may be used.
    private void OnPossibleChange(bool force)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var changeCount = UIPasteboard.General.ChangeCount;
        lock (_gate)
        {
            if (!force && changeCount == _lastChangeCount)
            {
                return;
            }

            _lastChangeCount = changeCount;
        }

        try
        {
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            LogHandlerFailed(_logger, ex);
        }
    }

    [LoggerMessage(EventId = 3871, Level = LogLevel.Error, Message = "A clipboard change handler failed.")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception);
}
