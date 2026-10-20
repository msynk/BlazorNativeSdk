using System.Text.Json;
using BlazorNativeSdk.Browser.Interop;
using BlazorNativeSdk.Devices;
using Microsoft.Extensions.Logging;

namespace BlazorNativeSdk.Browser.Services;

/// <summary>
/// Best-effort secret storage: disabled by default. When enabled with
/// <see cref="BrowserNativeOptions.EnableBestEffortSecureStorage"/>, values are encrypted with AES-GCM 256 using a
/// non-extractable WebCrypto key; key, per-value random 96-bit IVs and ciphertexts are stored in IndexedDB. The key name
/// is bound to each ciphertext as additional authenticated data, so values cannot be swapped between keys.
/// </summary>
internal sealed partial class BrowserSecureStorage : ISecureStorage
{
    internal const string DisabledReason =
        "Browsers cannot protect secrets from scripts running in the page; set BrowserNativeOptions.EnableBestEffortSecureStorage to opt in.";

    private readonly BrowserInterop _interop;
    private readonly ILogger<BrowserSecureStorage> _logger;
    private readonly bool _enabled;
    private readonly string _database;

    public BrowserSecureStorage(BrowserInterop interop, ILogger<BrowserSecureStorage> logger)
    {
        _interop = interop;
        _logger = logger;
        _enabled = interop.Options.EnableBestEffortSecureStorage;
        _database = interop.Options.SecureStorageDatabaseName;
    }

    public CapabilityId Capability => NativeCapabilities.SecureStorage;

    public SecureStorageProtection Protection => _enabled ? SecureStorageProtection.BestEffort : SecureStorageProtection.None;

    public async ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!_enabled)
        {
            return CapabilityStatus.Unsupported(Capability, DisabledReason, BrowserConstants.AdapterName);
        }

        var probe = await _interop.ProbeAsync(Capability, "secure-storage", cancellationToken).ConfigureAwait(false);
        if (!probe.IsSuccess)
        {
            return BrowserInteropExtensions.ToStatus(Capability, probe.Error);
        }

        if (!probe.Value.SecureContext)
        {
            return CapabilityStatus.Unavailable(Capability, NativeErrorCodes.InsecureContext,
                "WebCrypto is only available in a secure context (HTTPS or localhost).", BrowserConstants.AdapterName);
        }

        if (!probe.Value.IndexedDb || !probe.Value.SubtleCrypto)
        {
            return CapabilityStatus.Unavailable(Capability, NativeErrorCodes.NotSupported,
                "IndexedDB or WebCrypto is not available (for example in some private browsing modes or sandboxed frames).", BrowserConstants.AdapterName);
        }

        return CapabilityStatus.Available(Capability, BrowserConstants.AdapterName);
    }

    public async ValueTask<NativeResult<string?>> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (!_enabled)
        {
            return Disabled();
        }

        var result = await _interop.InvokeAsync(Capability, "secureGet", [_database, key], cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            LogFailed(_logger, "get", result.Error.Code);
            return result.Error;
        }

        return result.Value.ValueKind == JsonValueKind.String
            ? NativeResult.Success<string?>(result.Value.GetString())
            : NativeResult.Success<string?>(null);
    }

    public async ValueTask<NativeResult> SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        if (!_enabled)
        {
            return Disabled();
        }

        var result = await _interop.InvokeAsync(Capability, "secureSet", [_database, key, value], cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            LogFailed(_logger, "set", result.Error.Code);
        }

        return result.WithoutValue();
    }

    public async ValueTask<NativeResult<bool>> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (!_enabled)
        {
            return Disabled();
        }

        var result = await _interop.InvokeAsync(Capability, "secureRemove", [_database, key], cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            LogFailed(_logger, "remove", result.Error.Code);
            return result.Error;
        }

        return result.Value.ValueKind == JsonValueKind.True;
    }

    public async ValueTask<NativeResult> ClearAsync(CancellationToken cancellationToken = default)
    {
        if (!_enabled)
        {
            return Disabled();
        }

        var result = await _interop.InvokeAsync(Capability, "secureClear", [_database], cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            LogFailed(_logger, "clear", result.Error.Code);
        }

        return result.WithoutValue();
    }

    private NativeError Disabled() => NativeError.Unsupported(Capability, DisabledReason);

    // Only the operation and the error code are logged; keys and values never are.
    [LoggerMessage(EventId = 3401, Level = LogLevel.Debug, Message = "Browser secure storage {Operation} failed: {Code}.")]
    private static partial void LogFailed(ILogger logger, string operation, string code);
}
