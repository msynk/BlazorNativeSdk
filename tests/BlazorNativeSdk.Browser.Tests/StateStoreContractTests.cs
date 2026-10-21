using System.Text.Json;
using BlazorNativeSdk.Browser.Interop;
using BlazorNativeSdk.Browser.Services;
using BlazorNativeSdk.Testing;
using Bunit;
using TestContext = Xunit.TestContext;

namespace BlazorNativeSdk.Browser.Tests;

/// <summary>
/// Runs the state store contract against <see cref="LocalStorageStateStore"/> with an in-memory <c>window.localStorage</c>
/// behind the module's <c>state*</c> functions. Those functions are one-line wrappers around the Storage API, so emulating
/// them exactly keeps the test honest: the C# store, the interop envelope handling and the key prefixing are the real code.
/// </summary>
public sealed class StateStoreContractTests
{
    [Fact]
    public async Task Local_storage_state_store_satisfies_the_contract()
    {
        await using var fixture = new BrowserFixture();
        var localStorage = new LocalStorageEmulator();
        localStorage.Items["unrelated-app-key"] = "x";
        fixture.Session.AddInvocationHandler(localStorage);
        var store = new LocalStorageStateStore(fixture.Get<BrowserInterop>());

        await CapabilityContracts.VerifyStateStoreAsync(store, TestContext.Current.CancellationToken);

        // Every key the store used was namespaced, and the contract removed all of them.
        Assert.Equal(["unrelated-app-key"], localStorage.Items.Keys);
        Assert.Contains(fixture.Session.Invocations, i => i.Identifier == "stateSet" && ((string)i.Arguments[0]!).StartsWith("bns:", StringComparison.Ordinal));
    }

    /// <summary>A port of the module's <c>stateGet</c>, <c>stateSet</c>, <c>stateRemove</c> and <c>stateKeys</c> over a dictionary.</summary>
    private sealed class LocalStorageEmulator() : JSRuntimeInvocationHandlerBase<JsonElement>(
        static invocation => invocation.Identifier is "stateGet" or "stateSet" or "stateRemove" or "stateKeys",
        isCatchAllHandler: false)
    {
        public Dictionary<string, string> Items { get; } = new(StringComparer.Ordinal);

        protected override Task<JsonElement> HandleAsync(JSRuntimeInvocation invocation)
        {
            var key = (string)invocation.Arguments[0]!;
            object? value;
            lock (Items)
            {
                switch (invocation.Identifier)
                {
                    case "stateGet":
                        value = Items.GetValueOrDefault(key);
                        break;
                    case "stateSet":
                        Items[key] = (string)invocation.Arguments[1]!;
                        value = null;
                        break;
                    case "stateRemove":
                        value = Items.Remove(key);
                        break;
                    default:
                        value = Items.Keys.Where(k => k.StartsWith(key, StringComparison.Ordinal)).ToArray();
                        break;
                }
            }

            return Task.FromResult(JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["ok"] = true, ["value"] = value }));
        }
    }
}
