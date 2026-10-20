using BlazorNativeSdk.Desktop.Internal;

namespace BlazorNativeSdk.Desktop.SingleInstance;

/// <summary>The request a secondary instance sends to the primary instance.</summary>
internal sealed class ForwardedLaunch
{
    /// <summary>The secondary instance's arguments.</summary>
    public List<string>? Arguments { get; set; }

    /// <summary>The secondary instance's working directory, used to resolve relative file paths.</summary>
    public string? WorkingDirectory { get; set; }
}

/// <summary>Single-instance wire constants.</summary>
internal static class SingleInstanceProtocol
{
    public const string Forward = "bns.single-instance.forward";
    public const string Ack = "bns.single-instance.ack";
    public const string Invalid = "bns.single-instance.invalid";
}

/// <summary>The lease returned by <see cref="DesktopSingleInstanceService.AcquireAsync"/>.</summary>
internal sealed class SingleInstanceLease : ISingleInstanceLease
{
    private readonly Action<SingleInstanceLease>? _onDisposed;
    private ExclusiveFileLock? _lock;
    private IIpcServer? _server;
    private int _disposed;

    private SingleInstanceLease(bool isPrimary, bool argumentsForwarded, ExclusiveFileLock? fileLock, IIpcServer? server, Action<SingleInstanceLease>? onDisposed)
    {
        IsPrimary = isPrimary;
        ArgumentsForwarded = argumentsForwarded;
        _lock = fileLock;
        _server = server;
        _onDisposed = onDisposed;
    }

    public bool IsPrimary { get; }

    public bool ArgumentsForwarded { get; }

    public static SingleInstanceLease Primary(ExclusiveFileLock fileLock, IIpcServer server, Action<SingleInstanceLease> onDisposed) =>
        new(isPrimary: true, argumentsForwarded: false, fileLock, server, onDisposed);

    public static SingleInstanceLease Secondary(bool argumentsForwarded) =>
        new(isPrimary: false, argumentsForwarded, fileLock: null, server: null, onDisposed: null);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Stop serving before releasing the lock: the next primary must never find the channel still taken.
        if (Interlocked.Exchange(ref _server, null) is { } server)
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }

        Interlocked.Exchange(ref _lock, null)?.Dispose();
        _onDisposed?.Invoke(this);
    }
}
