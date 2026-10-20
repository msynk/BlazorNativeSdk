using BlazorNativeSdk.Browser.Interop;
using BlazorNativeSdk.Devices;

namespace BlazorNativeSdk.Browser.Services;

/// <summary>
/// Biometrics in browsers: unsupported, with an explicit reason instead of the generic "no adapter" message.
/// </summary>
internal sealed class BrowserBiometricService : IBiometricService
{
    internal const string UnsupportedReason =
        "Browsers cannot perform local-only biometric checks. WebAuthn/passkeys can use the device's biometric sensor, but " +
        "the result is a signed assertion that only proves something after server-side verification; implement passkeys " +
        "with your server instead of local biometric gating.";

    public CapabilityId Capability => NativeCapabilities.Biometrics;

    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Unsupported(Capability, UnsupportedReason, BrowserConstants.AdapterName));

    public ValueTask<NativeResult<BiometricInfo>> GetInfoAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(NativeResult.Failure<BiometricInfo>(NativeError.Unsupported(Capability, UnsupportedReason)));

    public ValueTask<NativeResult<BiometricAuthentication>> AuthenticateAsync(BiometricRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ValueTask.FromResult(NativeResult.Failure<BiometricAuthentication>(NativeError.Unsupported(Capability, UnsupportedReason)));
    }
}

/// <summary>App directories in browsers: unsupported, with an explicit reason.</summary>
internal sealed class BrowserAppStorage : IAppStorage
{
    internal const string UnsupportedReason =
        "Browsers have no addressable file system for app directories. Use INativeStateStore for small documents, or " +
        "IndexedDB and the Origin Private File System from JavaScript for larger data.";

    public CapabilityId Capability => NativeCapabilities.AppStorage;

    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Unsupported(Capability, UnsupportedReason, BrowserConstants.AdapterName));

    public NativeResult<string> GetDirectory(AppDirectory directory) =>
        NativeResult.Failure<string>(NativeError.Unsupported(Capability, UnsupportedReason));
}
