using System.Text.Json;
using BlazorNativeSdk.Browser.Interop;

namespace BlazorNativeSdk.Browser.Services;

/// <summary>
/// A durable <see cref="INativeStateStore"/> backed by <c>window.localStorage</c>, used by Blazor WebAssembly. Keys are
/// prefixed with <c>bns:</c>. Values are stored unencrypted and are visible to every script of the origin.
/// </summary>
/// <remarks>
/// Not used with Blazor Server: the SDK's state belongs to the server process there, so the in-memory (or a file-based)
/// store registered by the host is kept. localStorage is limited to about 5 MB per origin; exceeding it throws.
/// </remarks>
internal sealed class LocalStorageStateStore : INativeStateStore
{
    private static readonly CapabilityId s_capability = new("state-store");
    private readonly BrowserInterop _interop;

    public LocalStorageStateStore(BrowserInterop interop) => _interop = interop;

    public async ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var value = await InvokeAsync("stateGet", [BrowserConstants.StateStorePrefix + key], cancellationToken).ConfigureAwait(false);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    public async ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        await InvokeAsync("stateSet", [BrowserConstants.StateStorePrefix + key, value], cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var value = await InvokeAsync("stateRemove", [BrowserConstants.StateStorePrefix + key], cancellationToken).ConfigureAwait(false);
        return value.ValueKind == JsonValueKind.True;
    }

    public async ValueTask<IReadOnlyList<string>> GetKeysAsync(string prefix, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var value = await InvokeAsync("stateKeys", [BrowserConstants.StateStorePrefix + prefix], cancellationToken).ConfigureAwait(false);
        var keys = value.ValueKind == JsonValueKind.Array ? value.Deserialize(BrowserJsonContext.Default.StringArray) ?? [] : [];
        return [.. keys
            .Where(k => k.StartsWith(BrowserConstants.StateStorePrefix, StringComparison.Ordinal))
            .Select(k => k[BrowserConstants.StateStorePrefix.Length..])
            .Order(StringComparer.Ordinal)];
    }

    // The state store contract throws on infrastructure failures: callers cannot continue without durable state.
    private async ValueTask<JsonElement> InvokeAsync(string identifier, object?[] args, CancellationToken cancellationToken)
    {
        var result = await _interop.InvokeAsync(s_capability, identifier, args, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new IOException($"localStorage operation failed: {result.Error.Message}", result.Error.ToException());
        }

        return result.Value;
    }
}
