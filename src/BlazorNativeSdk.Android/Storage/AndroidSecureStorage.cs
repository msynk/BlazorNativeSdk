using BlazorNativeSdk.Devices;
using Microsoft.Extensions.Logging;
using SecureStorage = Microsoft.Maui.Storage.SecureStorage;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>
/// <see cref="ISecureStorage"/> for Android based on Microsoft.Maui.Essentials <c>SecureStorage</c>: values are
/// encrypted with AndroidX <c>EncryptedSharedPreferences</c> under a key held by the Android Keystore.
/// </summary>
/// <remarks>
/// <see cref="Protection"/> is <see cref="SecureStorageProtection.OperatingSystem"/>: the Keystore key is usually
/// TEE-backed, but the adapter does not verify hardware backing on each device. Keystore keys are not part of Android Auto
/// Backup, so restored encrypted preferences cannot be decrypted on a new device; exclude the Essentials secure-storage
/// preferences file from backup rules (see the Android adapter documentation). Operations run off the UI thread.
/// </remarks>
public sealed partial class AndroidSecureStorage : ISecureStorage
{
    private const string AdapterName = "android";
    private readonly ILogger<AndroidSecureStorage> _logger;

    /// <summary>Creates the storage.</summary>
    public AndroidSecureStorage(ILogger<AndroidSecureStorage> logger) => _logger = logger;

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.SecureStorage;

    /// <inheritdoc />
    public SecureStorageProtection Protection => SecureStorageProtection.OperatingSystem;

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Available(Capability, AdapterName, "android-keystore"));

    /// <inheritdoc />
    public ValueTask<NativeResult<string?>> GetAsync(string key, CancellationToken cancellationToken = default) =>
        NativeOperation.RunAsync<string?>(Capability, async ct =>
        {
            if (InvalidKey(key) is { } invalid)
            {
                return invalid;
            }

            var value = await Task.Run(() => SecureStorage.Default.GetAsync(key), ct).ConfigureAwait(false);
            return NativeResult.Success<string?>(value);
        }, cancellationToken);

    /// <inheritdoc />
    public ValueTask<NativeResult> SetAsync(string key, string value, CancellationToken cancellationToken = default) =>
        NativeOperation.RunAsync(Capability, async ct =>
        {
            if (InvalidKey(key) is { } invalid)
            {
                return invalid;
            }

            if (value is null)
            {
                return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "The value must not be null; use RemoveAsync to delete a key.");
            }

            await Task.Run(() => SecureStorage.Default.SetAsync(key, value), ct).ConfigureAwait(false);
            return NativeResult.Ok;
        }, cancellationToken);

    /// <inheritdoc />
    public ValueTask<NativeResult<bool>> RemoveAsync(string key, CancellationToken cancellationToken = default) =>
        NativeOperation.RunAsync<bool>(Capability, async ct =>
        {
            if (InvalidKey(key) is { } invalid)
            {
                return invalid;
            }

            return await Task.Run(() => SecureStorage.Default.Remove(key), ct).ConfigureAwait(false);
        }, cancellationToken);

    /// <inheritdoc />
    public ValueTask<NativeResult> ClearAsync(CancellationToken cancellationToken = default) =>
        NativeOperation.TryAsync(Capability, ct => Task.Run(() =>
        {
            SecureStorage.Default.RemoveAll();
            LogCleared(_logger);
        }, ct), cancellationToken);

    private NativeError? InvalidKey(string key) =>
        string.IsNullOrWhiteSpace(key)
            ? NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "The key must not be null or whitespace.")
            : null;

    [LoggerMessage(EventId = 3501, Level = LogLevel.Information, Message = "Secure storage was cleared.")]
    private static partial void LogCleared(ILogger logger);
}
