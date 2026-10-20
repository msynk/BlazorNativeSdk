namespace BlazorNativeSdk.Devices;

/// <summary>How strongly <see cref="ISecureStorage"/> protects stored values.</summary>
public enum SecureStorageProtection
{
    /// <summary>Values are not protected (only used by test fakes).</summary>
    None = 0,

    /// <summary>
    /// Values are encrypted with a key that the platform cannot fully protect from code running in the same origin
    /// (for example a non-extractable WebCrypto key in a browser). Protects against casual inspection and storage
    /// exfiltration, not against script injection (XSS).
    /// </summary>
    BestEffort = 1,

    /// <summary>Values are encrypted by an operating-system facility bound to the user account (DPAPI, Keychain, libsecret).</summary>
    OperatingSystem = 2,

    /// <summary>Keys are held in hardware (Secure Enclave, StrongBox/TEE-backed Android Keystore).</summary>
    HardwareBacked = 3,
}

/// <summary>Encrypted key/value storage for small secrets such as tokens.</summary>
/// <remarks>
/// Values are scoped to the application. Do not store large payloads; most platforms are optimized for values
/// under a few kilobytes. In browsers, secure storage is disabled by default because no browser API can keep a
/// secret away from scripts running in the page; it can be enabled explicitly with best-effort protection.
/// </remarks>
public interface ISecureStorage : INativeCapability
{
    /// <summary>The protection level provided by the current adapter.</summary>
    SecureStorageProtection Protection { get; }

    /// <summary>Gets a value.</summary>
    /// <returns>The value, or a successful <see langword="null"/> when the key does not exist.</returns>
    ValueTask<NativeResult<string?>> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Stores a value, replacing any existing value.</summary>
    ValueTask<NativeResult> SetAsync(string key, string value, CancellationToken cancellationToken = default);

    /// <summary>Removes a value.</summary>
    /// <returns><see langword="true"/> when a value was removed.</returns>
    ValueTask<NativeResult<bool>> RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Removes all values stored by the application.</summary>
    ValueTask<NativeResult> ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>Well-known app directories.</summary>
public enum AppDirectory
{
    /// <summary>Private, persistent, backed-up (where applicable) application data.</summary>
    AppData,

    /// <summary>Private cache data that the OS may delete under storage pressure.</summary>
    Cache,

    /// <summary>Short-lived temporary files.</summary>
    Temp,

    /// <summary>The user's documents folder (desktop), or the app's user-visible documents container (iOS "Files" app).</summary>
    Documents,

    /// <summary>The user's downloads folder (desktop only).</summary>
    Downloads,
}

/// <summary>Resolves app-specific directories on hosts with a local file system.</summary>
/// <remarks>Browsers have no addressable file system; the browser adapter reports this capability as unsupported.</remarks>
public interface IAppStorage : INativeCapability
{
    /// <summary>Gets the absolute path of an app directory, creating it when needed.</summary>
    NativeResult<string> GetDirectory(AppDirectory directory);
}
