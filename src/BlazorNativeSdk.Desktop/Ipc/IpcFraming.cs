using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;

namespace BlazorNativeSdk.Desktop.Ipc;

/// <summary>
/// Length-prefixed framing: a 4-byte little-endian payload length followed by the UTF-8 JSON envelope. The length is
/// validated against the limit before any payload memory is allocated, and pooled buffers are cleared on return.
/// </summary>
/// <remarks>
/// The announced length is not trusted for allocation either: the body is read into a buffer that starts at
/// <see cref="InitialReadBufferBytes"/> and doubles as data actually arrives, so a peer (authenticated or not) must send the
/// bytes it claims before the reader holds that much memory.
/// </remarks>
internal static class IpcFraming
{
    /// <summary>The size of the length prefix.</summary>
    public const int HeaderSize = 4;

    /// <summary>The initial size of the body buffer; it grows only as the peer sends data.</summary>
    internal const int InitialReadBufferBytes = 16 * 1024;

    /// <summary>The frame limit before the peer is authenticated (bounds pre-authentication memory use).</summary>
    public const int HandshakeMaxBytes = 4096;

    /// <summary>Serializes an envelope.</summary>
    public static byte[] Serialize(IpcEnvelope envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(envelope, DesktopJsonContext.Default.IpcEnvelope);

    /// <summary>Parses an envelope.</summary>
    /// <exception cref="IpcException">The payload is not a valid envelope.</exception>
    public static IpcEnvelope Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize(utf8Json, DesktopJsonContext.Default.IpcEnvelope);
            if (envelope is null || string.IsNullOrEmpty(envelope.Kind))
            {
                throw IpcException.Protocol("The frame does not contain a valid envelope.");
            }

            return envelope;
        }
        catch (JsonException ex)
        {
            throw IpcException.Protocol("The frame is not valid JSON.", ex);
        }
    }

    /// <summary>Writes one frame. The caller serializes concurrent writers.</summary>
    /// <exception cref="IpcException">The payload exceeds <paramref name="maxBytes"/>.</exception>
    public static async ValueTask WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, int maxBytes, CancellationToken cancellationToken)
    {
        if (payload.Length > maxBytes)
        {
            throw new IpcException(NativeResultStatus.Failed, NativeErrorCodes.TooLarge,
                $"The message is {payload.Length} bytes, which exceeds the limit of {maxBytes} bytes.");
        }

        var length = HeaderSize + payload.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer, payload.Length);
            payload.Span.CopyTo(buffer.AsSpan(HeaderSize));
            await stream.WriteAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    /// <summary>Reads one frame and parses it.</summary>
    /// <returns>The envelope, or <see langword="null"/> when the peer closed the stream at a frame boundary.</returns>
    /// <exception cref="IpcException">The frame is empty, too large or malformed.</exception>
    /// <exception cref="EndOfStreamException">The stream ended in the middle of a frame.</exception>
    public static async ValueTask<IpcEnvelope?> ReadFrameAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        var header = ArrayPool<byte>.Shared.Rent(HeaderSize);
        int length;
        try
        {
            if (!await TryReadHeaderAsync(stream, header.AsMemory(0, HeaderSize), cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            length = BinaryPrimitives.ReadInt32LittleEndian(header);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }

        // Validate before allocating: a hostile or buggy peer must not be able to make us reserve arbitrary memory.
        if (length <= 0)
        {
            throw IpcException.Protocol($"Invalid frame length {length}.");
        }

        if (length > maxBytes)
        {
            throw new IpcException(NativeResultStatus.Failed, NativeErrorCodes.TooLarge,
                $"The peer sent a {length}-byte frame, which exceeds the limit of {maxBytes} bytes.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(length, InitialReadBufferBytes));
        try
        {
            var received = 0;
            while (received < length)
            {
                if (received == buffer.Length)
                {
                    // The buffer is full of data the peer really sent: grow it (at most to the announced length).
                    var larger = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, (long)buffer.Length * 2));
                    buffer.AsSpan(0, received).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                    buffer = larger;
                }

                var window = buffer.AsMemory(received, Math.Min(buffer.Length, length) - received);
                var read = await stream.ReadAsync(window, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException($"The peer closed the connection after {received} of {length} frame bytes.");
                }

                received += read;
            }

            return Deserialize(buffer.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async ValueTask<bool> TryReadHeaderAsync(Stream stream, Memory<byte> header, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < header.Length)
        {
            var read = await stream.ReadAsync(header[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (total == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("The peer closed the connection in the middle of a frame header.");
            }

            total += read;
        }

        return true;
    }
}
