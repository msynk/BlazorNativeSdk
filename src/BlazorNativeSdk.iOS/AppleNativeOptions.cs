using Security;
using UserNotifications;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>Options for the iOS / Mac Catalyst adapter (<c>builder.AddApple(o => ...)</c>).</summary>
public sealed class AppleNativeOptions
{
    /// <summary>The Blazor host that runs on top of the adapter. Default: <see cref="NativeHostKind.Maui"/>.</summary>
    public NativeHostKind HostKind { get; set; } = NativeHostKind.Maui;

    /// <summary>
    /// The Keychain service (<c>kSecAttrService</c>) that scopes secure storage items. Default: <see cref="NativeSdkOptions.AppId"/>.
    /// Changing it makes previously stored values invisible.
    /// </summary>
    public string? KeychainService { get; set; }

    /// <summary>
    /// The Keychain access group (<c>kSecAttrAccessGroup</c>) used to share items with app extensions or other apps from the same team.
    /// It must be listed in the app's <c>keychain-access-groups</c> entitlement. Default: the app's default group.
    /// </summary>
    public string? KeychainAccessGroup { get; set; }

    /// <summary>
    /// When Keychain items can be read. Default: <see cref="SecAccessible.AfterFirstUnlockThisDeviceOnly"/>, which lets background
    /// work read secrets after the first unlock and never migrates them to another device or iCloud Keychain.
    /// Use <see cref="SecAccessible.WhenUnlockedThisDeviceOnly"/> for secrets that are only needed in the foreground.
    /// </summary>
    public SecAccessible KeychainAccessible { get; set; } = SecAccessible.AfterFirstUnlockThisDeviceOnly;

    /// <summary>
    /// The <c>BGAppRefreshTask</c> identifier used for short background work. It must match the identifier passed to
    /// <see cref="AppleNativeSdk.Initialize(string?, string?)"/> (default <see cref="AppleNativeSdk.DefaultRefreshTaskIdentifier"/>) and be
    /// listed in Info.plist <c>BGTaskSchedulerPermittedIdentifiers</c>. <see langword="null"/> uses the registered identifier.
    /// </summary>
    public string? RefreshTaskIdentifier { get; set; }

    /// <summary>
    /// The <c>BGProcessingTask</c> identifier used for <c>WorkRequest.IsLongRunning</c> work. Same rules as
    /// <see cref="RefreshTaskIdentifier"/>; default <see cref="AppleNativeSdk.DefaultProcessingTaskIdentifier"/>.
    /// </summary>
    public string? ProcessingTaskIdentifier { get; set; }

    /// <summary>
    /// On iOS, run transfers (<see cref="Lifecycle.ITransferService"/>) in a background <c>URLSession</c>
    /// (<see cref="AppleBackgroundTransferService"/>), so they continue while the app is suspended or closed. When
    /// <see langword="false"/>, the Core in-process queue is used and transfers pause while the app is in the background.
    /// Mac Catalyst always uses the in-process queue (apps are not suspended there). Default: <see langword="true"/>.
    /// </summary>
    public bool UseBackgroundUrlSession { get; set; } = true;

    /// <summary>
    /// The identifier of the background <c>URLSession</c> used for transfers. Default:
    /// <see cref="AppleBackgroundTransfers.DefaultSessionIdentifier"/> (<c>{CFBundleIdentifier}.bns.transfers</c>). Changing it
    /// orphans transfers that are in flight in the previous session.
    /// </summary>
    public string? BackgroundTransferSessionIdentifier { get; set; }

    /// <summary>
    /// How notifications are presented while the app is in the foreground (see <see cref="NativeSdkNotificationCenterDelegate"/>).
    /// Default: banner, Notification Center list, sound and badge. Use <see cref="UNNotificationPresentationOptions.None"/> to
    /// suppress them while the app is open.
    /// </summary>
    public UNNotificationPresentationOptions ForegroundPresentationOptions { get; set; } =
        UNNotificationPresentationOptions.Banner | UNNotificationPresentationOptions.List |
        UNNotificationPresentationOptions.Sound | UNNotificationPresentationOptions.Badge;
}
