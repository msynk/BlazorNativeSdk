namespace BlazorNativeSdk;

/// <summary>Well-known capability identifiers.</summary>
public static class NativeCapabilities
{
    // Device capabilities (mobile, desktop and browser).

    /// <summary>Photo/video capture and media picking. See <c>ICameraService</c>.</summary>
    public static readonly CapabilityId Camera = new("camera");

    /// <summary>Barcode and QR code scanning. See <c>IBarcodeScanner</c>.</summary>
    public static readonly CapabilityId Barcode = new("barcode");

    /// <summary>Opening and saving user files through native pickers. See <c>IFilePickerService</c>.</summary>
    public static readonly CapabilityId Files = new("files");

    /// <summary>App-private directories on the local file system. See <c>IAppStorage</c>.</summary>
    public static readonly CapabilityId AppStorage = new("app-storage");

    /// <summary>Encrypted key/value storage for secrets. See <c>ISecureStorage</c>.</summary>
    public static readonly CapabilityId SecureStorage = new("secure-storage");

    /// <summary>The native share sheet. See <c>IShareService</c>.</summary>
    public static readonly CapabilityId Share = new("share");

    /// <summary>Biometric and device-credential authentication. See <c>IBiometricService</c>.</summary>
    public static readonly CapabilityId Biometrics = new("biometrics");

    /// <summary>The system clipboard. See <c>IClipboardService</c>.</summary>
    public static readonly CapabilityId Clipboard = new("clipboard");

    /// <summary>Runtime permission checks and requests. See <c>IPermissionService</c>.</summary>
    public static readonly CapabilityId Permissions = new("permissions");

    /// <summary>Local notifications. See <c>INotificationService</c>.</summary>
    public static readonly CapabilityId Notifications = new("notifications");

    /// <summary>App activation: deep links, notification taps and launch arguments. See <c>IAppActivationService</c>.</summary>
    public static readonly CapabilityId DeepLinks = new("deep-links");

    // Lifecycle and background work.

    /// <summary>Application lifecycle state and transitions. See <c>IAppLifecycle</c>.</summary>
    public static readonly CapabilityId Lifecycle = new("lifecycle");

    /// <summary>Network connectivity state. See <c>IConnectivityService</c>.</summary>
    public static readonly CapabilityId Connectivity = new("connectivity");

    /// <summary>Deferred and background work scheduling. See <c>IBackgroundWorkScheduler</c>.</summary>
    public static readonly CapabilityId BackgroundWork = new("background-work");

    /// <summary>Durable file uploads and downloads. See <c>ITransferService</c>.</summary>
    public static readonly CapabilityId Transfers = new("transfers");

    /// <summary>Checkpoints for resuming interrupted workflows. See <c>IWorkflowCheckpointStore</c>.</summary>
    public static readonly CapabilityId WorkflowCheckpoints = new("workflow-checkpoints");

    // Desktop shell.

    /// <summary>Multi-window management. See <c>IWindowManager</c>.</summary>
    public static readonly CapabilityId Windows = new("desktop.windows");

    /// <summary>Displays/monitors information. See <c>IScreenService</c>.</summary>
    public static readonly CapabilityId Screens = new("desktop.screens");

    /// <summary>System-wide keyboard shortcuts. See <c>IGlobalShortcutService</c>.</summary>
    public static readonly CapabilityId GlobalShortcuts = new("desktop.global-shortcuts");

    /// <summary>Notification-area (tray / menu bar extra) icons. See <c>ITrayService</c>.</summary>
    public static readonly CapabilityId Tray = new("desktop.tray");

    /// <summary>Native application and context menus. See <c>INativeMenuService</c>.</summary>
    public static readonly CapabilityId Menus = new("desktop.menus");

    /// <summary>Shell file operations (reveal, open with default app, move to trash). See <c>IDesktopFileOperations</c>.</summary>
    public static readonly CapabilityId DesktopFiles = new("desktop.files");

    /// <summary>Authenticated inter-process messaging. See <c>IIpcService</c>.</summary>
    public static readonly CapabilityId Ipc = new("desktop.ipc");

    /// <summary>Single-instance enforcement with argument forwarding. See <c>ISingleInstanceService</c>.</summary>
    public static readonly CapabilityId SingleInstance = new("desktop.single-instance");

    /// <summary>Runtime registration of custom URL schemes. See <c>IProtocolRegistrationService</c>.</summary>
    public static readonly CapabilityId ProtocolRegistration = new("desktop.protocol-registration");

    /// <summary>All well-known capabilities, in documentation order.</summary>
    public static IReadOnlyList<CapabilityId> All { get; } =
    [
        Camera, Barcode, Files, AppStorage, SecureStorage, Share, Biometrics, Clipboard, Permissions, Notifications, DeepLinks,
        Lifecycle, Connectivity, BackgroundWork, Transfers, WorkflowCheckpoints,
        Windows, Screens, GlobalShortcuts, Tray, Menus, DesktopFiles, Ipc, SingleInstance, ProtocolRegistration,
    ];
}
