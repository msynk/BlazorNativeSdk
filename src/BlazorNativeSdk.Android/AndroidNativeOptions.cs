using BlazorNativeSdk.Devices;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>Options for the Android adapter registered by <c>AddAndroid()</c>.</summary>
public sealed class AndroidNativeOptions
{
    /// <summary>
    /// The Blazor host reported in <see cref="NativeHostInfo.Host"/>. Default: <see cref="NativeHostKind.Maui"/>. Use
    /// <see cref="NativeHostKind.Custom"/> for a plain .NET for Android app that hosts its own <c>WebView</c>.
    /// </summary>
    public NativeHostKind HostKind { get; set; } = NativeHostKind.Maui;

    /// <summary>
    /// The name of a drawable resource (for example <c>ic_stat_notify</c>) used as the notification small icon. It should
    /// be a white, transparent-background silhouette: Android renders the alpha channel only. Default:
    /// <see langword="null"/>, which falls back to the application icon (often rendered as a solid square).
    /// </summary>
    public string? NotificationSmallIcon { get; set; }

    /// <summary>
    /// The id of the notification channel used when <see cref="LocalNotification.ChannelId"/> is not set. The channel is
    /// created lazily on Android 8.0+ the first time a notification is posted. Default: <c>bns.default</c>.
    /// </summary>
    public string DefaultChannelId { get; set; } = "bns.default";

    /// <summary>The user-visible name of the default channel (shown in system settings). Default: <c>Notifications</c>.</summary>
    public string DefaultChannelName { get; set; } = "Notifications";

    /// <summary>The user-visible description of the default channel.</summary>
    public string? DefaultChannelDescription { get; set; }

    /// <summary>
    /// The importance of the default channel. On Android 8.0+ importance belongs to the channel, so
    /// <see cref="LocalNotification.Importance"/> only applies to notifications posted to a channel with that importance
    /// (create channels with <see cref="INotificationService.CreateChannelAsync"/>). Default: <see cref="NotificationImportance.Default"/>.
    /// </summary>
    public NotificationImportance DefaultChannelImportance { get; set; } = NotificationImportance.Default;

    /// <summary>
    /// Schedule background work with AndroidX WorkManager, which runs workers after the app is closed and after reboots.
    /// When <see langword="false"/>, the Core in-process scheduler is used instead and work only runs while the app is in
    /// the foreground. Default: <see langword="true"/>.
    /// </summary>
    public bool UseWorkManager { get; set; } = true;

    /// <summary>
    /// When the Google code scanner module is not installed yet, ask Google Play services to download it in the background
    /// so a later scan succeeds. Default: <see langword="true"/>. To download it at install time instead, add the
    /// <c>com.google.mlkit.vision.DEPENDENCIES</c> meta-data with the value <c>barcode_ui</c> to the app manifest.
    /// </summary>
    public bool InstallBarcodeScannerModule { get; set; } = true;

    /// <summary>
    /// How long a WorkManager run waits for the SDK to start (the app's service provider to become available) when the
    /// OS launched the process only to run work. When it does not start in time the run is retried later. Default: 20 seconds.
    /// </summary>
    public TimeSpan WorkerStartupTimeout { get; set; } = TimeSpan.FromSeconds(20);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(DefaultChannelId))
        {
            throw new InvalidOperationException($"{nameof(AndroidNativeOptions)}.{nameof(DefaultChannelId)} must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(DefaultChannelName))
        {
            throw new InvalidOperationException($"{nameof(AndroidNativeOptions)}.{nameof(DefaultChannelName)} must not be empty.");
        }

        if (WorkerStartupTimeout < TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{nameof(AndroidNativeOptions)}.{nameof(WorkerStartupTimeout)} must not be negative.");
        }
    }
}
