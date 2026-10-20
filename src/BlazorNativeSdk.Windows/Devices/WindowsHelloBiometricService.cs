using System.Runtime.InteropServices;
using BlazorNativeSdk.Devices;
using Microsoft.Extensions.Logging;
using Windows.Foundation;
using Windows.Security.Credentials.UI;

namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>Maps Windows Hello results to BlazorNativeSdk results (pure, unit-tested).</summary>
internal static class BiometricResultMapper
{
    private const string BusyMessage = "The Windows Hello device is busy (another verification is in progress).";

    public static NativeResult<BiometricAuthentication> MapVerification(UserConsentVerificationResult result) => result switch
    {
        // Windows does not report whether a face, fingerprint or PIN was used.
        UserConsentVerificationResult.Verified => new BiometricAuthentication(BiometricKinds.Other),
        UserConsentVerificationResult.Canceled => NativeError.Cancelled(NativeCapabilities.Biometrics),
        UserConsentVerificationResult.RetriesExhausted => NativeError.Unavailable(NativeCapabilities.Biometrics, NativeErrorCodes.BiometricLockedOut, "Too many failed attempts; Windows Hello is temporarily locked."),
        UserConsentVerificationResult.DeviceNotPresent => NativeError.Unavailable(NativeCapabilities.Biometrics, NativeErrorCodes.HardwareUnavailable, "No Windows Hello capable device is available."),
        UserConsentVerificationResult.NotConfiguredForUser => NativeError.Unavailable(NativeCapabilities.Biometrics, NativeErrorCodes.BiometricNotEnrolled, "Windows Hello is not set up for this user."),
        UserConsentVerificationResult.DisabledByPolicy => NativeError.Unavailable(NativeCapabilities.Biometrics, NativeErrorCodes.PermissionRestricted, "Windows Hello is disabled by policy."),
        UserConsentVerificationResult.DeviceBusy => NativeError.Unavailable(NativeCapabilities.Biometrics, NativeErrorCodes.HardwareUnavailable, BusyMessage),
        _ => NativeError.Failed(NativeCapabilities.Biometrics, NativeErrorCodes.PlatformError, $"Unexpected Windows Hello result '{result}'."),
    };

    public static NativeResult<BiometricInfo> MapAvailability(UserConsentVerifierAvailability availability) => availability switch
    {
        UserConsentVerifierAvailability.Available => new BiometricInfo
        {
            IsHardwarePresent = true,
            IsEnrolled = true,
            AvailableKinds = BiometricKinds.Other | BiometricKinds.DeviceCredential,
            Strength = BiometricStrength.Strong,
        },
        UserConsentVerifierAvailability.DeviceNotPresent => new BiometricInfo { IsHardwarePresent = false, IsEnrolled = false },
        UserConsentVerifierAvailability.NotConfiguredForUser => new BiometricInfo { IsHardwarePresent = true, IsEnrolled = false },
        UserConsentVerifierAvailability.DisabledByPolicy => NativeError.Unavailable(NativeCapabilities.Biometrics, NativeErrorCodes.PermissionRestricted, "Windows Hello is disabled by policy."),
        UserConsentVerifierAvailability.DeviceBusy => NativeError.Unavailable(NativeCapabilities.Biometrics, NativeErrorCodes.HardwareUnavailable, BusyMessage),
        _ => NativeError.Failed(NativeCapabilities.Biometrics, NativeErrorCodes.PlatformError, $"Unexpected Windows Hello availability '{availability}'."),
    };

    public static CapabilityStatus MapStatus(UserConsentVerifierAvailability availability) => availability switch
    {
        UserConsentVerifierAvailability.Available =>
            CapabilityStatus.Available(NativeCapabilities.Biometrics, WindowsAdapter.Name, BiometricFeatures.DeviceCredential),
        UserConsentVerifierAvailability.DeviceNotPresent =>
            CapabilityStatus.Unavailable(NativeCapabilities.Biometrics, NativeErrorCodes.HardwareUnavailable, "No Windows Hello capable device is available.", WindowsAdapter.Name),
        UserConsentVerifierAvailability.NotConfiguredForUser =>
            CapabilityStatus.Unavailable(NativeCapabilities.Biometrics, NativeErrorCodes.BiometricNotEnrolled, "Windows Hello is not set up for this user.", WindowsAdapter.Name),
        UserConsentVerifierAvailability.DisabledByPolicy =>
            CapabilityStatus.Unavailable(NativeCapabilities.Biometrics, NativeErrorCodes.PermissionRestricted, "Windows Hello is disabled by policy.", WindowsAdapter.Name),
        UserConsentVerifierAvailability.DeviceBusy =>
            CapabilityStatus.Unavailable(NativeCapabilities.Biometrics, NativeErrorCodes.HardwareUnavailable, BusyMessage, WindowsAdapter.Name),
        _ => CapabilityStatus.Unknown(NativeCapabilities.Biometrics, $"Unexpected Windows Hello availability '{availability}'.", WindowsAdapter.Name),
    };
}

/// <summary>
/// <see cref="IBiometricService"/> over Windows Hello (<see cref="UserConsentVerifier"/>). When the host has a main window
/// the prompt is parented to it through <c>IUserConsentVerifierInterop</c> so it appears in the foreground.
/// </summary>
/// <remarks>
/// Windows Hello always offers the user's PIN as an alternative to face or fingerprint, so
/// <see cref="BiometricRequest.AllowDeviceCredential"/> cannot be turned off, and the method used is reported as
/// <see cref="BiometricKinds.Other"/>. The prompt shows <see cref="BiometricRequest.Title"/> and
/// <see cref="BiometricRequest.Reason"/> as its message; the cancel label cannot be customized.
/// </remarks>
internal sealed partial class WindowsHelloBiometricService : IBiometricService
{
    private readonly IWindowsHostBridge _bridge;
    private readonly ILogger _logger;

    public WindowsHelloBiometricService(IWindowsHostBridge bridge, ILogger<WindowsHelloBiometricService> logger)
    {
        _bridge = bridge;
        _logger = logger;
    }

    public CapabilityId Capability => NativeCapabilities.Biometrics;

    public async ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync().AsTask(cancellationToken).ConfigureAwait(false);
            return BiometricResultMapper.MapStatus(availability);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return CapabilityStatus.FromError(NativeError.FromException(Capability, ex), WindowsAdapter.Name);
        }
    }

    public ValueTask<NativeResult<BiometricInfo>> GetInfoAsync(CancellationToken cancellationToken = default) =>
        NativeOperation.RunAsync(
            Capability,
            async ct =>
            {
                var availability = await UserConsentVerifier.CheckAvailabilityAsync().AsTask(ct).ConfigureAwait(false);
                return BiometricResultMapper.MapAvailability(availability);
            },
            cancellationToken);

    public ValueTask<NativeResult<BiometricAuthentication>> AuthenticateAsync(BiometricRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Reason);
        return NativeOperation.RunAsync(
            Capability,
            async ct =>
            {
                var message = string.IsNullOrWhiteSpace(request.Title) ? request.Reason : request.Title + Environment.NewLine + request.Reason;
                var operation = await StartVerificationAsync(message).ConfigureAwait(false);
                var result = await operation.AsTask(ct).ConfigureAwait(false);
                return BiometricResultMapper.MapVerification(result);
            },
            cancellationToken);
    }

    private async Task<IAsyncOperation<UserConsentVerificationResult>> StartVerificationAsync(string message)
    {
        var hwnd = _bridge.MainWindowHandle;
        if (hwnd != 0)
        {
            try
            {
                return await _bridge.RunOnUIThreadAsync(() => WinRTInterop.RequestVerificationForWindow(hwnd, message)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or EntryPointNotFoundException)
            {
                LogInteropFallback(_logger, ex);
            }
        }

        return UserConsentVerifier.RequestVerificationAsync(message);
    }

    [LoggerMessage(EventId = 3401, Level = LogLevel.Warning, Message = "Windows Hello could not be parented to the main window; falling back to an unparented prompt.")]
    private static partial void LogInteropFallback(ILogger logger, Exception exception);
}
