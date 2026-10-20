using Android.Content;
using BlazorNativeSdk.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;
using MauiClipboard = Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>
/// <see cref="IClipboardService"/> for Android based on Microsoft.Maui.Essentials <c>Clipboard</c>.
/// </summary>
/// <remarks>
/// Android 10+ only lets the focused foreground app read the clipboard and receive change notifications: in the background
/// <see cref="GetTextAsync"/> returns <see langword="null"/> and <see cref="ContentChanged"/> is not raised. Android 13+
/// shows a system confirmation when the app writes to the clipboard. Clipboard contents are never logged.
/// </remarks>
public sealed partial class AndroidClipboardService : IClipboardService, IDisposable, IAsyncDisposable
{
    private const string AdapterName = "android";
    private readonly Lock _gate = new();
    private readonly ILogger<AndroidClipboardService> _logger;
    private EventHandler? _contentChanged;
    private bool _subscribed;
    private int _disposed;

    /// <summary>Creates the service.</summary>
    public AndroidClipboardService(ILogger<AndroidClipboardService> logger) => _logger = logger;

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.Clipboard;

    /// <inheritdoc />
    public event EventHandler? ContentChanged
    {
        add
        {
            lock (_gate)
            {
                _contentChanged += value;
                if (!_subscribed && _contentChanged is not null && Volatile.Read(ref _disposed) == 0)
                {
                    MauiClipboard.Default.ClipboardContentChanged += OnClipboardContentChanged;
                    _subscribed = true;
                }
            }
        }

        remove
        {
            lock (_gate)
            {
                _contentChanged -= value;
                if (_subscribed && _contentChanged is null)
                {
                    MauiClipboard.Default.ClipboardContentChanged -= OnClipboardContentChanged;
                    _subscribed = false;
                }
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Available(Capability, AdapterName,
            ClipboardFeatures.ReadText, ClipboardFeatures.WriteText, ClipboardFeatures.ChangeNotification));

    /// <inheritdoc />
    public ValueTask<NativeResult<string?>> GetTextAsync(CancellationToken cancellationToken = default) =>
        NativeOperation.TryAsync<string?>(Capability,
            ct => MainThread.InvokeOnMainThreadAsync(() => MauiClipboard.Default.HasText ? MauiClipboard.Default.GetTextAsync() : Task.FromResult<string?>(null))
                .WaitAsync(ct),
            cancellationToken);

    /// <inheritdoc />
    /// <remarks><see langword="null"/> clears the clipboard (<c>clearPrimaryClip</c> on Android 9+, an empty clip below).</remarks>
    public ValueTask<NativeResult> SetTextAsync(string? text, CancellationToken cancellationToken = default) =>
        NativeOperation.TryAsync(Capability, ct => MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (text is null && OperatingSystem.IsAndroidVersionAtLeast(28) &&
                AndroidContext.App.GetSystemService(Context.ClipboardService) is ClipboardManager manager)
            {
                manager.ClearPrimaryClip();
                return Task.CompletedTask;
            }

            return MauiClipboard.Default.SetTextAsync(text ?? string.Empty);
        }).WaitAsync(ct), cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_subscribed)
            {
                MauiClipboard.Default.ClipboardContentChanged -= OnClipboardContentChanged;
                _subscribed = false;
            }

            _contentChanged = null;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void OnClipboardContentChanged(object? sender, EventArgs e)
    {
        EventHandler? handler;
        lock (_gate)
        {
            handler = _contentChanged;
        }

        try
        {
            handler?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            LogHandlerFailed(_logger, ex);
        }
    }

    [LoggerMessage(EventId = 3701, Level = LogLevel.Error, Message = "A clipboard ContentChanged handler failed.")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception);
}
