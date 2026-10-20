using System.Security.Cryptography;

namespace BlazorNativeSdk.Desktop.Ipc;

/// <summary>A validated, immutable snapshot of <see cref="IpcSecurityOptions"/> plus adapter limits.</summary>
internal sealed class IpcSettings : IDisposable
{
    internal const int MinSecretBytes = 16;
    internal const int MinMessageBytes = 256;
    internal const int MaxMessageBytesLimit = 256 * 1024 * 1024;

    private byte[]? _secret;

    private IpcSettings()
    {
    }

    public bool CurrentUserOnly { get; private init; }

    /// <summary>A private copy of the shared secret (zeroed on dispose), or <see langword="null"/>.</summary>
    public byte[]? Secret => _secret;

    public IReadOnlyList<string> AllowedClientExecutables { get; private init; } = [];

    public int MaxMessageBytes { get; private init; }

    public TimeSpan HandshakeTimeout { get; private init; }

    public TimeSpan WriteTimeout { get; private init; }

    public int MaxConnections { get; private init; }

    public int MaxConcurrentRequests { get; private init; }

    public static NativeResult<IpcSettings> Create(IpcSecurityOptions? security, DesktopNativeOptions options)
    {
        security ??= new IpcSecurityOptions();
        if (security.SharedSecret is { } secret && secret.Length < MinSecretBytes)
        {
            return Invalid($"SharedSecret must be at least {MinSecretBytes} bytes (got {secret.Length}). Use RandomNumberGenerator.GetBytes(32).");
        }

        if (!security.CurrentUserOnly && security.SharedSecret is null)
        {
            // Any local user can connect to a machine-wide endpoint, and can create (squat) it before the app does.
            return Invalid(
                "CurrentUserOnly = false requires a SharedSecret: a machine-wide channel is reachable by every local user, who could " +
                "also claim its endpoint name before the app starts, so peers must authenticate each other.");
        }

        if (security.MaxMessageBytes is < MinMessageBytes or > MaxMessageBytesLimit)
        {
            return Invalid($"MaxMessageBytes must be between {MinMessageBytes} and {MaxMessageBytesLimit}.");
        }

        if (security.HandshakeTimeout <= TimeSpan.Zero)
        {
            return Invalid("HandshakeTimeout must be positive.");
        }

        var allowed = new List<string>();
        foreach (var path in security.AllowedClientExecutables ?? [])
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            {
                return Invalid($"AllowedClientExecutables entries must be fully qualified paths ('{path}').");
            }

            allowed.Add(Path.GetFullPath(path));
        }

        return new IpcSettings
        {
            CurrentUserOnly = security.CurrentUserOnly,
            _secret = security.SharedSecret?.ToArray(),
            AllowedClientExecutables = allowed,
            MaxMessageBytes = security.MaxMessageBytes,
            HandshakeTimeout = security.HandshakeTimeout,
            WriteTimeout = options.IpcWriteTimeout > TimeSpan.Zero ? options.IpcWriteTimeout : TimeSpan.FromSeconds(10),
            MaxConnections = Math.Max(1, options.IpcMaxConnections),
            MaxConcurrentRequests = Math.Max(1, options.IpcMaxConcurrentRequestsPerConnection),
        };

        static NativeError Invalid(string message) =>
            NativeError.Failed(NativeCapabilities.Ipc, NativeErrorCodes.InvalidArgument, message);
    }

    public void Dispose()
    {
        var secret = Interlocked.Exchange(ref _secret, null);
        if (secret is not null)
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }
}
