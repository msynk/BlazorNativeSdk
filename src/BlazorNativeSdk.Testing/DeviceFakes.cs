using System.Collections.Concurrent;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;

namespace BlazorNativeSdk.Testing;

/// <summary>A scriptable <see cref="ICameraService"/>. By default every operation is cancelled by the "user".</summary>
public sealed class FakeCameraService() : FakeCapability(NativeCapabilities.Camera, CameraFeatures.Photo, CameraFeatures.Video, CameraFeatures.Library), ICameraService
{
    /// <summary>Results for <see cref="CapturePhotoAsync"/>.</summary>
    public ResultQueue<NativeFile> Photos { get; } = new(NativeError.Cancelled(NativeCapabilities.Camera));

    /// <summary>Results for <see cref="CaptureVideoAsync"/>.</summary>
    public ResultQueue<NativeFile> Videos { get; } = new(NativeError.Cancelled(NativeCapabilities.Camera));

    /// <summary>Results for <see cref="PickMediaAsync"/>.</summary>
    public ResultQueue<IReadOnlyList<NativeFile>> Picks { get; } = new(NativeError.Cancelled(NativeCapabilities.Camera));

    /// <inheritdoc />
    public ValueTask<NativeResult<NativeFile>> CapturePhotoAsync(PhotoCaptureOptions? options = null, CancellationToken cancellationToken = default)
    {
        Record(nameof(CapturePhotoAsync));
        return ValueTask.FromResult(StatusError() is { } e ? e : Photos.Next());
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<NativeFile>> CaptureVideoAsync(VideoCaptureOptions? options = null, CancellationToken cancellationToken = default)
    {
        Record(nameof(CaptureVideoAsync));
        return ValueTask.FromResult(StatusError() is { } e ? e : Videos.Next());
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<IReadOnlyList<NativeFile>>> PickMediaAsync(MediaPickOptions? options = null, CancellationToken cancellationToken = default)
    {
        Record(nameof(PickMediaAsync));
        return ValueTask.FromResult(StatusError() is { } e ? e : Picks.Next());
    }

    /// <summary>Queues a JPEG-like photo with the given bytes.</summary>
    public FakeCameraService ReturnsPhoto(byte[]? content = null, string name = "photo.jpg")
    {
        Photos.EnqueueSuccess(NativeFile.FromBytes(name, content ?? [0xFF, 0xD8, 0xFF, 0xD9], "image/jpeg"));
        return this;
    }
}

/// <summary>A scriptable <see cref="IBarcodeScanner"/>.</summary>
public sealed class FakeBarcodeScanner() : FakeCapability(NativeCapabilities.Barcode, BarcodeFeatures.LiveScan, BarcodeFeatures.ImageDetection), IBarcodeScanner
{
    /// <summary>Results for <see cref="ScanAsync"/>.</summary>
    public ResultQueue<Barcode> Scans { get; } = new(NativeError.Cancelled(NativeCapabilities.Barcode));

    /// <summary>Results for <see cref="DetectAsync"/>.</summary>
    public ResultQueue<IReadOnlyList<Barcode>> Detections { get; } = new(NativeResult.Success<IReadOnlyList<Barcode>>([]));

    /// <inheritdoc />
    public ValueTask<NativeResult<Barcode>> ScanAsync(BarcodeScanOptions? options = null, CancellationToken cancellationToken = default)
    {
        Record(nameof(ScanAsync));
        return ValueTask.FromResult(StatusError() is { } e ? e : Scans.Next());
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<IReadOnlyList<Barcode>>> DetectAsync(NativeFile image, BarcodeFormats formats = BarcodeFormats.All, CancellationToken cancellationToken = default)
    {
        Record(nameof(DetectAsync));
        return ValueTask.FromResult(StatusError() is { } e ? e : Detections.Next());
    }

    /// <summary>Queues a QR code scan result.</summary>
    public FakeBarcodeScanner ReturnsQrCode(string value)
    {
        Scans.EnqueueSuccess(new Barcode { RawValue = value, Format = BarcodeFormats.QrCode });
        return this;
    }
}

/// <summary>A scriptable <see cref="IFilePickerService"/> that records saved content.</summary>
public sealed class FakeFilePickerService() : FakeCapability(NativeCapabilities.Files, FilePickerFeatures.Multiple, FilePickerFeatures.SaveDialog, FilePickerFeatures.FolderPicker), IFilePickerService
{
    private readonly ConcurrentQueue<(string FileName, byte[] Content)> _saved = new();

    /// <summary>Results for <see cref="PickFilesAsync"/>.</summary>
    public ResultQueue<IReadOnlyList<NativeFile>> Picks { get; } = new(NativeError.Cancelled(NativeCapabilities.Files));

    /// <summary>Results for <see cref="PickFolderAsync"/>.</summary>
    public ResultQueue<PickedFolder> Folders { get; } = new(NativeError.Cancelled(NativeCapabilities.Files));

    /// <summary>Whether <see cref="SaveFileAsync"/> succeeds. Defaults to <see langword="true"/>.</summary>
    public bool SaveSucceeds { get; set; } = true;

    /// <summary>Files saved through <see cref="SaveFileAsync"/>.</summary>
    public IReadOnlyList<(string FileName, byte[] Content)> SavedFiles => [.. _saved];

    /// <inheritdoc />
    public ValueTask<NativeResult<IReadOnlyList<NativeFile>>> PickFilesAsync(FilePickOptions? options = null, CancellationToken cancellationToken = default)
    {
        Record(nameof(PickFilesAsync));
        return ValueTask.FromResult(StatusError() is { } e ? e : Picks.Next());
    }

    /// <inheritdoc />
    public async ValueTask<NativeResult<SavedFile>> SaveFileAsync(FileSaveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Record(nameof(SaveFileAsync));
        if (StatusError() is { } error)
        {
            return error;
        }

        if (!SaveSucceeds)
        {
            return NativeError.Cancelled(Capability);
        }

        using var buffer = new MemoryStream();
        await request.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        _saved.Enqueue((request.SuggestedFileName, buffer.ToArray()));
        return new SavedFile(request.SuggestedFileName, null, FileSaveMethod.SaveDialog);
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<PickedFolder>> PickFolderAsync(string? title = null, CancellationToken cancellationToken = default)
    {
        Record(nameof(PickFolderAsync));
        return ValueTask.FromResult(StatusError() is { } e ? e : Folders.Next());
    }
}

/// <summary>An in-memory <see cref="ISecureStorage"/> with real key/value semantics.</summary>
public sealed class InMemorySecureStorage() : FakeCapability(NativeCapabilities.SecureStorage), ISecureStorage
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public SecureStorageProtection Protection { get; set; } = SecureStorageProtection.None;

    /// <summary>The stored values (for assertions).</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    /// <inheritdoc />
    public ValueTask<NativeResult<string?>> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        Record(nameof(GetAsync));
        if (StatusError() is { } error)
        {
            return ValueTask.FromResult<NativeResult<string?>>(error);
        }

        return ValueTask.FromResult(NativeResult.Success(_values.TryGetValue(key, out var value) ? value : null));
    }

    /// <inheritdoc />
    public ValueTask<NativeResult> SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        Record(nameof(SetAsync));
        if (StatusError() is { } error)
        {
            return ValueTask.FromResult<NativeResult>(error);
        }

        _values[key] = value;
        return ValueTask.FromResult(NativeResult.Ok);
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<bool>> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        Record(nameof(RemoveAsync));
        if (StatusError() is { } error)
        {
            return ValueTask.FromResult<NativeResult<bool>>(error);
        }

        return ValueTask.FromResult(NativeResult.Success(_values.TryRemove(key, out _)));
    }

    /// <inheritdoc />
    public ValueTask<NativeResult> ClearAsync(CancellationToken cancellationToken = default)
    {
        Record(nameof(ClearAsync));
        if (StatusError() is { } error)
        {
            return ValueTask.FromResult<NativeResult>(error);
        }

        _values.Clear();
        return ValueTask.FromResult(NativeResult.Ok);
    }
}

/// <summary>A <see cref="IShareService"/> that records share requests.</summary>
public sealed class FakeShareService() : FakeCapability(NativeCapabilities.Share, ShareFeatures.Text, ShareFeatures.Uri, ShareFeatures.Files), IShareService
{
    private readonly ConcurrentQueue<ShareRequest> _requests = new();

    /// <summary>Results for <see cref="ShareAsync"/>; defaults to success.</summary>
    public ResultQueue Results { get; } = new();

    /// <summary>The recorded requests.</summary>
    public IReadOnlyList<ShareRequest> Requests => [.. _requests];

    /// <inheritdoc />
    public ValueTask<NativeResult> ShareAsync(ShareRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Record(nameof(ShareAsync));
        if (StatusError() is { } error)
        {
            return ValueTask.FromResult<NativeResult>(error);
        }

        _requests.Enqueue(request);
        return ValueTask.FromResult(Results.Next());
    }
}

/// <summary>A scriptable <see cref="IBiometricService"/>. Authentication succeeds with a fingerprint by default.</summary>
public sealed class FakeBiometricService() : FakeCapability(NativeCapabilities.Biometrics, BiometricFeatures.Fingerprint), IBiometricService
{
    /// <summary>The reported hardware state.</summary>
    public BiometricInfo Info { get; set; } = new() { IsHardwarePresent = true, IsEnrolled = true, AvailableKinds = BiometricKinds.Fingerprint, Strength = BiometricStrength.Strong };

    /// <summary>Results for <see cref="AuthenticateAsync"/>.</summary>
    public ResultQueue<BiometricAuthentication> Authentications { get; } = new(new BiometricAuthentication(BiometricKinds.Fingerprint));

    /// <summary>The recorded requests.</summary>
    public IReadOnlyList<BiometricRequest> Requests => [.. _requests];

    private readonly ConcurrentQueue<BiometricRequest> _requests = new();

    /// <inheritdoc />
    public ValueTask<NativeResult<BiometricInfo>> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        Record(nameof(GetInfoAsync));
        return ValueTask.FromResult(StatusError() is { } e ? e : NativeResult.Success(Info));
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<BiometricAuthentication>> AuthenticateAsync(BiometricRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Record(nameof(AuthenticateAsync));
        _requests.Enqueue(request);
        if (StatusError() is { } error)
        {
            return ValueTask.FromResult<NativeResult<BiometricAuthentication>>(error);
        }

        if (!Info.IsEnrolled)
        {
            return ValueTask.FromResult<NativeResult<BiometricAuthentication>>(
                NativeError.Unavailable(Capability, NativeErrorCodes.BiometricNotEnrolled, "No biometrics enrolled."));
        }

        return ValueTask.FromResult(Authentications.Next());
    }
}

/// <summary>An in-memory <see cref="IClipboardService"/> that raises <see cref="ContentChanged"/>.</summary>
public sealed class FakeClipboardService() : FakeCapability(NativeCapabilities.Clipboard, ClipboardFeatures.ReadText, ClipboardFeatures.WriteText, ClipboardFeatures.ChangeNotification), IClipboardService
{
    private readonly Lock _gate = new();
    private string? _text;

    /// <inheritdoc />
    public event EventHandler? ContentChanged;

    /// <summary>The current clipboard text.</summary>
    public string? Text
    {
        get
        {
            lock (_gate)
            {
                return _text;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<string?>> GetTextAsync(CancellationToken cancellationToken = default)
    {
        Record(nameof(GetTextAsync));
        return ValueTask.FromResult(StatusError() is { } e ? e : NativeResult.Success(Text));
    }

    /// <inheritdoc />
    public ValueTask<NativeResult> SetTextAsync(string? text, CancellationToken cancellationToken = default)
    {
        Record(nameof(SetTextAsync));
        if (StatusError() is { } error)
        {
            return ValueTask.FromResult<NativeResult>(error);
        }

        SimulateExternalCopy(text);
        return ValueTask.FromResult(NativeResult.Ok);
    }

    /// <summary>Simulates another application changing the clipboard.</summary>
    public void SimulateExternalCopy(string? text)
    {
        lock (_gate)
        {
            _text = text;
        }

        ContentChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// A <see cref="IPermissionService"/> backed by a dictionary. Unknown permissions start as <see cref="PermissionStatus.NotDetermined"/>;
/// requesting one applies <see cref="RequestOutcome"/>.
/// </summary>
public sealed class FakePermissionService() : FakeCapability(NativeCapabilities.Permissions), IPermissionService
{
    private readonly ConcurrentDictionary<NativePermission, PermissionStatus> _states = new();

    /// <summary>The status a permission takes when requested from <see cref="PermissionStatus.NotDetermined"/>. Defaults to granted.</summary>
    public PermissionStatus RequestOutcome { get; set; } = PermissionStatus.Granted;

    /// <summary>Whether <see cref="ShouldShowRationaleAsync"/> returns <see langword="true"/>.</summary>
    public bool ShouldShowRationale { get; set; }

    /// <summary>The number of times app settings were opened.</summary>
    public int SettingsOpened { get; private set; }

    /// <summary>Sets the current state of a permission.</summary>
    public FakePermissionService Set(NativePermission permission, PermissionStatus status)
    {
        _states[permission] = status;
        return this;
    }

    /// <summary>Gets the current state of a permission.</summary>
    public PermissionStatus Get(NativePermission permission) => _states.GetValueOrDefault(permission, PermissionStatus.NotDetermined);

    /// <inheritdoc />
    public ValueTask<NativeResult<PermissionStatus>> CheckAsync(NativePermission permission, CancellationToken cancellationToken = default)
    {
        Record(nameof(CheckAsync));
        return ValueTask.FromResult(StatusError() is { } e ? e : NativeResult.Success(Get(permission)));
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<PermissionStatus>> RequestAsync(NativePermission permission, CancellationToken cancellationToken = default)
    {
        Record(nameof(RequestAsync));
        if (StatusError() is { } error)
        {
            return ValueTask.FromResult<NativeResult<PermissionStatus>>(error);
        }

        var current = Get(permission);
        if (current == PermissionStatus.NotDetermined)
        {
            current = RequestOutcome;
            _states[permission] = current;
        }

        return ValueTask.FromResult(NativeResult.Success(current));
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<bool>> ShouldShowRationaleAsync(NativePermission permission, CancellationToken cancellationToken = default)
    {
        Record(nameof(ShouldShowRationaleAsync));
        return ValueTask.FromResult(NativeResult.Success(ShouldShowRationale));
    }

    /// <inheritdoc />
    public ValueTask<NativeResult> OpenAppSettingsAsync(CancellationToken cancellationToken = default)
    {
        Record(nameof(OpenAppSettingsAsync));
        SettingsOpened++;
        return ValueTask.FromResult(NativeResult.Ok);
    }
}

/// <summary>A <see cref="INotificationService"/> that records notifications and can simulate taps.</summary>
public sealed class FakeNotificationService : FakeCapability, INotificationService
{
    private readonly ConcurrentDictionary<string, LocalNotification> _shown = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (LocalNotification Notification, NotificationSchedule Schedule)> _scheduled = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NotificationChannel> _channels = new(StringComparer.Ordinal);
    private readonly IActivationPublisher? _publisher;

    /// <summary>Creates the fake. Pass the activation publisher to make <see cref="SimulateTapAsync"/> route through the app.</summary>
    public FakeNotificationService(IActivationPublisher? publisher = null)
        : base(NativeCapabilities.Notifications, NotificationFeatures.Scheduled, NotificationFeatures.Actions, NotificationFeatures.ActivationRouting)
    {
        _publisher = publisher;
    }

    /// <summary>The permission returned by <see cref="RequestPermissionAsync"/>.</summary>
    public PermissionStatus Permission { get; set; } = PermissionStatus.Granted;

    /// <summary>Currently shown notifications by id.</summary>
    public IReadOnlyDictionary<string, LocalNotification> Shown => _shown;

    /// <summary>Currently scheduled notifications by id.</summary>
    public IReadOnlyDictionary<string, (LocalNotification Notification, NotificationSchedule Schedule)> Scheduled => _scheduled;

    /// <summary>Created channels by id.</summary>
    public IReadOnlyDictionary<string, NotificationChannel> Channels => _channels;

    /// <inheritdoc />
    public ValueTask<NativeResult<PermissionStatus>> RequestPermissionAsync(CancellationToken cancellationToken = default)
    {
        Record(nameof(RequestPermissionAsync));
        return ValueTask.FromResult(NativeResult.Success(Permission));
    }

    /// <inheritdoc />
    public ValueTask<NativeResult> ShowAsync(LocalNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        Record(nameof(ShowAsync));
        if (Gate() is { } error)
        {
            return ValueTask.FromResult<NativeResult>(error);
        }

        _shown[notification.Id] = notification;
        return ValueTask.FromResult(NativeResult.Ok);
    }

    /// <inheritdoc />
    public ValueTask<NativeResult> ScheduleAsync(LocalNotification notification, NotificationSchedule schedule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(schedule);
        Record(nameof(ScheduleAsync));
        if (Gate() is { } error)
        {
            return ValueTask.FromResult<NativeResult>(error);
        }

        _scheduled[notification.Id] = (notification, schedule);
        return ValueTask.FromResult(NativeResult.Ok);
    }

    /// <inheritdoc />
    public ValueTask<NativeResult> CancelAsync(string notificationId, CancellationToken cancellationToken = default)
    {
        Record(nameof(CancelAsync));
        _shown.TryRemove(notificationId, out _);
        _scheduled.TryRemove(notificationId, out _);
        return ValueTask.FromResult(NativeResult.Ok);
    }

    /// <inheritdoc />
    public ValueTask<NativeResult> CancelAllAsync(CancellationToken cancellationToken = default)
    {
        Record(nameof(CancelAllAsync));
        _shown.Clear();
        _scheduled.Clear();
        return ValueTask.FromResult(NativeResult.Ok);
    }

    /// <inheritdoc />
    public ValueTask<NativeResult> CreateChannelAsync(NotificationChannel channel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        Record(nameof(CreateChannelAsync));
        _channels[channel.Id] = channel;
        return ValueTask.FromResult(NativeResult.Ok);
    }

    /// <summary>Simulates the user tapping a shown notification (or one of its actions).</summary>
    public async ValueTask SimulateTapAsync(string notificationId, string? actionId = null, string? userText = null, bool isColdStart = false)
    {
        if (!_shown.TryGetValue(notificationId, out var notification))
        {
            throw new InvalidOperationException($"Notification '{notificationId}' is not shown.");
        }

        if (_publisher is null)
        {
            throw new InvalidOperationException("Create the fake with an IActivationPublisher to simulate taps.");
        }

        await _publisher.PublishAsync(new AppActivation
        {
            Kind = ActivationKind.Notification,
            NotificationId = notificationId,
            ActionId = actionId,
            UserText = userText,
            Route = notification.Route,
            Data = notification.Data,
            IsColdStart = isColdStart,
            Source = "fake",
        }).ConfigureAwait(false);
    }

    private NativeError? Gate()
    {
        if (StatusError() is { } error)
        {
            return error;
        }

        return Permission.IsGranted()
            ? null
            : NativeError.PermissionDenied(Capability, NativePermissions.Notifications, Permission);
    }
}

/// <summary>A connectivity fake whose state is set by tests.</summary>
public sealed class FakeConnectivityService() : FakeCapability(NativeCapabilities.Connectivity), IConnectivityService
{
    private readonly Lock _gate = new();
    private ConnectivitySnapshot _current = new() { Access = NetworkAccess.Internet, ConnectionTypes = [ConnectionType.WiFi], IsMetered = false };

    /// <inheritdoc />
    public event EventHandler<ConnectivityChangedEventArgs>? Changed;

    /// <inheritdoc />
    public ConnectivitySnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Sets a new snapshot and raises <see cref="Changed"/>.</summary>
    public void Set(ConnectivitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ConnectivitySnapshot previous;
        lock (_gate)
        {
            previous = _current;
            _current = snapshot;
        }

        Changed?.Invoke(this, new ConnectivityChangedEventArgs(previous, snapshot));
    }

    /// <summary>Simulates going offline.</summary>
    public void GoOffline() => Set(new ConnectivitySnapshot { Access = NetworkAccess.None });

    /// <summary>Simulates going online.</summary>
    public void GoOnline(ConnectionType type = ConnectionType.WiFi, bool metered = false) =>
        Set(new ConnectivitySnapshot { Access = NetworkAccess.Internet, ConnectionTypes = [type], IsMetered = metered });
}

/// <summary>A lifecycle fake driven by tests.</summary>
public sealed class FakeAppLifecycle : AppLifecycleBase
{
    /// <summary>Creates the fake in the <see cref="AppLifecycleState.Active"/> state.</summary>
    public FakeAppLifecycle(TimeProvider? timeProvider = null)
        : base(timeProvider)
    {
        ReportState(AppLifecycleState.Active, "fake");
    }

    /// <summary>The guarantee reported by leases.</summary>
    public BackgroundExecutionGuarantee Guarantee { get; set; } = BackgroundExecutionGuarantee.OsGranted;

    /// <inheritdoc />
    public override BackgroundExecutionGuarantee BackgroundGuarantee => Guarantee;

    /// <summary>The most recent lease handed out (to simulate expiration).</summary>
    public BackgroundTimeLease? LastLease { get; private set; }

    /// <inheritdoc />
    protected override string AdapterName => "fake";

    /// <summary>Simulates moving to the background.</summary>
    public Task SimulateBackgroundAsync()
    {
        ReportState(AppLifecycleState.Inactive, "fake");
        ReportState(AppLifecycleState.Background, "fake");
        return LastTransitionCompletion;
    }

    /// <summary>Simulates returning to the foreground.</summary>
    public Task SimulateResumeAsync()
    {
        ReportState(AppLifecycleState.Inactive, "fake");
        ReportState(AppLifecycleState.Active, "fake");
        return LastTransitionCompletion;
    }

    /// <inheritdoc />
    protected override ValueTask<NativeResult<IBackgroundTimeLease>> RequestBackgroundTimeCoreAsync(string name, CancellationToken cancellationToken)
    {
        LastLease = new BackgroundTimeLease(name, Guarantee, () => TimeSpan.FromSeconds(30));
        return ValueTask.FromResult(NativeResult.Success<IBackgroundTimeLease>(LastLease));
    }
}
