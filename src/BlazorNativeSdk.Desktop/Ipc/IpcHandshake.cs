using System.Security.Cryptography;
using System.Text;

namespace BlazorNativeSdk.Desktop.Ipc;

/// <summary>
/// The connection handshake: protocol negotiation and, when a shared secret is configured, mutual HMAC-SHA256
/// challenge-response authentication followed by HKDF session-key derivation.
/// </summary>
/// <remarks>
/// <code>
/// S → C  hello   { type: scheme, payload: b64(Ns) }                       Ns, Nc: 32 random bytes each
/// C → S  auth    { type: scheme, payload: b64(Nc), mac: b64(proof_c) }    proof_r = HMAC(K, "bns-ipc-v1|channel|b64(Ns)|b64(Nc)|r")
/// S → C  welcome { type: scheme, mac: b64(proof_s) }                     (or an error frame, then close)
/// session key = HKDF-SHA256(ikm: K, salt: Ns ‖ Nc, info: "bns-ipc-v1|session|channel", 32 bytes)
/// </code>
/// Each side proves knowledge of K over fresh nonces chosen by both sides, so proofs cannot be replayed across
/// connections, and the proofs are role-bound so a server cannot be tricked into answering its own challenge. A side with
/// a secret refuses peers that do not authenticate (no downgrade). Without a secret, the same exchange runs with no proofs
/// to negotiate the protocol version and enforce <see cref="IpcSecurityOptions.HandshakeTimeout"/>.
/// </remarks>
internal static class IpcHandshake
{
    internal const string ProtocolName = "bns-ipc-v1";
    internal const string SchemeHmac = ProtocolName + ";auth=hmac-sha256";
    internal const string SchemeNone = ProtocolName + ";auth=none";
    internal const int NonceSize = 32;
    internal const int SessionKeySize = 32;

    /// <summary>
    /// Runs the server side up to (not including) the final <c>welcome</c> frame. The caller sends the returned welcome
    /// through <see cref="IpcLink.SendHandshakeAsync"/>, so the peer can be registered atomically with completing the handshake.
    /// </summary>
    public static async Task<(IpcProtector Protector, IpcEnvelope Welcome)> RunServerAsync(Stream stream, string channel, byte[]? secret, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var token = timeoutCts.Token;
        var scheme = secret is null ? SchemeNone : SchemeHmac;
        var serverNonce = RandomNumberGenerator.GetBytes(NonceSize);
        try
        {
            await WriteAsync(stream, new IpcEnvelope { Kind = IpcKinds.Hello, Type = scheme, Payload = Convert.ToBase64String(serverNonce) }, token).ConfigureAwait(false);

            var auth = await ReadAsync(stream, token).ConfigureAwait(false);
            if (auth.Kind != IpcKinds.Auth || !string.Equals(auth.Type, scheme, StringComparison.Ordinal))
            {
                await TrySendErrorAsync(stream, DesktopErrorCodes.IpcAuthenticationFailed, "The client does not use the server's authentication scheme.", token).ConfigureAwait(false);
                throw Authentication("The client does not use the server's authentication scheme (shared secret mismatch between the endpoints).");
            }

            var clientNonce = DecodeNonce(auth.Payload);
            byte[]? sessionKey = null;
            string? serverProof = null;
            if (secret is not null)
            {
                var expected = ComputeProof(secret, channel, serverNonce, clientNonce, "client");
                if (!TryDecodeProof(auth.Mac, out var received) || !CryptographicOperations.FixedTimeEquals(expected, received))
                {
                    await TrySendErrorAsync(stream, DesktopErrorCodes.IpcAuthenticationFailed, "Authentication failed.", token).ConfigureAwait(false);
                    throw Authentication("The client failed the shared-secret challenge.");
                }

                serverProof = Convert.ToBase64String(ComputeProof(secret, channel, serverNonce, clientNonce, "server"));
                sessionKey = DeriveSessionKey(secret, channel, serverNonce, clientNonce);
            }
            else if (auth.Mac is not null)
            {
                throw IpcException.Protocol("The client sent a proof on an unauthenticated channel.");
            }

            return (new IpcProtector(sessionKey, isServer: true), new IpcEnvelope { Kind = IpcKinds.Welcome, Type = scheme, Mac = serverProof });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IpcException(NativeResultStatus.Failed, NativeErrorCodes.Timeout, $"The IPC handshake did not complete within {timeout.TotalMilliseconds:0} ms.");
        }
    }

    /// <summary>Runs the client side. Returns the session protector.</summary>
    public static async Task<IpcProtector> RunClientAsync(Stream stream, string channel, byte[]? secret, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var token = timeoutCts.Token;
        try
        {
            var hello = await ReadAsync(stream, token).ConfigureAwait(false);
            ThrowIfError(hello);
            if (hello.Kind != IpcKinds.Hello || hello.Type is null || !hello.Type.StartsWith(ProtocolName + ";", StringComparison.Ordinal))
            {
                throw IpcException.Protocol("The server does not speak the BlazorNativeSdk IPC protocol.");
            }

            var serverRequiresAuth = hello.Type == SchemeHmac;
            if (!serverRequiresAuth && hello.Type != SchemeNone)
            {
                throw IpcException.Protocol($"Unsupported authentication scheme '{hello.Type}'.");
            }

            if (secret is not null && !serverRequiresAuth)
            {
                // Downgrade protection: a server that cannot prove knowledge of the secret might be an impostor.
                throw Authentication("A shared secret is configured but the server does not authenticate; refusing to connect.");
            }

            if (secret is null && serverRequiresAuth)
            {
                throw Authentication("The server requires a shared secret (IpcSecurityOptions.SharedSecret).");
            }

            var serverNonce = DecodeNonce(hello.Payload);
            var clientNonce = RandomNumberGenerator.GetBytes(NonceSize);
            var proof = secret is null ? null : Convert.ToBase64String(ComputeProof(secret, channel, serverNonce, clientNonce, "client"));
            await WriteAsync(stream, new IpcEnvelope { Kind = IpcKinds.Auth, Type = hello.Type, Payload = Convert.ToBase64String(clientNonce), Mac = proof }, token).ConfigureAwait(false);

            var welcome = await ReadAsync(stream, token).ConfigureAwait(false);
            ThrowIfError(welcome);
            if (welcome.Kind != IpcKinds.Welcome || welcome.Type != hello.Type)
            {
                throw IpcException.Protocol("The server did not complete the handshake.");
            }

            byte[]? sessionKey = null;
            if (secret is not null)
            {
                var expected = ComputeProof(secret, channel, serverNonce, clientNonce, "server");
                if (!TryDecodeProof(welcome.Mac, out var received) || !CryptographicOperations.FixedTimeEquals(expected, received))
                {
                    throw Authentication("The server failed the shared-secret challenge.");
                }

                sessionKey = DeriveSessionKey(secret, channel, serverNonce, clientNonce);
            }

            return new IpcProtector(sessionKey, isServer: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IpcException(NativeResultStatus.Failed, NativeErrorCodes.Timeout, $"The IPC handshake did not complete within {timeout.TotalMilliseconds:0} ms.");
        }
    }

    /// <summary>Sends an error frame (best effort) before the server closes a connection it refuses.</summary>
    public static async Task TrySendErrorAsync(Stream stream, string code, string message, CancellationToken cancellationToken)
    {
        try
        {
            await WriteAsync(stream, new IpcEnvelope { Kind = IpcKinds.Error, Type = code, Payload = message }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    internal static byte[] ComputeProof(byte[] secret, string channel, ReadOnlySpan<byte> serverNonce, ReadOnlySpan<byte> clientNonce, string role)
    {
        var transcript = Encoding.UTF8.GetBytes(
            $"{ProtocolName}|{channel}|{Convert.ToBase64String(serverNonce)}|{Convert.ToBase64String(clientNonce)}|{role}");
        return HMACSHA256.HashData(secret, transcript);
    }

    internal static byte[] DeriveSessionKey(byte[] secret, string channel, ReadOnlySpan<byte> serverNonce, ReadOnlySpan<byte> clientNonce)
    {
        Span<byte> salt = stackalloc byte[NonceSize * 2];
        serverNonce.CopyTo(salt);
        clientNonce.CopyTo(salt[NonceSize..]);
        var key = new byte[SessionKeySize];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, key, salt, Encoding.UTF8.GetBytes($"{ProtocolName}|session|{channel}"));
        return key;
    }

    private static async Task WriteAsync(Stream stream, IpcEnvelope envelope, CancellationToken cancellationToken) =>
        await IpcFraming.WriteFrameAsync(stream, IpcFraming.Serialize(envelope), IpcFraming.HandshakeMaxBytes, cancellationToken).ConfigureAwait(false);

    private static async Task<IpcEnvelope> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            return await IpcFraming.ReadFrameAsync(stream, IpcFraming.HandshakeMaxBytes, cancellationToken).ConfigureAwait(false)
                ?? throw new IpcException(NativeResultStatus.Failed, DesktopErrorCodes.IpcDisconnected, "The peer closed the connection during the handshake.");
        }
        catch (EndOfStreamException ex)
        {
            throw new IpcException(NativeResultStatus.Failed, DesktopErrorCodes.IpcDisconnected, "The peer closed the connection during the handshake.", ex);
        }
    }

    private static void ThrowIfError(IpcEnvelope envelope)
    {
        if (envelope.Kind != IpcKinds.Error)
        {
            return;
        }

        var code = envelope.Type is DesktopErrorCodes.IpcAuthenticationFailed or DesktopErrorCodes.IpcRejected
            ? envelope.Type
            : DesktopErrorCodes.IpcRejected;
        throw new IpcException(NativeResultStatus.Failed, code, "The server refused the connection: " + Truncate(envelope.Payload));
    }

    private static byte[] DecodeNonce(string? value)
    {
        var nonce = new byte[NonceSize];
        if (value is null || !Convert.TryFromBase64String(value, nonce, out var written) || written != NonceSize)
        {
            throw IpcException.Protocol("The handshake nonce is invalid.");
        }

        return nonce;
    }

    private static bool TryDecodeProof(string? value, out byte[] proof)
    {
        proof = new byte[IpcProtector.MacSize];
        return value is not null && Convert.TryFromBase64String(value, proof, out var written) && written == IpcProtector.MacSize;
    }

    private static IpcException Authentication(string message) =>
        new(NativeResultStatus.Failed, DesktopErrorCodes.IpcAuthenticationFailed, message);

    private static string Truncate(string? value) =>
        string.IsNullOrEmpty(value) ? "(no reason given)" : value.Length <= 256 ? value : value[..256];
}
