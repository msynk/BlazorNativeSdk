using System.Globalization;
using System.Text;
using BlazorNativeSdk.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Security;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary><see cref="ISecureStorage"/> backed by the Keychain (generic password items).</summary>
/// <remarks>
/// <para>
/// Items use <c>kSecClassGenericPassword</c> with the service <see cref="AppleNativeOptions.KeychainService"/> (default:
/// <see cref="NativeSdkOptions.AppId"/>) and the key as the account. Accessibility defaults to
/// <c>AfterFirstUnlockThisDeviceOnly</c>: readable by background work after the first unlock, never synced to iCloud and never
/// restored to another device.
/// </para>
/// <para>
/// <see cref="Protection"/> is <see cref="SecureStorageProtection.HardwareBacked"/> on devices, because Keychain items are
/// encrypted with data-protection class keys that the Secure Enclave protects. The simulator has no Secure Enclave, and on Mac
/// Catalyst the data-protection keychain is protected by the user's login session (Intel Macs without a T2 chip have no Secure
/// Enclave), so both report <see cref="SecureStorageProtection.OperatingSystem"/>.
/// </para>
/// <para>
/// The simulator and Mac Catalyst need a <c>keychain-access-groups</c> entitlement (Entitlements.plist); without it the Keychain
/// returns <c>errSecMissingEntitlement</c>, reported as <see cref="NativeErrorCodes.MissingDeclaration"/>. Keychain items
/// survive app deletion on iOS.
/// </para>
/// </remarks>
public sealed partial class AppleSecureStorage : ISecureStorage
{
    /// <summary>
    /// Error code reported when the Keychain is locked (device locked and the item requires an unlocked device). Equal to
    /// <see cref="NativeErrorCodes.ProtectedDataUnavailable"/>.
    /// </summary>
    public const string KeychainLockedCode = NativeErrorCodes.ProtectedDataUnavailable;

    private readonly string _service;
    private readonly string? _accessGroup;
    private readonly SecAccessible _accessible;
    private readonly ILogger<AppleSecureStorage> _logger;

    /// <summary>Creates the storage.</summary>
    public AppleSecureStorage(IOptions<AppleNativeOptions> options, IOptions<NativeSdkOptions> sdkOptions, ILogger<AppleSecureStorage> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sdkOptions);
        _service = string.IsNullOrWhiteSpace(options.Value.KeychainService) ? sdkOptions.Value.AppId : options.Value.KeychainService;
        _accessGroup = string.IsNullOrWhiteSpace(options.Value.KeychainAccessGroup) ? null : options.Value.KeychainAccessGroup;
        _accessible = options.Value.KeychainAccessible;
        _logger = logger;
    }

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.SecureStorage;

    /// <inheritdoc />
    public SecureStorageProtection Protection =>
        AppleHost.IsSimulator || OperatingSystem.IsMacCatalyst() ? SecureStorageProtection.OperatingSystem : SecureStorageProtection.HardwareBacked;

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Available(Capability, AppleHost.AdapterName, Protection.ToString()));

    /// <inheritdoc />
    public ValueTask<NativeResult<string?>> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return RunKeychainAsync<string?>(() =>
        {
            using var query = CreateQuery(key);
            using var data = SecKeyChain.QueryAsData(query, false, out var status);
            if (status == SecStatusCode.ItemNotFound)
            {
                return NativeResult.Success<string?>(null);
            }

            if (status != SecStatusCode.Success || data is null)
            {
                return Map(status, "read");
            }

            return Encoding.UTF8.GetString(data.ToArray());
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<NativeResult> SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        var result = RunKeychainAsync<bool>(() =>
        {
            using var data = NSData.FromArray(Encoding.UTF8.GetBytes(value));
            using var query = CreateQuery(key);
            // The attributes to update must not contain kSecClass or meta keys, so start from an empty record.
            using var update = new SecRecord { ValueData = data, Accessible = _accessible };
            var status = SecKeyChain.Update(query, update);
            if (status == SecStatusCode.ItemNotFound)
            {
                using var record = CreateQuery(key);
                record.ValueData = data;
                record.Accessible = _accessible;
                status = SecKeyChain.Add(record);
                if (status == SecStatusCode.DuplicateItem)
                {
                    // Another writer added the item concurrently.
                    status = SecKeyChain.Update(query, update);
                }
            }

            return status == SecStatusCode.Success ? true : Map(status, "write");
        }, cancellationToken);
        return ToNonGeneric(result);
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<bool>> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return RunKeychainAsync<bool>(() =>
        {
            using var query = CreateQuery(key);
            var status = SecKeyChain.Remove(query);
            return status switch
            {
                SecStatusCode.Success => true,
                SecStatusCode.ItemNotFound => false,
                _ => Map(status, "delete"),
            };
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<NativeResult> ClearAsync(CancellationToken cancellationToken = default)
    {
        var result = RunKeychainAsync<bool>(() =>
        {
            using var query = CreateQuery(account: null);
            var status = SecKeyChain.Remove(query);
            return status is SecStatusCode.Success or SecStatusCode.ItemNotFound ? true : Map(status, "clear");
        }, cancellationToken);
        return ToNonGeneric(result);
    }

    private SecRecord CreateQuery(string? account)
    {
        var record = new SecRecord(SecKind.GenericPassword) { Service = _service };
        if (account is not null)
        {
            record.Account = account;
        }

        if (_accessGroup is not null)
        {
            record.AccessGroup = _accessGroup;
        }

        if (OperatingSystem.IsMacCatalyst())
        {
            // Use the iOS-style data-protection keychain rather than the legacy file-based macOS keychain.
            record.UseDataProtectionKeychain = true;
        }

        return record;
    }

    private NativeError Map(SecStatusCode status, string operation)
    {
        LogKeychainFailure(_logger, operation, (int)status);
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["osstatus"] = ((int)status).ToString(CultureInfo.InvariantCulture),
            ["status"] = status.ToString(),
        };

        var error = status switch
        {
            SecStatusCode.MissingEntitlement => NativeError.Unavailable(Capability, NativeErrorCodes.MissingDeclaration,
                "The Keychain rejected the request with errSecMissingEntitlement. Add a 'keychain-access-groups' entitlement (Entitlements.plist), " +
                "which the iOS simulator and Mac Catalyst require, and make sure KeychainAccessGroup matches it."),
            SecStatusCode.InteractionNotAllowed => NativeError.Unavailable(Capability, KeychainLockedCode,
                "The Keychain item is not accessible while the device is locked. Retry after the user unlocks the device, or use a less restrictive KeychainAccessible value."),
            _ => NativeError.Failed(Capability, NativeErrorCodes.PlatformError, $"The Keychain {operation} failed with OSStatus {(int)status} ({status})."),
        };
        return error with { Details = details };
    }

    private ValueTask<NativeResult<T>> RunKeychainAsync<T>(Func<NativeResult<T>> operation, CancellationToken cancellationToken) =>
        NativeOperation.RunAsync<T>(Capability, async token =>
        {
            // Keychain calls block on securityd; keep them off the UI thread.
            return await Task.Run(operation, token).ConfigureAwait(false);
        }, cancellationToken);

    private static async ValueTask<NativeResult> ToNonGeneric(ValueTask<NativeResult<bool>> result) =>
        (await result.ConfigureAwait(false)).WithoutValue();

    [LoggerMessage(EventId = 3601, Level = LogLevel.Warning, Message = "Keychain {Operation} failed with OSStatus {Status}.")]
    private static partial void LogKeychainFailure(ILogger logger, string operation, int status);
}
