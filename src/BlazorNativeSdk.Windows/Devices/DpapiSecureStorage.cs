using System.Security.Cryptography;
using System.Text;
using BlazorNativeSdk.Devices;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>
/// <see cref="ISecureStorage"/> backed by DPAPI (<see cref="ProtectedData"/>, <see cref="DataProtectionScope.CurrentUser"/>).
/// Each key is stored in its own file named by the SHA-256 of the key, written atomically (temporary file + rename).
/// </summary>
/// <remarks>
/// The DPAPI entropy is derived from the app id and the key, so values of other apps cannot be decrypted by this app and a
/// file copied over another key's file fails to decrypt instead of returning the wrong secret. DPAPI ties values to the
/// Windows user account (and its credentials): they do not roam to other machines and become unreadable when the user's
/// password is reset by an administrator.
/// </remarks>
internal sealed class DpapiSecureStorage : ISecureStorage
{
    private const byte FormatVersion = 1;
    private const string Extension = ".dat";
    private readonly Lock _gate = new();
    private readonly byte[] _appEntropy;

    public DpapiSecureStorage(IOptions<NativeSdkOptions> sdkOptions, IOptions<WindowsNativeOptions> options)
        : this(
            options.Value.SecureStorageDirectory ??
                Path.Combine(WindowsAppStorage.GetAppDataRoot(WindowsAdapter.ToFolderName(sdkOptions.Value.AppId)), "securestorage"),
            sdkOptions.Value.AppId)
    {
    }

    internal DpapiSecureStorage(string directory, string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        Directory = Path.GetFullPath(directory);
        _appEntropy = SHA256.HashData(Encoding.UTF8.GetBytes("BlazorNativeSdk.SecureStorage/v1\0" + appId));
    }

    /// <summary>The storage directory.</summary>
    public string Directory { get; }

    public CapabilityId Capability => NativeCapabilities.SecureStorage;

    public SecureStorageProtection Protection => SecureStorageProtection.OperatingSystem;

    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Available(Capability, WindowsAdapter.Name));

    public ValueTask<NativeResult<string?>> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        cancellationToken.ThrowIfCancellationRequested();
        byte[]? payload;
        try
        {
            var path = GetPath(key);
            lock (_gate)
            {
                payload = File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult<NativeResult<string?>>(NativeError.FromException(Capability, ex));
        }

        if (payload is null)
        {
            return ValueTask.FromResult(NativeResult.Success<string?>(null));
        }

        if (payload.Length < 2 || payload[0] != FormatVersion)
        {
            return ValueTask.FromResult<NativeResult<string?>>(
                NativeError.Failed(Capability, NativeErrorCodes.PlatformError, "The stored value has an unrecognized format."));
        }

        byte[]? plain = null;
        try
        {
            plain = ProtectedData.Unprotect(payload.AsSpan(1).ToArray(), GetEntropy(key), DataProtectionScope.CurrentUser);
            return ValueTask.FromResult(NativeResult.Success<string?>(Encoding.UTF8.GetString(plain)));
        }
        catch (CryptographicException ex)
        {
            return ValueTask.FromResult<NativeResult<string?>>(NativeError.Failed(
                Capability,
                NativeErrorCodes.PlatformError,
                "The stored value could not be decrypted (it was written by another user, app or machine, or is corrupted).",
                ex));
        }
        finally
        {
            if (plain is not null)
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
    }

    public ValueTask<NativeResult> SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();

        var plain = Encoding.UTF8.GetBytes(value);
        string? temp = null;
        try
        {
            var protectedBytes = ProtectedData.Protect(plain, GetEntropy(key), DataProtectionScope.CurrentUser);
            var payload = new byte[protectedBytes.Length + 1];
            payload[0] = FormatVersion;
            protectedBytes.CopyTo(payload, 1);

            var path = GetPath(key);
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temp, payload);
                File.Move(temp, path, overwrite: true);
                temp = null;
            }

            return ValueTask.FromResult(NativeResult.Ok);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return ValueTask.FromResult<NativeResult>(NativeError.FromException(Capability, ex));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (temp is not null)
            {
                TryDelete(temp);
            }
        }
    }

    public ValueTask<NativeResult<bool>> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var path = GetPath(key);
            lock (_gate)
            {
                if (!File.Exists(path))
                {
                    return ValueTask.FromResult(NativeResult.Success(false));
                }

                File.Delete(path);
                return ValueTask.FromResult(NativeResult.Success(true));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult<NativeResult<bool>>(NativeError.FromException(Capability, ex));
        }
    }

    public ValueTask<NativeResult> ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            lock (_gate)
            {
                if (System.IO.Directory.Exists(Directory))
                {
                    foreach (var file in System.IO.Directory.EnumerateFiles(Directory))
                    {
                        if (file.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) || file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(file);
                        }
                    }
                }
            }

            return ValueTask.FromResult(NativeResult.Ok);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult<NativeResult>(NativeError.FromException(Capability, ex));
        }
    }

    private string GetPath(string key) =>
        Path.Combine(Directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + Extension);

    private byte[] GetEntropy(string key) => HMACSHA256.HashData(_appEntropy, Encoding.UTF8.GetBytes(key));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover temporary file is removed by ClearAsync.
        }
    }
}
