using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;

namespace BlazorNativeSdk.Unsupported;

/// <summary>
/// Base class for the fallbacks registered when no adapter provides a capability. Every operation returns
/// <see cref="NativeResultStatus.Unsupported"/> with <see cref="NativeErrorCodes.NoAdapter"/> and a message that tells the
/// developer which adapter to add. Status reports <see cref="CapabilityAvailability.Unsupported"/>.
/// </summary>
public abstract class UnsupportedCapability : INativeCapability
{
    /// <summary>The adapter name reported by fallbacks.</summary>
    public const string AdapterName = "unsupported";

    /// <summary>Creates a fallback for a capability.</summary>
    protected UnsupportedCapability(CapabilityId capability, NativeHostInfo host)
    {
        Capability = capability;
        Host = host;
    }

    /// <inheritdoc />
    public CapabilityId Capability { get; }

    /// <summary>The current host.</summary>
    protected NativeHostInfo Host { get; }

    /// <summary>The error returned by every operation.</summary>
    protected NativeError Error => NativeError.NoAdapter(Capability, Host.Platform == NativePlatform.Unknown ? null : Host.ToString());

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Unsupported(Capability, Error.Message, AdapterName, NativeErrorCodes.NoAdapter));

    /// <summary>Returns a completed failed result.</summary>
    protected ValueTask<NativeResult> Fail() => ValueTask.FromResult(NativeResult.Failure(Error));

    /// <summary>Returns a completed failed result.</summary>
    protected ValueTask<NativeResult<T>> Fail<T>() => ValueTask.FromResult(NativeResult.Failure<T>(Error));
}

#pragma warning disable CS1591 // Fallback members implement documented interfaces.
#pragma warning disable CS0067 // Events are part of the contract but never raised by fallbacks.

public sealed class UnsupportedCameraService(NativeHostInfo host) : UnsupportedCapability(NativeCapabilities.Camera, host), ICameraService
{
    public ValueTask<NativeResult<NativeFile>> CapturePhotoAsync(PhotoCaptureOptions? options = null, CancellationToken cancellationToken = default) => Fail<NativeFile>();
    public ValueTask<NativeResult<NativeFile>> CaptureVideoAsync(VideoCaptureOptions? options = null, CancellationToken cancellationToken = default) => Fail<NativeFile>();
    public ValueTask<NativeResult<IReadOnlyList<NativeFile>>> PickMediaAsync(MediaPickOptions? options = null, CancellationToken cancellationToken = default) => Fail<IReadOnlyList<NativeFile>>();
}

public sealed class UnsupportedBarcodeScanner(NativeHostInfo host) : UnsupportedCapability(NativeCapabilities.Barcode, host), IBarcodeScanner
{
    public ValueTask<NativeResult<Barcode>> ScanAsync(BarcodeScanOptions? options = null, CancellationToken cancellationToken = default) => Fail<Barcode>();
    public ValueTask<NativeResult<IReadOnlyList<Barcode>>> DetectAsync(NativeFile image, BarcodeFormats formats = BarcodeFormats.All, CancellationToken cancellationToken = default) => Fail<IReadOnlyList<Barcode>>();
}

public sealed class UnsupportedFilePickerService(NativeHostInfo host) : UnsupportedCapability(NativeCapabilities.Files, host), IFilePickerService
{
    public ValueTask<NativeResult<IReadOnlyList<NativeFile>>> PickFilesAsync(FilePickOptions? options = null, CancellationToken cancellationToken = default) => Fail<IReadOnlyList<NativeFile>>();
    public ValueTask<NativeResult<SavedFile>> SaveFileAsync(FileSaveRequest request, CancellationToken cancellationToken = default) => Fail<SavedFile>();
    public ValueTask<NativeResult<PickedFolder>> PickFolderAsync(string? title = null, CancellationToken cancellationToken = default) => Fail<PickedFolder>();
}

public sealed class UnsupportedAppStorage(NativeHostInfo host) : UnsupportedCapability(NativeCapabilities.AppStorage, host), IAppStorage
{
    public NativeResult<string> GetDirectory(AppDirectory directory) => NativeResult.Failure<string>(Error);
}

public sealed class UnsupportedSecureStorage(NativeHostInfo host) : UnsupportedCapability(NativeCapabilities.SecureStorage, host), ISecureStorage
{
    public SecureStorageProtection Protection => SecureStorageProtection.None;
    public ValueTask<NativeResult<string?>> GetAsync(string key, CancellationToken cancellationToken = default) => Fail<string?>();
    public ValueTask<NativeResult> SetAsync(string key, string value, CancellationToken cancellationToken = default) => Fail();
    public ValueTask<NativeResult<bool>> RemoveAsync(string key, CancellationToken cancellationToken = default) => Fail<bool>();
    public ValueTask<NativeResult> ClearAsync(CancellationToken cancellationToken = default) => Fail();
}

public sealed class UnsupportedShareService(NativeHostInfo host) : UnsupportedCapability(NativeCapabilities.Share, host), IShareService
{
    public ValueTask<NativeResult> ShareAsync(ShareRequest request, CancellationToken cancellationToken = default) => Fail();
}

public sealed class UnsupportedBiometricService(NativeHostInfo host) : UnsupportedCapability(NativeCapabilities.Biometrics, host), IBiometricService
{
    public ValueTask<NativeResult<BiometricInfo>> GetInfoAsync(CancellationToken cancellationToken = default) => Fail<BiometricInfo>();
    public ValueTask<NativeResult<BiometricAuthentication>> AuthenticateAsync(BiometricRequest request, CancellationToken cancellationToken = default) => Fail<BiometricAuthentication>();
}

public sealed class UnsupportedClipboardService(NativeHostInfo host) : UnsupportedCapability(NativeCapabilities.Clipboard, host), IClipboardService
{
    public event EventHandler? ContentChanged;
    public ValueTask<NativeResult<string?>> GetTextAsync(CancellationToken cancellationToken = default) => Fail<string?>();
    public ValueTask<NativeResult> SetTextAsync(string? text, CancellationToken cancellationToken = default) => Fail();
}

public sealed class UnsupportedPermissionService(NativeHostInfo host) : UnsupportedCapability(NativeCapabilities.Permissions, host), IPermissionService
{
    public ValueTask<NativeResult<PermissionStatus>> CheckAsync(NativePermission permission, CancellationToken cancellationToken = default) => Fail<PermissionStatus>();
    public ValueTask<NativeResult<PermissionStatus>> RequestAsync(NativePermission permission, CancellationToken cancellationToken = default) => Fail<PermissionStatus>();
    public ValueTask<NativeResult<bool>> ShouldShowRationaleAsync(NativePermission permission, CancellationToken cancellationToken = default) => Fail<bool>();
    public ValueTask<NativeResult> OpenAppSettingsAsync(CancellationToken cancellationToken = default) => Fail();
}

public sealed class UnsupportedNotificationService(NativeHostInfo host) : UnsupportedCapability(NativeCapabilities.Notifications, host), INotificationService
{
    public ValueTask<NativeResult<PermissionStatus>> RequestPermissionAsync(CancellationToken cancellationToken = default) => Fail<PermissionStatus>();
    public ValueTask<NativeResult> ShowAsync(LocalNotification notification, CancellationToken cancellationToken = default) => Fail();
    public ValueTask<NativeResult> ScheduleAsync(LocalNotification notification, NotificationSchedule schedule, CancellationToken cancellationToken = default) => Fail();
    public ValueTask<NativeResult> CancelAsync(string notificationId, CancellationToken cancellationToken = default) => Fail();
    public ValueTask<NativeResult> CancelAllAsync(CancellationToken cancellationToken = default) => Fail();
    public ValueTask<NativeResult> CreateChannelAsync(NotificationChannel channel, CancellationToken cancellationToken = default) => Fail();
}

public sealed class UnsupportedConnectivityService(NativeHostInfo host) : UnsupportedCapability(NativeCapabilities.Connectivity, host), IConnectivityService
{
    public ConnectivitySnapshot Current => ConnectivitySnapshot.Unknown;
    public event EventHandler<ConnectivityChangedEventArgs>? Changed;
}

public sealed class UnsupportedBackgroundWorkScheduler(NativeHostInfo host) : UnsupportedCapability(NativeCapabilities.BackgroundWork, host), IBackgroundWorkScheduler
{
    public BackgroundExecutionPolicy Policy { get; } = new()
    {
        Adapter = AdapterName,
        RunsInBackground = false,
        RunsAfterTermination = false,
        SurvivesReboot = false,
        PersistsAcrossLaunches = false,
        Summary = "No background work adapter is registered.",
    };

    public event EventHandler<WorkInfo>? WorkChanged;
    public ValueTask<NativeResult<WorkInfo>> ScheduleAsync(WorkRequest request, CancellationToken cancellationToken = default) => Fail<WorkInfo>();
    public ValueTask<NativeResult<bool>> CancelAsync(string workId, CancellationToken cancellationToken = default) => Fail<bool>();
    public ValueTask<NativeResult<IReadOnlyList<WorkInfo>>> GetWorkAsync(CancellationToken cancellationToken = default) => Fail<IReadOnlyList<WorkInfo>>();
}

public sealed class UnsupportedTransferService(NativeHostInfo host) : UnsupportedCapability(NativeCapabilities.Transfers, host), ITransferService
{
    public TransferPolicy Policy { get; } = new()
    {
        Adapter = AdapterName,
        ContinuesInBackground = false,
        ContinuesAfterTermination = false,
        ResumesDownloads = false,
        Summary = "No transfer adapter is registered.",
    };

    public event EventHandler<TransferInfo>? TransferChanged;
    public ValueTask<NativeResult<TransferInfo>> EnqueueDownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default) => Fail<TransferInfo>();
    public ValueTask<NativeResult<TransferInfo>> EnqueueUploadAsync(UploadRequest request, CancellationToken cancellationToken = default) => Fail<TransferInfo>();
    public ValueTask<NativeResult<bool>> CancelAsync(string transferId, CancellationToken cancellationToken = default) => Fail<bool>();
    public ValueTask<NativeResult<TransferInfo>> RetryAsync(string transferId, CancellationToken cancellationToken = default) => Fail<TransferInfo>();
    public ValueTask<IReadOnlyList<TransferInfo>> GetTransfersAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<TransferInfo>>([]);
    public ValueTask<NativeResult<TransferInfo>> WaitForCompletionAsync(string transferId, CancellationToken cancellationToken = default) => Fail<TransferInfo>();
}

public sealed class UnsupportedWindowManager(NativeHostInfo host) : UnsupportedCapability(NativeCapabilities.Windows, host), IWindowManager
{
    public IReadOnlyList<INativeWindow> Windows => [];
    public INativeWindow? MainWindow => null;
    public event EventHandler<INativeWindow>? WindowOpened;
    public event EventHandler<INativeWindow>? WindowClosed;
    public ValueTask<NativeResult<INativeWindow>> OpenAsync(WindowOptions options, CancellationToken cancellationToken = default) => Fail<INativeWindow>();
    public INativeWindow? FindWindow(string id) => null;
}

public sealed class UnsupportedScreenService(NativeHostInfo host) : UnsupportedCapability(NativeCapabilities.Screens, host), IScreenService
{
    public ValueTask<NativeResult<IReadOnlyList<DisplayInfo>>> GetDisplaysAsync(CancellationToken cancellationToken = default) => Fail<IReadOnlyList<DisplayInfo>>();
}

public sealed class UnsupportedGlobalShortcutService(NativeHostInfo host) : UnsupportedCapability(NativeCapabilities.GlobalShortcuts, host), IGlobalShortcutService
{
    public IReadOnlyList<KeyGesture> RegisteredGestures => [];
    public ValueTask<NativeResult<IGlobalShortcutRegistration>> RegisterAsync(KeyGesture gesture, Func<ValueTask> callback, CancellationToken cancellationToken = default) => Fail<IGlobalShortcutRegistration>();
}

public sealed class UnsupportedTrayService(NativeHostInfo host) : UnsupportedCapability(NativeCapabilities.Tray, host), ITrayService
{
    public IReadOnlyList<ITrayIcon> Icons => [];
    public ValueTask<NativeResult<ITrayIcon>> CreateAsync(TrayIconOptions options, CancellationToken cancellationToken = default) => Fail<ITrayIcon>();
}

public sealed class UnsupportedNativeMenuService(NativeHostInfo host) : UnsupportedCapability(NativeCapabilities.Menus, host), INativeMenuService
{
    public event EventHandler<MenuItemInvokedEventArgs>? ItemInvoked;
    public ValueTask<NativeResult> SetApplicationMenuAsync(NativeMenu? menu, string? windowId = null, CancellationToken cancellationToken = default) => Fail();
    public ValueTask<NativeResult<string>> ShowContextMenuAsync(NativeMenu menu, double? screenX = null, double? screenY = null, string? windowId = null, CancellationToken cancellationToken = default) => Fail<string>();
}

public sealed class UnsupportedDesktopFileOperations(NativeHostInfo host) : UnsupportedCapability(NativeCapabilities.DesktopFiles, host), IDesktopFileOperations
{
    public ValueTask<NativeResult> RevealInFileManagerAsync(string path, CancellationToken cancellationToken = default) => Fail();
    public ValueTask<NativeResult> OpenPathAsync(string path, CancellationToken cancellationToken = default) => Fail();
    public ValueTask<NativeResult> OpenUriAsync(Uri uri, CancellationToken cancellationToken = default) => Fail();
    public ValueTask<NativeResult> MoveToTrashAsync(string path, CancellationToken cancellationToken = default) => Fail();
}

public sealed class UnsupportedIpcService(NativeHostInfo host) : UnsupportedCapability(NativeCapabilities.Ipc, host), IIpcService
{
    public ValueTask<NativeResult<IIpcServer>> StartServerAsync(string channel, IpcMessageHandler handler, IpcSecurityOptions? security = null, CancellationToken cancellationToken = default) => Fail<IIpcServer>();
    public ValueTask<NativeResult<IIpcConnection>> ConnectAsync(string channel, IpcSecurityOptions? security = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default) => Fail<IIpcConnection>();
}

public sealed class UnsupportedSingleInstanceService(NativeHostInfo host) : UnsupportedCapability(NativeCapabilities.SingleInstance, host), ISingleInstanceService
{
    public ValueTask<NativeResult<ISingleInstanceLease>> AcquireAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) => Fail<ISingleInstanceLease>();
}

public sealed class UnsupportedProtocolRegistrationService(NativeHostInfo host) : UnsupportedCapability(NativeCapabilities.ProtocolRegistration, host), IProtocolRegistrationService
{
    public ValueTask<NativeResult> RegisterAsync(string scheme, ProtocolRegistrationOptions? options = null, CancellationToken cancellationToken = default) => Fail();
    public ValueTask<NativeResult> UnregisterAsync(string scheme, CancellationToken cancellationToken = default) => Fail();
    public ValueTask<NativeResult<bool>> IsRegisteredAsync(string scheme, CancellationToken cancellationToken = default) => Fail<bool>();
}

#pragma warning restore CS0067
#pragma warning restore CS1591
