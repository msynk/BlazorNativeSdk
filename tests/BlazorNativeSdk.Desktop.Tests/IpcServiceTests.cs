using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace BlazorNativeSdk.Desktop.Tests;

public sealed class IpcServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ValueTask<IpcMessage?> Echo(IpcMessage message, IpcPeer peer, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IpcMessage?>(IpcMessage.Create(message.Type + ".reply", message.Payload));

    private static async Task<IIpcServer> StartAsync(DesktopIpcService ipc, string channel, IpcMessageHandler handler, IpcSecurityOptions? security = null)
    {
        var result = await ipc.StartServerAsync(channel, handler, security, Ct);
        Assert.True(result.IsSuccess, result.ToString());
        return result.Value!;
    }

    private static async Task<IIpcConnection> ConnectAsync(DesktopIpcService ipc, string channel, IpcSecurityOptions? security = null)
    {
        var result = await ipc.ConnectAsync(channel, security, TimeSpan.FromSeconds(5), Ct);
        Assert.True(result.IsSuccess, result.ToString());
        return result.Value!;
    }

    [Fact]
    public async Task Request_receives_the_handler_response()
    {
        await using var ipc = TestSupport.CreateIpc(TestSupport.SdkOptions());
        var channel = TestSupport.NewChannel();
        await using var server = await StartAsync(ipc, channel, Echo);
        await using var client = await ConnectAsync(ipc, channel);

        var response = await client.RequestAsync(IpcMessage.Create("greet", "{\"name\":\"Ada\"}"), cancellationToken: Ct);

        Assert.True(response.IsSuccess, response.ToString());
        Assert.Equal("greet.reply", response.Value!.Type);
        Assert.Equal("{\"name\":\"Ada\"}", response.Value.Payload);
        Assert.True(client.IsConnected);
        Assert.Equal(channel, server.Channel);
    }

    [Fact]
    public async Task One_way_messages_reach_the_handler_in_order()
    {
        await using var ipc = TestSupport.CreateIpc(TestSupport.SdkOptions());
        var channel = TestSupport.NewChannel();
        var received = new ConcurrentQueue<string>();
        await using var server = await StartAsync(ipc, channel, (message, _, _) =>
        {
            received.Enqueue(message.Payload!);
            return ValueTask.FromResult<IpcMessage?>(null);
        });
        await using var client = await ConnectAsync(ipc, channel);

        for (var i = 0; i < 20; i++)
        {
            Assert.True((await client.SendAsync(IpcMessage.Create("tick", i.ToString(System.Globalization.CultureInfo.InvariantCulture)), Ct)).IsSuccess);
        }

        await TestSupport.WaitUntilAsync(() => received.Count == 20, Ct);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), received);
    }

    [Fact]
    public async Task Broadcast_pushes_to_every_connected_client()
    {
        await using var ipc = TestSupport.CreateIpc(TestSupport.SdkOptions());
        var channel = TestSupport.NewChannel();
        await using var server = await StartAsync(ipc, channel, Echo);
        await using var first = await ConnectAsync(ipc, channel);
        await using var second = await ConnectAsync(ipc, channel);
        var firstReceived = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReceived = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.MessageReceived += (_, m) => firstReceived.TrySetResult(m);
        second.MessageReceived += (_, m) => secondReceived.TrySetResult(m);

        // A client is a peer as soon as ConnectAsync returns.
        Assert.Equal(2, server.Peers.Count);
        Assert.All(server.Peers, p => Assert.Equal(Environment.ProcessId, p.ProcessId));

        var result = await server.BroadcastAsync(IpcMessage.Create("theme-changed", "dark"), Ct);

        Assert.True(result.IsSuccess, result.ToString());
        var a = await firstReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var b = await secondReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(("theme-changed", "dark"), (a.Type, a.Payload));
        Assert.Equal(("theme-changed", "dark"), (b.Type, b.Payload));
    }

    [Fact]
    public async Task Concurrent_requests_are_correlated_with_their_responses()
    {
        await using var ipc = TestSupport.CreateIpc(TestSupport.SdkOptions());
        var channel = TestSupport.NewChannel();
        await using var server = await StartAsync(ipc, channel, async (message, _, ct) =>
        {
            // Finish out of order to prove correlation does not depend on ordering.
            await Task.Delay(Random.Shared.Next(0, 20), ct);
            return IpcMessage.Create("square", (int.Parse(message.Payload!, System.Globalization.CultureInfo.InvariantCulture) * 2).ToString(System.Globalization.CultureInfo.InvariantCulture));
        });
        await using var client = await ConnectAsync(ipc, channel);

        var requests = Enumerable.Range(0, 64).Select(async i =>
        {
            var response = await client.RequestAsync(IpcMessage.Create("double", i.ToString(System.Globalization.CultureInfo.InvariantCulture)), cancellationToken: Ct);
            Assert.True(response.IsSuccess, response.ToString());
            return (i, int.Parse(response.Value!.Payload!, System.Globalization.CultureInfo.InvariantCulture));
        });

        foreach (var (input, output) in await Task.WhenAll(requests))
        {
            Assert.Equal(input * 2, output);
        }
    }

    [Fact]
    public async Task Oversized_messages_are_rejected_before_sending()
    {
        await using var ipc = TestSupport.CreateIpc(TestSupport.SdkOptions());
        var channel = TestSupport.NewChannel();
        var security = new IpcSecurityOptions { MaxMessageBytes = 1024 };
        var calls = 0;
        await using var server = await StartAsync(ipc, channel, (m, p, c) =>
        {
            Interlocked.Increment(ref calls);
            return Echo(m, p, c);
        }, security);
        await using var client = await ConnectAsync(ipc, channel, security);

        var tooLarge = await client.RequestAsync(IpcMessage.Create("big", new string('x', 2048)), cancellationToken: Ct);
        var send = await client.SendAsync(IpcMessage.Create("big", new string('x', 2048)), Ct);

        Assert.Equal(NativeErrorCodes.TooLarge, tooLarge.Error!.Code);
        Assert.Equal(NativeErrorCodes.TooLarge, send.Error!.Code);
        Assert.Equal(0, Volatile.Read(ref calls));

        // The connection is still usable.
        Assert.True((await client.RequestAsync(IpcMessage.Create("small", "ok"), cancellationToken: Ct)).IsSuccess);
    }

    [Fact]
    public async Task A_server_drops_clients_that_send_frames_over_its_limit()
    {
        await using var ipc = TestSupport.CreateIpc(TestSupport.SdkOptions());
        var channel = TestSupport.NewChannel();
        await using var server = await StartAsync(ipc, channel, Echo, new IpcSecurityOptions { MaxMessageBytes = 1024 });
        await using var client = await ConnectAsync(ipc, channel, new IpcSecurityOptions { MaxMessageBytes = 64 * 1024 });
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += (_, _) => disconnected.TrySetResult();

        var response = await client.RequestAsync(IpcMessage.Create("big", new string('x', 8 * 1024)), TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(NativeResultStatus.Failed, response.Status);
        Assert.Equal(DesktopErrorCodes.IpcDisconnected, response.Error!.Code);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Matching_shared_secrets_authenticate_and_protect_messages()
    {
        await using var ipc = TestSupport.CreateIpc(TestSupport.SdkOptions());
        var channel = TestSupport.NewChannel();
        var security = new IpcSecurityOptions { SharedSecret = RandomNumberGenerator.GetBytes(32) };
        await using var server = await StartAsync(ipc, channel, Echo, security);
        await using var client = await ConnectAsync(ipc, channel, security);

        for (var i = 0; i < 5; i++)
        {
            var response = await client.RequestAsync(IpcMessage.Create("ping", "n" + i), cancellationToken: Ct);
            Assert.True(response.IsSuccess, response.ToString());
            Assert.Equal("n" + i, response.Value!.Payload);
        }

        Assert.True((await server.BroadcastAsync(IpcMessage.Create("push"), Ct)).IsSuccess);
    }

    [Fact]
    public async Task Mismatched_or_missing_shared_secrets_are_rejected()
    {
        await using var ipc = TestSupport.CreateIpc(TestSupport.SdkOptions());
        var channel = TestSupport.NewChannel();
        await using var server = await StartAsync(ipc, channel, Echo, new IpcSecurityOptions { SharedSecret = RandomNumberGenerator.GetBytes(32) });

        var wrongSecret = await ipc.ConnectAsync(channel, new IpcSecurityOptions { SharedSecret = RandomNumberGenerator.GetBytes(32) }, TimeSpan.FromSeconds(5), Ct);
        var noSecret = await ipc.ConnectAsync(channel, new IpcSecurityOptions(), TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(DesktopErrorCodes.IpcAuthenticationFailed, wrongSecret.Error!.Code);
        Assert.Equal(DesktopErrorCodes.IpcAuthenticationFailed, noSecret.Error!.Code);
        Assert.Empty(server.Peers);
    }

    [Fact]
    public async Task A_client_with_a_secret_refuses_a_server_without_one()
    {
        await using var ipc = TestSupport.CreateIpc(TestSupport.SdkOptions());
        var channel = TestSupport.NewChannel();
        await using var server = await StartAsync(ipc, channel, Echo);

        var result = await ipc.ConnectAsync(channel, new IpcSecurityOptions { SharedSecret = RandomNumberGenerator.GetBytes(32) }, TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(DesktopErrorCodes.IpcAuthenticationFailed, result.Error!.Code);
    }

    [Fact]
    public async Task Short_shared_secrets_are_invalid_arguments()
    {
        await using var ipc = TestSupport.CreateIpc(TestSupport.SdkOptions());
        var security = new IpcSecurityOptions { SharedSecret = new byte[15] };

        var server = await ipc.StartServerAsync(TestSupport.NewChannel(), Echo, security, Ct);
        var client = await ipc.ConnectAsync(TestSupport.NewChannel(), security, TimeSpan.FromSeconds(1), Ct);

        Assert.Equal(NativeErrorCodes.InvalidArgument, server.Error!.Code);
        Assert.Equal(NativeErrorCodes.InvalidArgument, client.Error!.Code);
    }

    [Fact]
    public async Task Machine_wide_channels_require_a_shared_secret()
    {
        await using var ipc = TestSupport.CreateIpc(TestSupport.SdkOptions());
        var unauthenticated = new IpcSecurityOptions { CurrentUserOnly = false };

        var server = await ipc.StartServerAsync(TestSupport.NewChannel(), Echo, unauthenticated, Ct);
        var client = await ipc.ConnectAsync(TestSupport.NewChannel(), unauthenticated, TimeSpan.FromSeconds(1), Ct);

        Assert.Equal(NativeErrorCodes.InvalidArgument, server.Error!.Code);
        Assert.Contains("SharedSecret", server.Error.Message, StringComparison.Ordinal);
        Assert.Equal(NativeErrorCodes.InvalidArgument, client.Error!.Code);

        var authenticated = Ipc.IpcSettings.Create(unauthenticated with { SharedSecret = RandomNumberGenerator.GetBytes(32) }, new DesktopNativeOptions());
        Assert.True(authenticated.IsSuccess, authenticated.Error?.ToString());
        authenticated.Value!.Dispose();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nchannel")]
    public async Task Invalid_channel_names_are_rejected(string channel)
    {
        await using var ipc = TestSupport.CreateIpc(TestSupport.SdkOptions());

        var result = await ipc.StartServerAsync(channel, Echo, cancellationToken: Ct);

        Assert.Equal(NativeErrorCodes.InvalidArgument, result.Error!.Code);
    }

    [Fact]
    public async Task A_second_server_on_the_same_channel_reports_a_conflict()
    {
        var sdk = TestSupport.SdkOptions();
        await using var ipc = TestSupport.CreateIpc(sdk);
        await using var other = TestSupport.CreateIpc(sdk);
        var channel = TestSupport.NewChannel();
        await using var server = await StartAsync(ipc, channel, Echo);

        var second = await other.StartServerAsync(channel, Echo, cancellationToken: Ct);

        Assert.Equal(NativeResultStatus.Failed, second.Status);
        Assert.Equal(NativeErrorCodes.Conflict, second.Error!.Code);

        // The original server keeps working, and the channel can be reclaimed after it stops.
        await using (var client = await ConnectAsync(ipc, channel))
        {
            Assert.True((await client.RequestAsync(IpcMessage.Create("still-here"), cancellationToken: Ct)).IsSuccess);
        }

        await server.DisposeAsync();
        var reclaimed = await other.StartServerAsync(channel, Echo, cancellationToken: Ct);
        Assert.True(reclaimed.IsSuccess, reclaimed.ToString());
        await reclaimed.Value!.DisposeAsync();
    }

    [Fact]
    public async Task Connecting_without_a_server_is_unavailable()
    {
        await using var ipc = TestSupport.CreateIpc(TestSupport.SdkOptions());

        var result = await ipc.ConnectAsync(TestSupport.NewChannel(), timeout: TimeSpan.FromMilliseconds(200), cancellationToken: Ct);

        Assert.Equal(NativeResultStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task Requests_time_out_when_the_handler_does_not_answer()
    {
        await using var ipc = TestSupport.CreateIpc(TestSupport.SdkOptions());
        var channel = TestSupport.NewChannel();
        await using var server = await StartAsync(ipc, channel, async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        });
        await using var client = await ConnectAsync(ipc, channel);

        var result = await client.RequestAsync(IpcMessage.Create("slow"), TimeSpan.FromMilliseconds(200), Ct);

        Assert.Equal(NativeResultStatus.Failed, result.Status);
        Assert.Equal(NativeErrorCodes.Timeout, result.Error!.Code);
    }

    [Fact]
    public async Task Handler_exceptions_fail_the_request_without_closing_the_connection()
    {
        await using var ipc = TestSupport.CreateIpc(TestSupport.SdkOptions());
        var channel = TestSupport.NewChannel();
        await using var server = await StartAsync(ipc, channel, (message, peer, ct) =>
            message.Type == "boom" ? throw new InvalidOperationException("secret internal detail") : Echo(message, peer, ct));
        await using var client = await ConnectAsync(ipc, channel);

        var failed = await client.RequestAsync(IpcMessage.Create("boom"), cancellationToken: Ct);
        var next = await client.RequestAsync(IpcMessage.Create("fine"), cancellationToken: Ct);

        Assert.Equal(NativeResultStatus.Failed, failed.Status);
        Assert.Equal(DesktopErrorCodes.IpcHandlerFailed, failed.Error!.Code);
        Assert.DoesNotContain("secret internal detail", failed.Error.Message, StringComparison.Ordinal);
        Assert.True(next.IsSuccess, next.ToString());
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Pending_requests_fail_and_clients_are_notified_when_the_server_stops()
    {
        await using var ipc = TestSupport.CreateIpc(TestSupport.SdkOptions());
        var channel = TestSupport.NewChannel();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = await StartAsync(ipc, channel, async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        });
        await using var client = await ConnectAsync(ipc, channel);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += (_, _) => disconnected.TrySetResult();

        var pending = client.RequestAsync(IpcMessage.Create("wait"), TimeSpan.FromSeconds(30), Ct).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await server.DisposeAsync();

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(DesktopErrorCodes.IpcDisconnected, result.Error!.Code);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.False(client.IsConnected);
        Assert.Equal(DesktopErrorCodes.IpcDisconnected, (await client.SendAsync(IpcMessage.Create("after"), Ct)).Error!.Code);
    }

    [Fact]
    public async Task Disposing_the_service_stops_its_servers()
    {
        var sdk = TestSupport.SdkOptions();
        var channel = TestSupport.NewChannel();
        var ipc = TestSupport.CreateIpc(sdk);
        _ = await StartAsync(ipc, channel, Echo);

        ipc.Dispose();
        ipc.Dispose();

        await using var other = TestSupport.CreateIpc(sdk);
        var reclaimed = await other.StartServerAsync(channel, Echo, cancellationToken: Ct);
        Assert.True(reclaimed.IsSuccess, reclaimed.ToString());
        await reclaimed.Value!.DisposeAsync();
    }

    [Fact]
    public async Task Allowed_client_executables_are_enforced_on_windows_and_unsupported_elsewhere()
    {
        await using var ipc = TestSupport.CreateIpc(TestSupport.SdkOptions());
        var self = Environment.ProcessPath!;
        if (!OperatingSystem.IsWindows())
        {
            var unsupported = await ipc.StartServerAsync(TestSupport.NewChannel(), Echo, new IpcSecurityOptions { AllowedClientExecutables = [self] }, Ct);
            Assert.Equal(NativeResultStatus.Unsupported, unsupported.Status);
            return;
        }

        var allowedChannel = TestSupport.NewChannel();
        await using (var allowed = await StartAsync(ipc, allowedChannel, Echo, new IpcSecurityOptions { AllowedClientExecutables = [self.ToUpperInvariant()] }))
        await using (var client = await ConnectAsync(ipc, allowedChannel))
        {
            Assert.True((await client.RequestAsync(IpcMessage.Create("hi"), cancellationToken: Ct)).IsSuccess);
            Assert.Equal(Path.GetFullPath(self), allowed.Peers.Single().ProcessPath, ignoreCase: true);
        }

        var deniedChannel = TestSupport.NewChannel();
        await using var denied = await StartAsync(ipc, deniedChannel, Echo, new IpcSecurityOptions { AllowedClientExecutables = [@"C:\definitely\not\this.exe"] });
        var rejected = await ipc.ConnectAsync(deniedChannel, timeout: TimeSpan.FromSeconds(5), cancellationToken: Ct);
        Assert.Equal(DesktopErrorCodes.IpcRejected, rejected.Error!.Code);
    }

    [Fact]
    public void Peer_paths_are_bound_to_processes_that_existed_when_the_connection_was_accepted()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Named pipe peers are identified by process id on Windows only.");
            return;
        }

        var accepted = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(Interop.WindowsInterop.StartedBy(accepted.AddSeconds(-1).ToFileTimeUtc(), accepted));
        Assert.True(Interop.WindowsInterop.StartedBy(accepted.ToFileTimeUtc(), accepted));
        Assert.False(Interop.WindowsInterop.StartedBy(accepted.AddTicks(1).ToFileTimeUtc(), accepted));
        Assert.False(Interop.WindowsInterop.StartedBy(0, accepted));

        // The current process stands in for a peer: started before "now", but after a connection observed earlier than its
        // start (which is what a reused process id looks like).
        var self = (uint)Environment.ProcessId;
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        Assert.Equal(Environment.ProcessPath, Interop.WindowsInterop.TryGetProcessImagePath(self, DateTime.UtcNow), ignoreCase: true);
        Assert.Null(Interop.WindowsInterop.TryGetProcessImagePath(self, current.StartTime.ToUniversalTime().AddSeconds(-5)));
    }

    [Fact]
    public async Task Status_reports_available_on_desktop_platforms()
    {
        await using var ipc = TestSupport.CreateIpc(TestSupport.SdkOptions());

        var status = await ipc.GetStatusAsync(Ct);

        Assert.True(status.IsAvailable);
        Assert.Equal("desktop", status.Adapter);
        Assert.True(status.Supports("shared-secret"));
    }
}
