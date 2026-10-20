using Android.Gms.Common;
using Android.Gms.Common.Apis;
using Android.Gms.Common.ModuleInstall;
using BlazorNativeSdk.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;
using Xamarin.Google.MLKit.Common;
using Xamarin.Google.MLKit.Vision.CodeScanner;
using MlKitBarcode = Xamarin.Google.MLKit.Vision.Barcode.Common.Barcode;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>
/// <see cref="IBarcodeScanner"/> for Android based on the Google code scanner (<c>GmsBarcodeScanning</c>). The scanner UI
/// is provided by Google Play services, so the app needs no camera permission and no camera code.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Requires Google Play services. On devices without it the capability reports
/// <see cref="CapabilityAvailability.Unavailable"/> with <see cref="AndroidErrorCodes.PlayServicesUnavailable"/>;
/// register your own <see cref="IBarcodeScanner"/> (for example CameraX + ML Kit or ZXing.Net) for those devices.</item>
/// <item>The scanner module is downloaded by Google Play services on first use; until then scans fail with
/// <see cref="AndroidErrorCodes.ModuleUnavailable"/>.</item>
/// <item>The Google UI cannot show a custom prompt, choose the camera or be closed programmatically, so
/// <see cref="BarcodeScanOptions.Prompt"/>, <see cref="BarcodeScanOptions.Camera"/> and <see cref="BarcodeScanOptions.Timeout"/>
/// are ignored. Cancelling the token completes the call but leaves the scanner open until the user dismisses it.</item>
/// <item><see cref="DetectAsync"/> is unsupported: detecting codes in images needs the ML Kit barcode-scanning model,
/// which this adapter does not bundle.</item>
/// </list>
/// </remarks>
public sealed partial class AndroidBarcodeScanner : IBarcodeScanner
{
    private const string AdapterName = "android";
    private readonly AndroidNativeOptions _options;
    private readonly ILogger<AndroidBarcodeScanner> _logger;
    private int _installRequested;

    /// <summary>Creates the scanner.</summary>
    public AndroidBarcodeScanner(AndroidNativeOptions options, ILogger<AndroidBarcodeScanner> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.Barcode;

    /// <inheritdoc />
    public async ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (CheckPlayServices() is { } error)
        {
            return CapabilityStatus.FromError(error, AdapterName);
        }

        // Google Code Scanner owns its UI: Prompt, Camera and Timeout options are not honored, so those features are not reported.
        var status = CapabilityStatus.Available(Capability, AdapterName, BarcodeFeatures.LiveScan, BarcodeFeatures.PermissionlessScan);
        try
        {
            var available = await IsModuleInstalledAsync(cancellationToken).ConfigureAwait(false);
            if (available == false)
            {
                return status with
                {
                    Reason = "The Google code scanner module is not installed yet; Google Play services downloads it on first use.",
                };
            }
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or TimeoutException)
        {
            LogModuleCheckFailed(_logger, ex);
        }

        return status;
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<Barcode>> ScanAsync(BarcodeScanOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new BarcodeScanOptions();
        return NativeOperation.RunAsync<Barcode>(Capability, async ct =>
        {
            if (CheckPlayServices() is { } unavailable)
            {
                return unavailable;
            }

            if (options.Timeout is not null || options.Prompt is not null)
            {
                LogIgnoredOptions(_logger);
            }

            var formats = AndroidBarcodeMapper.ToMlKit(options.Formats);
            IGmsBarcodeScanner? scanner = null;
            try
            {
                var outcome = await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    var context = (Android.Content.Context?)AndroidContext.CurrentActivity
                        ?? throw new NativeCapabilityException(AndroidContext.NoActivity(Capability));
                    using var builder = new GmsBarcodeScannerOptions.Builder();
                    builder.SetBarcodeFormats(formats[0], formats[1..]);
                    builder.EnableAutoZoom();
                    using var scannerOptions = builder.Build();
                    scanner = GmsBarcodeScanning.GetClient(context, scannerOptions);
                    return GmsTaskBridge.AsTask(scanner.StartScan());
                }).WaitAsync(ct).ConfigureAwait(false);

                switch (outcome.Status)
                {
                    case GmsTaskStatus.Succeeded when outcome.Result is not null:
                        return ToBarcode(outcome.Result);
                    case GmsTaskStatus.Canceled:
                    case GmsTaskStatus.Succeeded:
                        return NativeError.Cancelled(Capability);
                    default:
                        return MapFailure(outcome.Error);
                }
            }
            finally
            {
                scanner?.Dispose();
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    /// <returns>Always <see cref="NativeResultStatus.Unsupported"/>.</returns>
    public ValueTask<NativeResult<IReadOnlyList<Barcode>>> DetectAsync(NativeFile image, BarcodeFormats formats = BarcodeFormats.All, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<NativeResult<IReadOnlyList<Barcode>>>(NativeError.Unsupported(Capability,
            "Detecting barcodes in images requires the ML Kit barcode-scanning model, which the Android adapter does not bundle " +
            "(it adds several MB). Register a custom IBarcodeScanner that uses Xamarin.Google.MLKit.BarcodeScanning " +
            "(BarcodeScanning.GetClient().Process(InputImage)) or ZXing.Net, after calling AddAndroid()."));

    private static Barcode ToBarcode(Java.Lang.Object result)
    {
        using var barcode = Android.Runtime.Extensions.JavaCast<MlKitBarcode>(result);
        using var box = barcode.BoundingBox;
        return new Barcode
        {
            RawValue = barcode.RawValue,
            RawBytes = barcode.GetRawBytes() is { Length: > 0 } bytes ? bytes : null,
            Format = AndroidBarcodeMapper.FromMlKit(barcode.Format),
            Bounds = box is null ? null : AndroidBarcodeMapper.ToBounds(box.Left, box.Top, box.Right, box.Bottom),
        };
    }

    private NativeError MapFailure(Java.Lang.Exception? error)
    {
        switch (error)
        {
            case MlKitException mlKit:
                var mapped = AndroidBarcodeMapper.MapScanError(mlKit.ErrorCode, mlKit.Message);
                if (mapped.Code == AndroidErrorCodes.ModuleUnavailable)
                {
                    RequestModuleInstall();
                }

                LogScanFailed(_logger, mlKit.ErrorCode);
                return mapped with { Exception = mlKit };

            case ApiException api:
                LogScanFailed(_logger, api.StatusCode);
                return NativeError.Unavailable(Capability, AndroidErrorCodes.PlayServicesUnavailable,
                    $"Google Play services rejected the scan: {CommonStatusCodes.GetStatusCodeString(api.StatusCode)}.") with { Exception = api };

            case null:
                return NativeError.Failed(Capability, NativeErrorCodes.PlatformError, "The Google code scanner failed without an error.");

            default:
                return NativeError.Failed(Capability, NativeErrorCodes.PlatformError, error.Message ?? "The Google code scanner failed.", error);
        }
    }

    private NativeError? CheckPlayServices()
    {
        var code = GoogleApiAvailability.Instance.IsGooglePlayServicesAvailable(AndroidContext.App);
        if (code == ConnectionResult.Success)
        {
            return null;
        }

        return NativeError.Unavailable(Capability, AndroidErrorCodes.PlayServicesUnavailable,
            $"Google Play services is not available ({GoogleApiAvailability.Instance.GetErrorString(code)}); the Google code scanner cannot run.")
            with { Details = new Dictionary<string, string> { ["connectionResult"] = code.ToString(System.Globalization.CultureInfo.InvariantCulture) } };
    }

    private static async Task<bool?> IsModuleInstalledAsync(CancellationToken cancellationToken)
    {
        using var client = ModuleInstall.GetClient(AndroidContext.App);
        using var scanner = GmsBarcodeScanning.GetClient(AndroidContext.App);
        var outcome = await GmsTaskBridge.AsTask(client.AreModulesAvailable([scanner]))
            .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        if (outcome.Status != GmsTaskStatus.Succeeded || outcome.Result is null)
        {
            return null;
        }

        using var response = Android.Runtime.Extensions.JavaCast<ModuleAvailabilityResponse>(outcome.Result);
        return response.AreModulesAvailable();
    }

    private void RequestModuleInstall()
    {
        if (!_options.InstallBarcodeScannerModule || Interlocked.Exchange(ref _installRequested, 1) != 0)
        {
            return;
        }

        _ = InstallModuleAsync();
    }

    private async Task InstallModuleAsync()
    {
        try
        {
            using var client = ModuleInstall.GetClient(AndroidContext.App);
            using var scanner = GmsBarcodeScanning.GetClient(AndroidContext.App);
            using var builder = ModuleInstallRequest.NewBuilder();
            builder.AddApi(scanner);
            using var request = builder.Build();
            var outcome = await GmsTaskBridge.AsTask(client.InstallModules(request)).ConfigureAwait(false);
            LogModuleInstallRequested(_logger, outcome.Status);
            if (outcome.Status != GmsTaskStatus.Succeeded)
            {
                Interlocked.Exchange(ref _installRequested, 0);
            }
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _installRequested, 0);
            LogModuleCheckFailed(_logger, ex);
        }
    }

    [LoggerMessage(EventId = 3301, Level = LogLevel.Debug, Message = "BarcodeScanOptions.Timeout and Prompt are not supported by the Google code scanner and were ignored.")]
    private static partial void LogIgnoredOptions(ILogger logger);

    [LoggerMessage(EventId = 3302, Level = LogLevel.Warning, Message = "The Google code scanner failed with code {Code}.")]
    private static partial void LogScanFailed(ILogger logger, int code);

    [LoggerMessage(EventId = 3303, Level = LogLevel.Information, Message = "Requested the Google code scanner module download: {Status}.")]
    private static partial void LogModuleInstallRequested(ILogger logger, GmsTaskStatus status);

    [LoggerMessage(EventId = 3304, Level = LogLevel.Debug, Message = "Checking or installing the Google code scanner module failed.")]
    private static partial void LogModuleCheckFailed(ILogger logger, Exception exception);
}
