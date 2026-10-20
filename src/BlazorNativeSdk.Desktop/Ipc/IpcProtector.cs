using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace BlazorNativeSdk.Desktop.Ipc;

/// <summary>
/// Per-session message protection: strictly sequential per-direction sequence numbers and, for authenticated sessions,
/// an HMAC-SHA256 over the direction, the sequence number and every envelope field.
/// </summary>
/// <remarks>
/// <para>
/// MAC input (all integers little-endian): <c>"bns-ipc-v1/msg"</c> ‖ direction byte (<c>'C'</c> client→server,
/// <c>'S'</c> server→client) ‖ int64 sequence ‖ kind ‖ type ‖ payload ‖ correlationId, where each string is encoded as an
/// int32 UTF-8 byte length (−1 for <see langword="null"/>) followed by its bytes. The length prefixes make the encoding
/// injective, the direction byte stops reflection of a peer's own frames back at it, and requiring the next sequence number
/// exactly rejects replayed, reordered and dropped frames.
/// </para>
/// <para>
/// Thread safety: <see cref="Protect"/> must be serialized by the caller (the connection's write lock) and
/// <see cref="TryAccept"/> is only called by the connection's single reader.
/// </para>
/// </remarks>
internal sealed class IpcProtector : IDisposable
{
    internal const int MacSize = 32;
    private const byte ClientToServer = (byte)'C';
    private const byte ServerToClient = (byte)'S';
    private static readonly byte[] s_domain = "bns-ipc-v1/msg"u8.ToArray();

    private readonly byte[]? _key;
    private readonly byte _sendDirection;
    private readonly byte _receiveDirection;
    private long _sendSequence;
    private long _receiveSequence;

    /// <summary>Creates a protector.</summary>
    /// <param name="sessionKey">The per-session key, or <see langword="null"/> for an unauthenticated session (ordering checks only).</param>
    /// <param name="isServer">Whether this end is the server.</param>
    public IpcProtector(byte[]? sessionKey, bool isServer)
    {
        if (sessionKey is not null && sessionKey.Length < 16)
        {
            throw new ArgumentException("The session key is too short.", nameof(sessionKey));
        }

        _key = sessionKey;
        _sendDirection = isServer ? ServerToClient : ClientToServer;
        _receiveDirection = isServer ? ClientToServer : ServerToClient;
    }

    /// <summary>Whether frames carry a MAC.</summary>
    public bool IsAuthenticated => _key is not null;

    /// <summary>Assigns the next sequence number and, for authenticated sessions, the MAC.</summary>
    public void Protect(IpcEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        envelope.Seq = ++_sendSequence;
        envelope.Mac = null;
        if (_key is not null)
        {
            Span<byte> mac = stackalloc byte[MacSize];
            ComputeMac(_key, _sendDirection, envelope, mac);
            envelope.Mac = Convert.ToBase64String(mac);
        }
    }

    /// <summary>Verifies a received frame and advances the receive sequence.</summary>
    /// <returns><see langword="false"/> with a reason when the frame is forged, tampered, replayed or out of order.</returns>
    public bool TryAccept(IpcEnvelope envelope, [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (_key is null)
        {
            if (envelope.Mac is not null)
            {
                reason = "The frame carries a MAC on an unauthenticated session.";
                return false;
            }
        }
        else
        {
            Span<byte> received = stackalloc byte[MacSize];
            if (envelope.Mac is null ||
                !Convert.TryFromBase64String(envelope.Mac, received, out var written) ||
                written != MacSize)
            {
                reason = "The frame has no valid MAC.";
                return false;
            }

            Span<byte> expected = stackalloc byte[MacSize];
            ComputeMac(_key, _receiveDirection, envelope, expected);
            if (!CryptographicOperations.FixedTimeEquals(expected, received))
            {
                reason = "The frame MAC does not match (tampered, reflected or forged frame).";
                return false;
            }
        }

        if (envelope.Seq != _receiveSequence + 1)
        {
            reason = $"Unexpected sequence number {envelope.Seq} (expected {_receiveSequence + 1}): the frame was replayed, reordered or dropped.";
            return false;
        }

        _receiveSequence = envelope.Seq;
        reason = null;
        return true;
    }

    /// <summary>Clears the session key.</summary>
    public void Dispose()
    {
        if (_key is not null)
        {
            CryptographicOperations.ZeroMemory(_key);
        }
    }

    private static void ComputeMac(byte[] key, byte direction, IpcEnvelope envelope, Span<byte> destination)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        hmac.AppendData(s_domain);
        Span<byte> header = stackalloc byte[1 + sizeof(long)];
        header[0] = direction;
        BinaryPrimitives.WriteInt64LittleEndian(header[1..], envelope.Seq);
        hmac.AppendData(header);
        AppendField(hmac, envelope.Kind);
        AppendField(hmac, envelope.Type);
        AppendField(hmac, envelope.Payload);
        AppendField(hmac, envelope.CorrelationId);
        if (!hmac.TryGetHashAndReset(destination, out _))
        {
            throw new CryptographicException("The MAC could not be computed.");
        }
    }

    private static void AppendField(IncrementalHash hmac, string? value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        if (value is null)
        {
            BinaryPrimitives.WriteInt32LittleEndian(length, -1);
            hmac.AppendData(length);
            return;
        }

        var byteCount = Encoding.UTF8.GetByteCount(value);
        BinaryPrimitives.WriteInt32LittleEndian(length, byteCount);
        hmac.AppendData(length);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(byteCount, 1));
        try
        {
            var written = Encoding.UTF8.GetBytes(value, buffer);
            hmac.AppendData(buffer, 0, written);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
