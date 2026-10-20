using BlazorNativeSdk.Devices;

namespace BlazorNativeSdk.Testing;

/// <summary>Thrown when an implementation violates a capability contract.</summary>
public sealed class ContractViolationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ContractViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public ContractViolationException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public ContractViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Framework-agnostic behavioral checks that every implementation of a capability must pass. Adapter test suites call
/// them against real implementations (on devices or desktop runners) to guarantee consistent semantics across platforms.
/// </summary>
public static class CapabilityContracts
{
    /// <summary>Verifies <see cref="ISecureStorage"/> semantics: missing keys, overwrite, unicode, remove, clear and key isolation.</summary>
    public static async Task VerifySecureStorageAsync(ISecureStorage storage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var prefix = "bns-contract-" + Guid.NewGuid().ToString("N")[..8] + "-";
        var a = prefix + "a";
        var b = prefix + "b";

        var missing = Require(await storage.GetAsync(a, cancellationToken).ConfigureAwait(false), "GetAsync(missing)");
        Ensure(missing is null, "A missing key must return a successful null.");

        Require(await storage.SetAsync(a, "first", cancellationToken).ConfigureAwait(false), "SetAsync");
        Require(await storage.SetAsync(a, "second ✓ ünïcödé", cancellationToken).ConfigureAwait(false), "SetAsync(overwrite)");
        Require(await storage.SetAsync(b, string.Empty, cancellationToken).ConfigureAwait(false), "SetAsync(empty)");

        Ensure(Require(await storage.GetAsync(a, cancellationToken).ConfigureAwait(false), "GetAsync") == "second ✓ ünïcödé", "Values must round-trip exactly, including Unicode, and overwrite previous values.");
        Ensure(Require(await storage.GetAsync(b, cancellationToken).ConfigureAwait(false), "GetAsync(empty)") == string.Empty, "Empty strings must round-trip as empty, not null.");

        Ensure(Require(await storage.RemoveAsync(a, cancellationToken).ConfigureAwait(false), "RemoveAsync"), "RemoveAsync must return true for an existing key.");
        Ensure(!Require(await storage.RemoveAsync(a, cancellationToken).ConfigureAwait(false), "RemoveAsync(missing)"), "RemoveAsync must return false for a missing key.");
        Ensure(Require(await storage.GetAsync(a, cancellationToken).ConfigureAwait(false), "GetAsync(removed)") is null, "Removed keys must read as null.");
        Ensure(Require(await storage.GetAsync(b, cancellationToken).ConfigureAwait(false), "GetAsync(other)") == string.Empty, "Removing a key must not affect other keys.");

        Require(await storage.ClearAsync(cancellationToken).ConfigureAwait(false), "ClearAsync");
        Ensure(Require(await storage.GetAsync(b, cancellationToken).ConfigureAwait(false), "GetAsync(cleared)") is null, "ClearAsync must remove every value.");
    }

    /// <summary>Verifies <see cref="INativeStateStore"/> semantics: missing keys, atomic replace, prefix listing and removal.</summary>
    public static async Task VerifyStateStoreAsync(INativeStateStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var prefix = "bns.contract." + Guid.NewGuid().ToString("N")[..8] + ".";

        Ensure(await store.GetAsync(prefix + "missing", cancellationToken).ConfigureAwait(false) is null, "Missing keys must return null.");
        await store.SetAsync(prefix + "one", "{\"v\":1}", cancellationToken).ConfigureAwait(false);
        await store.SetAsync(prefix + "one", "{\"v\":2}", cancellationToken).ConfigureAwait(false);
        await store.SetAsync(prefix + "two/with:odd*chars", "x", cancellationToken).ConfigureAwait(false);
        await store.SetAsync("other." + prefix, "y", cancellationToken).ConfigureAwait(false);

        Ensure(await store.GetAsync(prefix + "one", cancellationToken).ConfigureAwait(false) == "{\"v\":2}", "SetAsync must replace values.");
        Ensure(await store.GetAsync(prefix + "two/with:odd*chars", cancellationToken).ConfigureAwait(false) == "x", "Keys with arbitrary characters must be supported.");

        var keys = await store.GetKeysAsync(prefix, cancellationToken).ConfigureAwait(false);
        Ensure(keys.Count == 2 && keys.Contains(prefix + "one") && keys.Contains(prefix + "two/with:odd*chars"), "GetKeysAsync must return exactly the keys with the prefix.");

        Ensure(await store.RemoveAsync(prefix + "one", cancellationToken).ConfigureAwait(false), "RemoveAsync must return true for an existing key.");
        Ensure(!await store.RemoveAsync(prefix + "one", cancellationToken).ConfigureAwait(false), "RemoveAsync must return false for a missing key.");
        await store.RemoveAsync(prefix + "two/with:odd*chars", cancellationToken).ConfigureAwait(false);
        await store.RemoveAsync("other." + prefix, cancellationToken).ConfigureAwait(false);
        Ensure((await store.GetKeysAsync(prefix, cancellationToken).ConfigureAwait(false)).Count == 0, "Removed keys must not be listed.");
    }

    /// <summary>Verifies <see cref="IClipboardService"/> text round-trip semantics. Mutates the system clipboard.</summary>
    public static async Task VerifyClipboardAsync(IClipboardService clipboard, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        var text = "bns-contract " + Guid.NewGuid().ToString("N") + " ✓";
        Require(await clipboard.SetTextAsync(text, cancellationToken).ConfigureAwait(false), "SetTextAsync");
        Ensure(Require(await clipboard.GetTextAsync(cancellationToken).ConfigureAwait(false), "GetTextAsync") == text, "Clipboard text must round-trip exactly.");
    }

    /// <summary>Verifies that a capability reports a status consistent with its operations.</summary>
    public static async Task VerifyStatusAsync(INativeCapability capability, CapabilityId expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capability);
        Ensure(capability.Capability == expected, $"Capability id must be '{expected}' but was '{capability.Capability}'.");
        var status = await capability.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        Ensure(status.Capability == expected, "Status must describe the same capability.");
        Ensure(status.Availability is CapabilityAvailability.Available or CapabilityAvailability.RequiresPermission
            || status.Reason is not null, "Non-available statuses must explain why (Reason).");
    }

    private static T Require<T>(NativeResult<T> result, string operation)
    {
        if (!result.IsSuccess)
        {
            throw new ContractViolationException($"{operation} failed: {result.Error}");
        }

        return result.Value!;
    }

    private static void Require(NativeResult result, string operation)
    {
        if (!result.IsSuccess)
        {
            throw new ContractViolationException($"{operation} failed: {result.Error}");
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
        {
            throw new ContractViolationException(message);
        }
    }
}
