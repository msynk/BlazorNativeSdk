namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>Options for the Windows adapter (<c>builder.AddWindows(o =&gt; ...)</c>).</summary>
public sealed class WindowsNativeOptions
{
    /// <summary>
    /// The Application User Model ID used for toast notifications (and, for unpackaged apps, set on the process).
    /// Defaults to <see cref="NativeSdkOptions.AppId"/>. Must not contain spaces and is limited to 128 characters; invalid
    /// characters are replaced. Ignored by MSIX-packaged apps, whose AUMID comes from the package.
    /// </summary>
    public string? AppUserModelId { get; set; }

    /// <summary>
    /// For unpackaged apps, register the AUMID under <c>HKCU\Software\Classes\AppUserModelId</c> (display name and icon) so
    /// toasts can be shown without a Start menu shortcut. Default: <see langword="true"/>.
    /// </summary>
    public bool RegisterAppUserModelId { get; set; } = true;

    /// <summary>
    /// For unpackaged apps, call <c>SetCurrentProcessExplicitAppUserModelID</c> with the AUMID. This also changes how the
    /// taskbar groups the app's windows, so it should happen before the first window is shown (resolve
    /// <c>INotificationService</c> early, or set it yourself). Default: <see langword="true"/>.
    /// </summary>
    public bool SetProcessAppUserModelId { get; set; } = true;

    /// <summary>
    /// A custom URL scheme registered for the app (for example with <c>IProtocolRegistrationService</c> from
    /// <c>BlazorNativeSdk.Desktop</c>), such as <c>myapp</c>. When set, toasts use protocol activation: clicking a toast
    /// or one of its buttons launches the app (or reaches the running instance through single-instance forwarding) with a
    /// signed <c>myapp://bns-toast/?...</c> URI, which the adapter turns back into a notification activation.
    /// When <see langword="null"/>, toast clicks are only delivered while the process that showed them is running.
    /// </summary>
    public string? NotificationActivationScheme { get; set; }

    /// <summary>An absolute path to a .png or .ico shown as the app icon on toasts of unpackaged apps.</summary>
    public string? NotificationIconPath { get; set; }

    /// <summary>The directory for <c>ISecureStorage</c> files. Defaults to <c>%LOCALAPPDATA%\{AppId}\securestorage</c> (the package's local folder for packaged apps).</summary>
    public string? SecureStorageDirectory { get; set; }
}
