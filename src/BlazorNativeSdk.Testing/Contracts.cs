using System.Diagnostics;
using System.Text;
using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;

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
    /// <remarks>Calls <see cref="ISecureStorage.ClearAsync"/>, which removes every value of the app: run it against an isolated store.</remarks>
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

    /// <summary>
    /// Verifies <see cref="IClipboardService"/> semantics: text round-trips exactly, <see langword="null"/> clears the text, and
    /// <see cref="IClipboardService.ContentChanged"/> is raised when the status reports <see cref="ClipboardFeatures.ChangeNotification"/>.
    /// </summary>
    /// <remarks>Mutates the system clipboard; the previous text is restored afterwards when it could be read.</remarks>
    public static async Task VerifyClipboardAsync(IClipboardService clipboard, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        var status = await clipboard.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var previous = await clipboard.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var notifies = status.Supports(ClipboardFeatures.ChangeNotification);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, EventArgs e) => changed.TrySetResult();
        if (notifies)
        {
            clipboard.ContentChanged += OnChanged;
        }

        try
        {
            var text = "bns-contract " + Guid.NewGuid().ToString("N") + " ✓";
            Require(await clipboard.SetTextAsync(text, cancellationToken).ConfigureAwait(false), "SetTextAsync");
            Ensure(Require(await clipboard.GetTextAsync(cancellationToken).ConfigureAwait(false), "GetTextAsync") == text, "Clipboard text must round-trip exactly.");
            if (notifies)
            {
                await WithinAsync(changed.Task, "ContentChanged must be raised after the clipboard changes when the change-notification feature is reported.", cancellationToken).ConfigureAwait(false);
            }

            Require(await clipboard.SetTextAsync(null, cancellationToken).ConfigureAwait(false), "SetTextAsync(null)");
            Ensure(Require(await clipboard.GetTextAsync(cancellationToken).ConfigureAwait(false), "GetTextAsync(cleared)") is null, "SetTextAsync(null) must clear the clipboard text.");
        }
        finally
        {
            if (notifies)
            {
                clipboard.ContentChanged -= OnChanged;
            }

            if (previous.IsSuccess)
            {
                await clipboard.SetTextAsync(previous.Value, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Verifies <see cref="IAppStorage"/> semantics: every directory either succeeds with an absolute, existing, writable
    /// and stable path, or returns <see cref="NativeResultStatus.Unsupported"/> (never an exception). When the capability is
    /// available, the app-private <see cref="AppDirectory.AppData"/>, <see cref="AppDirectory.Cache"/> and
    /// <see cref="AppDirectory.Temp"/> directories must succeed, and <see cref="AppDirectory.AppData"/> must not live inside the
    /// purgeable cache or temp directories. An undefined <see cref="AppDirectory"/> value must fail.
    /// </summary>
    /// <remarks>
    /// Writability is probed with a uniquely named temporary file that is deleted when it is closed, including in user folders
    /// such as <see cref="AppDirectory.Documents"/> and <see cref="AppDirectory.Downloads"/>. Directories are left in place.
    /// </remarks>
    public static async Task VerifyAppStorageAsync(IAppStorage storage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        Ensure(storage.Capability == NativeCapabilities.AppStorage, $"Capability id must be '{NativeCapabilities.AppStorage}' but was '{storage.Capability}'.");
        var status = await storage.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var paths = new Dictionary<AppDirectory, string>();
        foreach (var directory in Enum.GetValues<AppDirectory>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = GetDirectory(storage, directory);
            if (status.IsUnsupported)
            {
                Ensure(result.Status == NativeResultStatus.Unsupported, $"GetDirectory({directory}) must return Unsupported when the capability reports Unsupported, but returned {result}.");
                continue;
            }

            if (!result.IsSuccess)
            {
                Ensure(result.Status == NativeResultStatus.Unsupported || !status.IsAvailable,
                    $"GetDirectory({directory}) must return a path or Unsupported, but returned {result.Error}.");
                Ensure(!status.IsAvailable || directory is AppDirectory.Documents or AppDirectory.Downloads,
                    $"{directory} is app-private and must be available whenever the capability is available, but GetDirectory returned {result.Error}.");
                Ensure(!string.IsNullOrWhiteSpace(result.Error!.Message), $"GetDirectory({directory}) must explain why it failed.");
                continue;
            }

            var path = result.Value;
            Ensure(!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path), $"GetDirectory({directory}) must return an absolute path, but returned '{path}'.");
            Ensure(Directory.Exists(path), $"GetDirectory({directory}) must create the directory '{path}'.");
            var again = GetDirectory(storage, directory);
            Ensure(again.IsSuccess && IsSameOrInside(again.Value!, path) && IsSameOrInside(path, again.Value!), $"GetDirectory({directory}) must return the same path every time.");
            await ProbeWritableAsync(directory, path, cancellationToken).ConfigureAwait(false);
            paths[directory] = path;
        }

        var undefined = GetDirectory(storage, (AppDirectory)(-1));
        Ensure(!undefined.IsSuccess, "An undefined AppDirectory value must return a failure, not a path.");

        if (paths.TryGetValue(AppDirectory.AppData, out var appData))
        {
            foreach (var purgeable in new[] { AppDirectory.Cache, AppDirectory.Temp })
            {
                Ensure(!paths.TryGetValue(purgeable, out var purgeablePath) || !IsSameOrInside(appData, purgeablePath),
                    $"AppData ('{appData}') must not be the {purgeable} directory or inside it: the OS or the app may purge {purgeable}.");
            }
        }
    }

    /// <summary>
    /// Verifies <see cref="IWorkflowCheckpointStore"/> semantics: missing workflows read as <see langword="null"/>; saving
    /// replaces and round-trips every field (including workflow ids with arbitrary characters); expired checkpoints are never
    /// returned, also when an expired checkpoint replaces a valid one; <see cref="IWorkflowCheckpointStore.GetPendingAsync"/>
    /// lists exactly the non-expired checkpoints, most recently saved first; completing removes a checkpoint and is idempotent.
    /// </summary>
    /// <param name="store">The store under test. Checkpoints of other workflows may exist; the contract only touches its own.</param>
    /// <param name="timeProvider">The clock the store evaluates expiry with; defaults to the system clock.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public static async Task VerifyWorkflowCheckpointStoreAsync(IWorkflowCheckpointStore store, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        Ensure(store.Capability == NativeCapabilities.WorkflowCheckpoints, $"Capability id must be '{NativeCapabilities.WorkflowCheckpoints}' but was '{store.Capability}'.");

        // Whole seconds, so stores that persist timestamps with less precision than DateTimeOffset still round-trip them.
        var clock = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var now = new DateTimeOffset(clock.UtcTicks - (clock.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
        var prefix = "bns-contract-" + Guid.NewGuid().ToString("N")[..8] + "-";
        var first = prefix + "first";
        var second = prefix + "second/with:odd*chars ✓";
        var expired = prefix + "expired";
        try
        {
            Ensure(await store.GetAsync(first, cancellationToken).ConfigureAwait(false) is null, "A missing workflow must read as null.");

            var original = new WorkflowCheckpoint { WorkflowId = first, StateJson = "{\"step\":1}", Route = "/first", Description = "First ✓", Version = 3, SavedAt = now.AddMinutes(-2) };
            await store.SaveAsync(original, cancellationToken).ConfigureAwait(false);
            var replacement = original with { StateJson = "{\"step\":2,\"text\":\"ünïcödé ✓\"}", SavedAt = now.AddMinutes(-1) };
            await store.SaveAsync(replacement, cancellationToken).ConfigureAwait(false);

            var read = await store.GetAsync(first, cancellationToken).ConfigureAwait(false);
            Ensure(read is not null, "A saved checkpoint must be returned by GetAsync.");
            Ensure(read!.WorkflowId == first && read.StateJson == replacement.StateJson, "SaveAsync must replace the previous checkpoint, and the state must round-trip exactly.");
            Ensure(read.Route == "/first" && read.Description == "First ✓" && read.Version == 3 && read.ExpiresAt is null, "Route, Description, Version and ExpiresAt must round-trip.");
            Ensure(read.SavedAt == replacement.SavedAt, "SavedAt must round-trip.");

            await store.SaveAsync(new WorkflowCheckpoint { WorkflowId = second, StateJson = "{}", SavedAt = now, ExpiresAt = now.AddHours(1) }, cancellationToken).ConfigureAwait(false);
            await store.SaveAsync(new WorkflowCheckpoint { WorkflowId = expired, StateJson = "{}", SavedAt = now, ExpiresAt = now.AddHours(-1) }, cancellationToken).ConfigureAwait(false);

            var secondRead = await store.GetAsync(second, cancellationToken).ConfigureAwait(false);
            Ensure(secondRead?.ExpiresAt == now.AddHours(1), "Workflow ids with arbitrary characters must be supported, and ExpiresAt must round-trip.");
            Ensure(await store.GetAsync(expired, cancellationToken).ConfigureAwait(false) is null, "An expired checkpoint must not be returned by GetAsync.");
            Ensure((await PendingAsync(store, prefix, cancellationToken).ConfigureAwait(false)).SequenceEqual([second, first]),
                "GetPendingAsync must list exactly the non-expired checkpoints, most recently saved first.");

            await store.CompleteAsync(first, cancellationToken).ConfigureAwait(false);
            await store.CompleteAsync(first, cancellationToken).ConfigureAwait(false);
            Ensure(await store.GetAsync(first, cancellationToken).ConfigureAwait(false) is null, "A completed checkpoint must not be returned by GetAsync.");
            Ensure((await PendingAsync(store, prefix, cancellationToken).ConfigureAwait(false)).SequenceEqual([second]), "A completed checkpoint must not be listed as pending.");

            await store.SaveAsync(new WorkflowCheckpoint { WorkflowId = second, StateJson = "{}", SavedAt = now, ExpiresAt = now.AddSeconds(-1) }, cancellationToken).ConfigureAwait(false);
            Ensure(await store.GetAsync(second, cancellationToken).ConfigureAwait(false) is null, "Saving an expired checkpoint must replace the previous one, which is then no longer returned.");
            Ensure((await PendingAsync(store, prefix, cancellationToken).ConfigureAwait(false)).Count == 0, "Expired checkpoints must not be listed as pending.");
        }
        finally
        {
            foreach (var id in new[] { first, second, expired })
            {
                await CleanupAsync(() => store.CompleteAsync(id, CancellationToken.None)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Verifies <see cref="IIpcService"/> semantics on a unique channel: connecting without a server is
    /// <see cref="NativeResultStatus.Unavailable"/>; a second server on a served channel fails with
    /// <see cref="NativeErrorCodes.Conflict"/>; a client is listed in <see cref="IIpcServer.Peers"/> as soon as it is connected;
    /// requests receive the handler's response; a throwing handler fails the request without leaking the exception message
    /// or closing the connection; one-way messages arrive in order with the sender's peer; broadcasts reach clients; stopping
    /// the server disconnects its clients (<see cref="IIpcConnection.Disconnected"/>, <see cref="IIpcConnection.IsConnected"/>,
    /// failing sends); and the channel can be served again afterwards. When the capability is unsupported, both entry points
    /// must return <see cref="NativeResultStatus.Unsupported"/>.
    /// </summary>
    public static async Task VerifyIpcAsync(IIpcService ipc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ipc);
        Ensure(ipc.Capability == NativeCapabilities.Ipc, $"Capability id must be '{NativeCapabilities.Ipc}' but was '{ipc.Capability}'.");
        var channel = "bns-contract-" + Guid.NewGuid().ToString("N")[..8];
        var received = new System.Collections.Concurrent.ConcurrentQueue<(IpcMessage Message, IpcPeer Peer)>();
        IpcMessageHandler handler = (message, peer, _) => message.Type switch
        {
            "contract.echo" => ValueTask.FromResult<IpcMessage?>(IpcMessage.Create("contract.echoed", message.Payload)),
            "contract.fail" => throw new InvalidOperationException("contract-secret-detail"),
            _ => Enqueue(received, message, peer),
        };

        if ((await ipc.GetStatusAsync(cancellationToken).ConfigureAwait(false)).IsUnsupported)
        {
            var unsupportedServer = await ipc.StartServerAsync(channel, handler, null, cancellationToken).ConfigureAwait(false);
            var unsupportedClient = await ipc.ConnectAsync(channel, null, TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            Ensure(unsupportedServer.Status == NativeResultStatus.Unsupported && unsupportedClient.Status == NativeResultStatus.Unsupported,
                "StartServerAsync and ConnectAsync must return Unsupported when the capability reports Unsupported.");
            return;
        }

        var missing = await ipc.ConnectAsync(channel, null, TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        if (missing.TryGetValue(out var unexpected))
        {
            await unexpected.DisposeAsync().ConfigureAwait(false);
        }

        Ensure(missing.Status == NativeResultStatus.Unavailable, $"Connecting to a channel without a server must return Unavailable, but returned {missing}.");

        var server = Require(await ipc.StartServerAsync(channel, handler, null, cancellationToken).ConfigureAwait(false), "StartServerAsync");
        IIpcConnection? client = null;
        try
        {
            Ensure(server.Channel == channel, "IIpcServer.Channel must be the requested channel.");
            var duplicate = await ipc.StartServerAsync(channel, handler, null, cancellationToken).ConfigureAwait(false);
            if (duplicate.TryGetValue(out var duplicateServer))
            {
                await duplicateServer.DisposeAsync().ConfigureAwait(false);
            }

            Ensure(duplicate.Error?.Code == NativeErrorCodes.Conflict, $"A second server on a served channel must fail with '{NativeErrorCodes.Conflict}', but returned {duplicate}.");

            client = Require(await ipc.ConnectAsync(channel, null, s_timeout, cancellationToken).ConfigureAwait(false), "ConnectAsync");
            Ensure(client.IsConnected && client.Channel == channel, "A new connection must be connected to the requested channel.");
            var peers = server.Peers;
            Ensure(peers.Count == 1 && !string.IsNullOrEmpty(peers[0].ConnectionId), "A connected client must be listed in Peers, with a connection id, as soon as ConnectAsync returns.");

            const string Payload = "{\"text\":\"ünïcödé ✓\"}";
            var response = Require(await client.RequestAsync(IpcMessage.Create("contract.echo", Payload), s_timeout, cancellationToken).ConfigureAwait(false), "RequestAsync");
            Ensure(response.Type == "contract.echoed" && response.Payload == Payload, "A request must receive the handler's response with its type and payload intact.");

            var failed = await client.RequestAsync(IpcMessage.Create("contract.fail"), s_timeout, cancellationToken).ConfigureAwait(false);
            Ensure(failed.Status == NativeResultStatus.Failed, $"A request whose handler throws must fail, but returned {failed}.");
            Ensure(!failed.Error!.Message.Contains("contract-secret-detail", StringComparison.Ordinal), "Handler exception messages must not be sent to the client.");
            Ensure(client.IsConnected, "A failing handler must not close the connection.");

            for (var i = 0; i < 3; i++)
            {
                Require(await client.SendAsync(IpcMessage.Create("contract.note", i.ToString(System.Globalization.CultureInfo.InvariantCulture)), cancellationToken).ConfigureAwait(false), "SendAsync");
            }

            await WaitUntilAsync(() => received.Count >= 3, "One-way messages must reach the server's handler.", cancellationToken).ConfigureAwait(false);
            Ensure(received.Select(static r => r.Message.Payload).SequenceEqual(["0", "1", "2"]), "One-way messages must arrive in order.");
            Ensure(received.All(r => r.Peer.ConnectionId == peers[0].ConnectionId), "The handler must receive the sending peer.");

            var pushed = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.MessageReceived += (_, message) => pushed.TrySetResult(message);
            Require(await server.BroadcastAsync(IpcMessage.Create("contract.push", "dark"), cancellationToken).ConfigureAwait(false), "BroadcastAsync");
            var push = await WithinAsync(pushed.Task, "Broadcast messages must reach connected clients.", cancellationToken).ConfigureAwait(false);
            Ensure(push.Type == "contract.push" && push.Payload == "dark", "Broadcast messages must keep their type and payload.");

            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Disconnected += (_, _) => disconnected.TrySetResult();
            await server.DisposeAsync().ConfigureAwait(false);
            await WithinAsync(disconnected.Task, "Clients must be notified through Disconnected when the server stops.", cancellationToken).ConfigureAwait(false);
            Ensure(!client.IsConnected, "IsConnected must be false after the server stopped.");
            Ensure(!(await client.SendAsync(IpcMessage.Create("contract.after"), cancellationToken).ConfigureAwait(false)).IsSuccess, "Sending on a closed connection must fail.");

            var reclaimed = Require(await ipc.StartServerAsync(channel, handler, null, cancellationToken).ConfigureAwait(false), "StartServerAsync(after stop)");
            await reclaimed.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (client is { } connection)
            {
                await CleanupAsync(() => connection.DisposeAsync()).ConfigureAwait(false);
            }

            await CleanupAsync(() => server.DisposeAsync()).ConfigureAwait(false);
        }

        static ValueTask<IpcMessage?> Enqueue(System.Collections.Concurrent.ConcurrentQueue<(IpcMessage, IpcPeer)> queue, IpcMessage message, IpcPeer peer)
        {
            queue.Enqueue((message, peer));
            return ValueTask.FromResult<IpcMessage?>(null);
        }
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

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

    private static NativeResult<string> GetDirectory(IAppStorage storage, AppDirectory directory)
    {
        try
        {
            return storage.GetDirectory(directory);
        }
        catch (Exception ex)
        {
            throw new ContractViolationException($"GetDirectory({directory}) must return a result instead of throwing {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    private static async Task ProbeWritableAsync(AppDirectory directory, string path, CancellationToken cancellationToken)
    {
        var probe = Path.Combine(path, ".bns-contract-" + Guid.NewGuid().ToString("N") + ".tmp");
        var content = Encoding.UTF8.GetBytes("bns contract ✓");
        try
        {
            var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Position = 0;
                var buffer = new byte[content.Length];
                await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
                Ensure(buffer.AsSpan().SequenceEqual(content), $"Files written to {directory} ('{path}') must read back unchanged.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ContractViolationException($"{directory} ('{path}') must be writable: {ex.Message}", ex);
        }
    }

    private static bool IsSameOrInside(string path, string directory)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var child = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return child.Equals(parent, comparison) || child.StartsWith(parent + Path.DirectorySeparatorChar, comparison);
    }

    private static async Task<List<string>> PendingAsync(IWorkflowCheckpointStore store, string prefix, CancellationToken cancellationToken) =>
        [.. (await store.GetPendingAsync(cancellationToken).ConfigureAwait(false)).Select(static c => c.WorkflowId).Where(id => id.StartsWith(prefix, StringComparison.Ordinal))];

    // Cleanup must not hide the violation that is being reported.
    private static async Task CleanupAsync(Func<ValueTask> cleanup)
    {
        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private static async Task WithinAsync(Task task, string message, CancellationToken cancellationToken)
    {
        try
        {
            await task.WaitAsync(s_timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new ContractViolationException(message, ex);
        }
    }

    private static async Task<T> WithinAsync<T>(Task<T> task, string message, CancellationToken cancellationToken)
    {
        try
        {
            return await task.WaitAsync(s_timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new ContractViolationException(message, ex);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string message, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(started) > s_timeout)
            {
                throw new ContractViolationException(message);
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
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
