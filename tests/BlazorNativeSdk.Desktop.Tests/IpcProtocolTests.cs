using System.Buffers.Binary;
using System.Security.Cryptography;
using BlazorNativeSdk.Desktop.Ipc;

namespace BlazorNativeSdk.Desktop.Tests;

public sealed class IpcProtocolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (IpcProtector Client, IpcProtector Server) CreatePair(bool authenticated = true)
    {
        byte[]? key = authenticated ? RandomNumberGenerator.GetBytes(32) : null;
        return (new IpcProtector(key?.ToArray(), isServer: false), new IpcProtector(key?.ToArray(), isServer: true));
    }

    private static IpcEnvelope Request(string payload) =>
        new() { Kind = IpcKinds.Request, Type = "transfer", Payload = payload, CorrelationId = Guid.NewGuid().ToString("N") };

    [Fact]
    public void Protected_frames_are_accepted_in_order()
    {
        var (client, server) = CreatePair();
        var first = Request("1");
        var second = Request("2");
        client.Protect(first);
        client.Protect(second);

        Assert.Equal(1, first.Seq);
        Assert.Equal(2, second.Seq);
        Assert.NotNull(first.Mac);
        Assert.True(server.TryAccept(first, out _));
        Assert.True(server.TryAccept(second, out _));
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("type")]
    [InlineData("kind")]
    [InlineData("correlationId")]
    [InlineData("seq")]
    [InlineData("mac")]
    public void Tampered_frames_are_rejected(string field)
    {
        var (client, server) = CreatePair();
        var envelope = Request("{\"amount\":10}");
        client.Protect(envelope);

        switch (field)
        {
            case "payload": envelope.Payload = "{\"amount\":10000}"; break;
            case "type": envelope.Type = "delete-everything"; break;
            case "kind": envelope.Kind = IpcKinds.OneWay; break;
            case "correlationId": envelope.CorrelationId = "forged"; break;
            case "seq": envelope.Seq = 2; break;
            case "mac": envelope.Mac = Convert.ToBase64String(new byte[32]); break;
        }

        Assert.False(server.TryAccept(envelope, out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void Replayed_frames_are_rejected()
    {
        var (client, server) = CreatePair();
        var envelope = Request("once");
        client.Protect(envelope);

        Assert.True(server.TryAccept(envelope, out _));
        Assert.False(server.TryAccept(envelope, out var reason));
        Assert.Contains("replayed", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Reordered_or_dropped_frames_are_rejected()
    {
        var (client, server) = CreatePair();
        var first = Request("1");
        var second = Request("2");
        client.Protect(first);
        client.Protect(second);

        Assert.False(server.TryAccept(second, out _));
    }

    [Fact]
    public void Frames_reflected_back_to_their_sender_are_rejected()
    {
        var (_, server) = CreatePair();
        var push = new IpcEnvelope { Kind = IpcKinds.Push, Type = "notice", Payload = "x" };
        server.Protect(push);

        // A valid server→client frame fed back to the server must not verify (direction is part of the MAC).
        Assert.False(server.TryAccept(push, out _));
    }

    [Fact]
    public void Frames_from_another_session_are_rejected()
    {
        var (client, _) = CreatePair();
        var (_, otherServer) = CreatePair();
        var envelope = Request("x");
        client.Protect(envelope);

        Assert.False(otherServer.TryAccept(envelope, out _));
    }

    [Fact]
    public void Unauthenticated_sessions_still_enforce_ordering_and_reject_macs()
    {
        var (client, server) = CreatePair(authenticated: false);
        var envelope = Request("x");
        client.Protect(envelope);

        Assert.Null(envelope.Mac);
        Assert.True(server.TryAccept(envelope, out _));
        Assert.False(server.TryAccept(envelope, out _));

        var withMac = Request("y");
        client.Protect(withMac);
        withMac.Mac = Convert.ToBase64String(new byte[32]);
        Assert.False(server.TryAccept(withMac, out _));
    }

    [Fact]
    public async Task Frames_round_trip_through_a_stream()
    {
        using var stream = new MemoryStream();
        var envelope = Request("héllo \"world\" ☃");
        await IpcFraming.WriteFrameAsync(stream, IpcFraming.Serialize(envelope), 4096, Ct);
        stream.Position = 0;

        var read = await IpcFraming.ReadFrameAsync(stream, 4096, Ct);
        var end = await IpcFraming.ReadFrameAsync(stream, 4096, Ct);

        Assert.NotNull(read);
        Assert.Equal(envelope.Payload, read.Payload);
        Assert.Equal(envelope.CorrelationId, read.CorrelationId);
        Assert.Null(end);
    }

    [Fact]
    public async Task Oversized_length_prefixes_are_rejected_before_allocating()
    {
        // Only the 4-byte header exists: if the reader tried to allocate and read 1 GB it would hit end-of-stream instead.
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 1024 * 1024 * 1024);
        using var stream = new MemoryStream(header);

        var ex = await Assert.ThrowsAsync<IpcException>(async () => await IpcFraming.ReadFrameAsync(stream, 64 * 1024, Ct));

        Assert.Equal(NativeErrorCodes.TooLarge, ex.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Empty_or_negative_length_prefixes_are_protocol_errors(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);

        var ex = await Assert.ThrowsAsync<IpcException>(async () => await IpcFraming.ReadFrameAsync(stream, 1024, Ct));

        Assert.Equal(DesktopErrorCodes.IpcProtocolError, ex.Code);
    }

    [Fact]
    public async Task Malformed_json_and_truncated_frames_are_rejected()
    {
        var garbage = "not json"u8.ToArray();
        using var stream = new MemoryStream();
        await IpcFraming.WriteFrameAsync(stream, garbage, 1024, Ct);
        stream.Position = 0;
        await Assert.ThrowsAsync<IpcException>(async () => await IpcFraming.ReadFrameAsync(stream, 1024, Ct));

        var truncated = new byte[6];
        BinaryPrimitives.WriteInt32LittleEndian(truncated, 100);
        using var partial = new MemoryStream(truncated);
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await IpcFraming.ReadFrameAsync(partial, 1024, Ct));
    }

    [Fact]
    public async Task Announced_lengths_do_not_allocate_memory_the_peer_never_sends()
    {
        // A frame that claims 100 MB (under a 200 MB limit) but carries 10 bytes.
        var bytes = new byte[4 + 10];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 100 * 1024 * 1024);
        await using var stream = new ReadTrackingStream(bytes);

        await Assert.ThrowsAsync<EndOfStreamException>(async () => await IpcFraming.ReadFrameAsync(stream, 200 * 1024 * 1024, Ct));

        Assert.InRange(stream.LargestRead, 1, IpcFraming.InitialReadBufferBytes * 2);
    }

    [Fact]
    public async Task Large_frames_are_read_through_a_growing_buffer()
    {
        var envelope = Request(new string('x', 300_000));
        await using var buffer = new MemoryStream();
        await IpcFraming.WriteFrameAsync(buffer, IpcFraming.Serialize(envelope), 1024 * 1024, Ct);
        await using var stream = new ReadTrackingStream(buffer.ToArray(), maxChunk: 7_000);

        var read = await IpcFraming.ReadFrameAsync(stream, 1024 * 1024, Ct);

        Assert.Equal(envelope.Payload, read!.Payload);
        Assert.Equal(envelope.CorrelationId, read.CorrelationId);
    }

    [Fact]
    public async Task Writers_refuse_payloads_over_the_limit()
    {
        using var stream = new MemoryStream();

        var ex = await Assert.ThrowsAsync<IpcException>(async () => await IpcFraming.WriteFrameAsync(stream, new byte[2048], 1024, Ct));

        Assert.Equal(NativeErrorCodes.TooLarge, ex.Code);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void Handshake_proofs_are_bound_to_role_channel_and_nonces()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var ns = RandomNumberGenerator.GetBytes(32);
        var nc = RandomNumberGenerator.GetBytes(32);

        var client = IpcHandshake.ComputeProof(secret, "chan", ns, nc, "client");

        Assert.Equal(client, IpcHandshake.ComputeProof(secret, "chan", ns, nc, "client"));
        Assert.NotEqual(client, IpcHandshake.ComputeProof(secret, "chan", ns, nc, "server"));
        Assert.NotEqual(client, IpcHandshake.ComputeProof(secret, "other", ns, nc, "client"));
        Assert.NotEqual(client, IpcHandshake.ComputeProof(secret, "chan", nc, ns, "client"));
        Assert.Equal(IpcHandshake.DeriveSessionKey(secret, "chan", ns, nc), IpcHandshake.DeriveSessionKey(secret, "chan", ns, nc));
        Assert.NotEqual(IpcHandshake.DeriveSessionKey(secret, "chan", ns, nc), IpcHandshake.DeriveSessionKey(secret, "chan", ns, RandomNumberGenerator.GetBytes(32)));
    }

    [Fact]
    public void Endpoint_names_are_sanitized_bounded_and_unique()
    {
        var a = IpcEndpoints.GetPipeName("com.contoso/notes", "single instance", currentUserOnly: true);
        var b = IpcEndpoints.GetPipeName("com.contoso/notes", "single instance", currentUserOnly: false);
        var longName = IpcEndpoints.GetPipeName(new string('a', 500), new string('b', 128), currentUserOnly: true);
        var longOther = IpcEndpoints.GetPipeName(new string('a', 500), new string('b', 127) + "c", currentUserOnly: true);

        Assert.StartsWith("bns-com.contoso_notes.single_instance-", a, StringComparison.Ordinal);
        Assert.NotEqual(a, b);
        Assert.All(a, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'));
        Assert.True(longName.Length <= 4 + 64 + 1 + 16);
        Assert.NotEqual(longName, longOther);
        Assert.False(IpcEndpoints.IsValidChannel(new string('x', IpcEndpoints.MaxChannelLength + 1)));
    }

    [Fact]
    public void Unix_socket_paths_fit_in_sun_path()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Skip("Unix domain socket paths are only used on Linux and macOS.");
        }

        var path = IpcEndpoints.GetUnixSocketPath(new string('a', 200), new string('b', 128), currentUserOnly: true);

        Assert.NotNull(path);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(path) <= IpcEndpoints.MaxUnixSocketPathBytes);
    }
}

/// <summary>A read-only stream that serves data in bounded chunks and records the largest buffer a reader offered.</summary>
internal sealed class ReadTrackingStream(byte[] data, int maxChunk = int.MaxValue) : Stream
{
    private int _position;

    public int LargestRead { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => data.Length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        LargestRead = Math.Max(LargestRead, buffer.Length);
        var count = Math.Min(Math.Min(buffer.Length, maxChunk), data.Length - _position);
        data.AsSpan(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer.AsSpan(offset, count)));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
