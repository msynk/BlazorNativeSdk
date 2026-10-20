using AVFoundation;
using BlazorNativeSdk.Devices;
using CoreFoundation;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>
/// A full-screen camera scanner built on <c>AVCaptureSession</c> and <c>AVCaptureMetadataOutput</c>: live preview, an optional
/// prompt and a close button. Completes with the first barcode of a requested format, or with
/// <see cref="NativeResultStatus.Cancelled"/> when the user closes it.
/// </summary>
/// <remarks>All members run on the main thread; the capture session is started and stopped on a private serial queue.</remarks>
internal sealed class AppleBarcodeScannerViewController : UIViewController
{
    private readonly AVCaptureDevice _device;
    private readonly AVMetadataObjectType _metadataTypes;
    private readonly BarcodeFormats _formats;
    private readonly string? _prompt;
    private readonly TaskCompletionSource<NativeResult<Barcode>> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatchQueue _sessionQueue = new("blazornativesdk.barcode.session");
    private AVCaptureSession? _session;
    private AVCaptureMetadataOutput? _output;
    private AVCaptureVideoPreviewLayer? _preview;
    private MetadataDelegate? _metadataDelegate;
    private NSObject? _rotationCoordinator;
    private int _completed;

    public AppleBarcodeScannerViewController(AVCaptureDevice device, AVMetadataObjectType metadataTypes, BarcodeFormats formats, string? prompt)
    {
        _device = device;
        _metadataTypes = metadataTypes;
        _formats = formats;
        _prompt = prompt;
        ModalPresentationStyle = UIModalPresentationStyle.FullScreen;
    }

    /// <summary>Completes when a barcode is detected, the user closes the scanner or the session fails.</summary>
    public Task<NativeResult<Barcode>> Completion => _completion.Task;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        var view = View!;
        view.BackgroundColor = UIColor.Black;

        _preview = new AVCaptureVideoPreviewLayer { VideoGravity = AVLayerVideoGravity.ResizeAspectFill };
        view.Layer.AddSublayer(_preview);
        AddOverlay(view);
        ConfigureSession();
    }

    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        if (_preview is not null && View is { } view)
        {
            _preview.Frame = view.Bounds;
            UpdatePreviewRotation();
        }
    }

    public override void ViewDidDisappear(bool animated)
    {
        base.ViewDidDisappear(animated);

        // Dismissed by something other than this scanner (for example the app's own UI): treat as a cancellation.
        if (IsBeingDismissed || PresentingViewController is null)
        {
            Complete(NativeError.Cancelled(NativeCapabilities.Barcode));
        }
    }

    /// <summary>Completes (as cancelled when still pending), stops the camera and dismisses the scanner.</summary>
    public async Task CloseAsync()
    {
        Complete(NativeError.Cancelled(NativeCapabilities.Barcode));
        await AppleUI.DismissAsync(this).ConfigureAwait(true);
    }

    private void AddOverlay(UIView view)
    {
        var close = UIButton.FromType(UIButtonType.Close);
        close.TranslatesAutoresizingMaskIntoConstraints = false;
        close.TouchUpInside += (_, _) => Complete(NativeError.Cancelled(NativeCapabilities.Barcode));
        view.AddSubview(close);

        var guide = view.SafeAreaLayoutGuide;
        var constraints = new List<NSLayoutConstraint>
        {
            close.TopAnchor.ConstraintEqualTo(guide.TopAnchor, 12),
            close.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -16),
            close.WidthAnchor.ConstraintEqualTo(44),
            close.HeightAnchor.ConstraintEqualTo(44),
        };

        if (!string.IsNullOrWhiteSpace(_prompt))
        {
            var background = new UIView
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                BackgroundColor = UIColor.Black.ColorWithAlpha(0.6f),
            };
            background.Layer.CornerRadius = 12;
            var label = new UILabel
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                Text = _prompt,
                TextColor = UIColor.White,
                TextAlignment = UITextAlignment.Center,
                Lines = 0,
                Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.Body),
                AdjustsFontForContentSizeCategory = true,
            };
            background.AddSubview(label);
            view.AddSubview(background);
            constraints.AddRange(
            [
                label.TopAnchor.ConstraintEqualTo(background.TopAnchor, 10),
                label.BottomAnchor.ConstraintEqualTo(background.BottomAnchor, -10),
                label.LeadingAnchor.ConstraintEqualTo(background.LeadingAnchor, 16),
                label.TrailingAnchor.ConstraintEqualTo(background.TrailingAnchor, -16),
                background.CenterXAnchor.ConstraintEqualTo(guide.CenterXAnchor),
                background.BottomAnchor.ConstraintEqualTo(guide.BottomAnchor, -24),
                background.LeadingAnchor.ConstraintGreaterThanOrEqualTo(guide.LeadingAnchor, 24),
                background.TrailingAnchor.ConstraintLessThanOrEqualTo(guide.TrailingAnchor, -24),
            ]);
        }

        NSLayoutConstraint.ActivateConstraints([.. constraints]);
    }

    private void ConfigureSession()
    {
        var input = AVCaptureDeviceInput.FromDevice(_device, out var inputError);
        if (input is null)
        {
            Fail(inputError?.LocalizedDescription ?? "The camera could not be opened.");
            return;
        }

        var session = new AVCaptureSession();
        session.BeginConfiguration();
        if (!session.CanAddInput(input))
        {
            session.CommitConfiguration();
            Fail("The camera cannot be used by a capture session right now (it may be in use by another app).");
            return;
        }

        session.AddInput(input);
        var output = new AVCaptureMetadataOutput();
        if (!session.CanAddOutput(output))
        {
            session.CommitConfiguration();
            Fail("Barcode detection is not available for this camera.");
            return;
        }

        session.AddOutput(output);

        // Available types are only known once the output is attached to a session with an input.
        var types = output.AvailableMetadataObjectTypes & _metadataTypes;
        if (types == AVMetadataObjectType.None)
        {
            session.CommitConfiguration();
            Fail("None of the requested barcode formats can be scanned live on this device.");
            return;
        }

        _metadataDelegate = new MetadataDelegate(this);
        output.SetDelegate(_metadataDelegate, DispatchQueue.MainQueue);
        output.MetadataObjectTypes = types;
        session.CommitConfiguration();
        ConfigureFocus();

        _session = session;
        _output = output;
        _preview!.Session = session;
        _sessionQueue.DispatchAsync(session.StartRunning);
    }

    private void ConfigureFocus()
    {
        if (!_device.LockForConfiguration(out _))
        {
            return;
        }

        try
        {
            if (_device.AutoFocusRangeRestrictionSupported)
            {
                // Barcodes are usually held close to the camera.
                _device.AutoFocusRangeRestriction = AVCaptureAutoFocusRangeRestriction.Near;
            }

            if (_device.IsFocusModeSupported(AVCaptureFocusMode.ContinuousAutoFocus))
            {
                _device.FocusMode = AVCaptureFocusMode.ContinuousAutoFocus;
            }
        }
        finally
        {
            _device.UnlockForConfiguration();
        }
    }

    private void UpdatePreviewRotation()
    {
        if (_preview?.Connection is not { } connection)
        {
            return;
        }

        if (OperatingSystem.IsIOSVersionAtLeast(17) || OperatingSystem.IsMacCatalystVersionAtLeast(17))
        {
            var coordinator = _rotationCoordinator as AVCaptureDeviceRotationCoordinator ?? new AVCaptureDeviceRotationCoordinator(_device, _preview);
            _rotationCoordinator = coordinator;
            var angle = coordinator.VideoRotationAngleForHorizonLevelPreview;
            if (connection.IsVideoRotationAngleSupported(angle))
            {
                connection.VideoRotationAngle = angle;
            }
        }
        else if (OperatingSystem.IsIOS() && !OperatingSystem.IsMacCatalyst() && connection.SupportsVideoOrientation &&
                 View?.Window?.WindowScene?.InterfaceOrientation is { } orientation)
        {
            connection.VideoOrientation = orientation switch
            {
                UIInterfaceOrientation.PortraitUpsideDown => AVCaptureVideoOrientation.PortraitUpsideDown,
                UIInterfaceOrientation.LandscapeLeft => AVCaptureVideoOrientation.LandscapeLeft,
                UIInterfaceOrientation.LandscapeRight => AVCaptureVideoOrientation.LandscapeRight,
                _ => AVCaptureVideoOrientation.Portrait,
            };
        }
    }

    private void OnMetadata(AVMetadataObject[] metadataObjects)
    {
        if (Volatile.Read(ref _completed) != 0)
        {
            return;
        }

        foreach (var metadata in metadataObjects)
        {
            if (metadata is not AVMetadataMachineReadableCodeObject code)
            {
                continue;
            }

            if (AppleBarcodeMapping.Normalize(AppleBarcodeMapping.FromMetadataType(code.Type), code.StringValue, _formats) is not { } barcode)
            {
                continue;
            }

            if (!OperatingSystem.IsMacCatalyst())
            {
                using var feedback = new UINotificationFeedbackGenerator();
                feedback.NotificationOccurred(UINotificationFeedbackType.Success);
            }

            Complete(new Barcode { RawValue = barcode.Value, Format = barcode.Format });
            return;
        }
    }

    private void Fail(string message) =>
        Complete(NativeError.Failed(NativeCapabilities.Barcode, NativeErrorCodes.PlatformError, message));

    private void Complete(NativeResult<Barcode> result)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        StopSession();
        _completion.TrySetResult(result);
    }

    private void StopSession()
    {
        var session = _session;
        var output = _output;
        var metadataDelegate = _metadataDelegate;
        _session = null;
        _output = null;
        _metadataDelegate = null;
        if (_preview is not null)
        {
            _preview.Session = null;
        }

        (_rotationCoordinator as IDisposable)?.Dispose();
        _rotationCoordinator = null;

        if (session is null)
        {
            return;
        }

        output?.SetDelegate(null, null);
        _sessionQueue.DispatchAsync(() =>
        {
            session.StopRunning();
            foreach (var input in session.Inputs)
            {
                session.RemoveInput(input);
            }

            if (output is not null)
            {
                session.RemoveOutput(output);
                output.Dispose();
            }

            session.Dispose();
            metadataDelegate?.Dispose();
        });
    }

    private sealed class MetadataDelegate(AppleBarcodeScannerViewController owner) : AVCaptureMetadataOutputObjectsDelegate
    {
        public override void DidOutputMetadataObjects(AVCaptureMetadataOutput captureOutput, AVMetadataObject[] metadataObjects, AVCaptureConnection connection) =>
            owner.OnMetadata(metadataObjects);
    }
}
