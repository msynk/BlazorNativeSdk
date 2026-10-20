using System.Collections.Concurrent;
using BlazorNativeSdk.Desktop.Ipc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk.Desktop;

/// <summary>
/// Local, authenticated inter-process messaging: Windows named pipes, Unix domain sockets on Linux and macOS.
/// </summary>
/// <remarks>
/// <para>
/// Endpoints are derived from <see cref="NativeSdkOptions.AppId"/>, the channel name and (for
/// <see cref="IpcSecurityOptions.CurrentUserOnly"/> channels) the user identity, so apps and users never collide.
/// Frames are length-prefixed UTF-8 JSON with an enforced <see cref="IpcSecurityOptions.MaxMessageBytes"/> limit. With a
/// <see cref="IpcSecurityOptions.SharedSecret"/>, peers run a mutual HMAC-SHA256 challenge-response and every frame carries
/// a MAC and a sequence number. See <c>docs/adapters/desktop.md</c> for the threat model.
/// </para>
/// <para>
/// Servers and connections created by this service are disposed with it (for example when the DI container is disposed).
/// </para>
/// </remarks>
public sealed partial class DesktopIpcService : IIpcService, IAsyncDisposable, IDisposable
{
    private readonly NativeSdkOptions _sdkOptions;
    private readonly DesktopNativeOptions _options;
    private readonly ILogger<DesktopIpcService> _logger;
    private readonly ConcurrentDictionary<IAsyncDisposable, byte> _owned = new(ReferenceEqualityComparer.Instance);
    private int _disposed;

    /// <summary>Creates the service.</summary>
    public DesktopIpcService(IOptions<NativeSdkOptions> sdkOptions, IOptions<DesktopNativeOptions> options, ILogger<DesktopIpcService> logger)
    {
        ArgumentNullException.ThrowIfNull(sdkOptions);
        ArgumentNullException.ThrowIfNull(options);
        _sdkOptions = sdkOptions.Value;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.Ipc;

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!IpcTransport.IsSupported)
        {
            return ValueTask.FromResult(CapabilityStatus.Unsupported(Capability, "Local IPC is implemented for Windows, Linux and macOS.", DesktopAdapter.Name));
        }

        var features = new List<string> { "shared-secret", "peer-process-id", "broadcast" };
        features.Add(OperatingSystem.IsWindows() ? "named-pipes" : "unix-domain-sockets");
        if (OperatingSystem.IsWindows())
        {
            features.Add("client-executable-allowlist");
        }

        return ValueTask.FromResult(CapabilityStatus.Available(Capability, DesktopAdapter.Name, features));
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<IIpcServer>> StartServerAsync(string channel, IpcMessageHandler handler, IpcSecurityOptions? security = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        cancellationToken.ThrowIfCancellationRequested();
        if (Validate(channel) is { } invalid)
        {
            return ValueTask.FromResult<NativeResult<IIpcServer>>(invalid);
        }

        var settingsResult = IpcSettings.Create(security, _options);
        if (!settingsResult.TryGetValue(out var settings))
        {
            return ValueTask.FromResult(NativeResult.Failure<IIpcServer>(settingsResult.Error!));
        }

        if (settings.AllowedClientExecutables.Count > 0 && !OperatingSystem.IsWindows())
        {
            settings.Dispose();
            return ValueTask.FromResult<NativeResult<IIpcServer>>(NativeError.Unsupported(Capability,
                "AllowedClientExecutables can only be enforced on Windows, where the OS reports the client process of a named pipe. " +
                "Use a SharedSecret to authenticate clients on Linux and macOS."));
        }

        NativeResult<IIpcListener> listener;
        try
        {
            listener = IpcTransport.Listen(_sdkOptions.AppId, channel, settings.CurrentUserOnly);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            settings.Dispose();
            return ValueTask.FromResult<NativeResult<IIpcServer>>(NativeError.FromException(Capability, ex));
        }

        if (!listener.TryGetValue(out var transport))
        {
            settings.Dispose();
            LogServerStartFailed(_logger, channel, listener.Error!.Message);
            return ValueTask.FromResult(NativeResult.Failure<IIpcServer>(listener.Error!));
        }

        var server = new IpcServer(channel, transport, handler, settings, _logger, s => _owned.TryRemove(s, out _));
        _owned.TryAdd(server, 0);
        if (Volatile.Read(ref _disposed) != 0)
        {
            _ = server.DisposeAsync().AsTask();
            return ValueTask.FromResult<NativeResult<IIpcServer>>(new NativeError(NativeResultStatus.Failed, NativeErrorCodes.HostNotReady, "The IPC service has been disposed.") { Capability = Capability });
        }

        server.Start();
        LogServerStarted(_logger, channel);
        return ValueTask.FromResult(NativeResult.Success<IIpcServer>(server));
    }

    /// <inheritdoc />
    public async ValueTask<NativeResult<IIpcConnection>> ConnectAsync(string channel, IpcSecurityOptions? security = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Validate(channel) is { } invalid)
        {
            return invalid;
        }

        var connectTimeout = timeout ?? _options.IpcConnectTimeout;
        if (connectTimeout < TimeSpan.Zero)
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "The connect timeout must not be negative.");
        }

        var settingsResult = IpcSettings.Create(security, _options);
        if (!settingsResult.TryGetValue(out var settings))
        {
            return settingsResult.Error!;
        }

        IpcRawConnection? raw = null;
        try
        {
            raw = await IpcTransport.ConnectAsync(_sdkOptions.AppId, channel, settings.CurrentUserOnly, connectTimeout, cancellationToken).ConfigureAwait(false);
            var protector = await IpcHandshake.RunClientAsync(raw.Stream, channel, settings.Secret, settings.HandshakeTimeout, cancellationToken).ConfigureAwait(false);
            var link = new IpcLink(raw, protector, settings.MaxMessageBytes, settings.WriteTimeout);
            var connection = new IpcClientConnection(channel, link, settings, _options.IpcRequestTimeout, _logger, c => _owned.TryRemove(c, out _));
            _owned.TryAdd(connection, 0);
            connection.Start();
            return NativeResult.Success<IIpcConnection>(connection);
        }
        catch (IpcException ex)
        {
            await DisposeQuietlyAsync(raw).ConfigureAwait(false);
            settings.Dispose();
            LogConnectFailed(_logger, channel, ex.Message);
            return ex.ToError();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or System.Net.Sockets.SocketException)
        {
            await DisposeQuietlyAsync(raw).ConfigureAwait(false);
            settings.Dispose();
            return NativeError.Unavailable(Capability, NativeErrorCodes.NotFound, $"Could not connect to IPC channel '{channel}': {ex.Message}");
        }
        catch
        {
            await DisposeQuietlyAsync(raw).ConfigureAwait(false);
            settings.Dispose();
            throw;
        }
    }

    /// <summary>Stops every server and closes every connection created by this service.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var owned in _owned.Keys.ToArray())
        {
            await DisposeQuietlyAsync(owned).ConfigureAwait(false);
        }
    }

    /// <summary>Stops every server and closes every connection created by this service.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private NativeError? Validate(string channel)
    {
        if (!IpcTransport.IsSupported)
        {
            return NativeError.Unsupported(Capability, "Local IPC is implemented for Windows, Linux and macOS.");
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            return new NativeError(NativeResultStatus.Failed, NativeErrorCodes.HostNotReady, "The IPC service has been disposed.") { Capability = Capability };
        }

        return IpcEndpoints.IsValidChannel(channel)
            ? null
            : NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument,
                $"The channel name must be 1-{IpcEndpoints.MaxChannelLength} characters without control characters.");
    }

    private async Task DisposeQuietlyAsync(IAsyncDisposable? disposable)
    {
        if (disposable is null)
        {
            return;
        }

        try
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogDisposeFailed(_logger, ex);
        }
    }

    [LoggerMessage(EventId = 5131, Level = LogLevel.Debug, Message = "IPC server started on channel '{Channel}'.")]
    private static partial void LogServerStarted(ILogger logger, string channel);

    [LoggerMessage(EventId = 5132, Level = LogLevel.Information, Message = "IPC server for channel '{Channel}' could not start: {Reason}")]
    private static partial void LogServerStartFailed(ILogger logger, string channel, string reason);

    [LoggerMessage(EventId = 5133, Level = LogLevel.Debug, Message = "Connecting to IPC channel '{Channel}' failed: {Reason}")]
    private static partial void LogConnectFailed(ILogger logger, string channel, string reason);

    [LoggerMessage(EventId = 5134, Level = LogLevel.Warning, Message = "Disposing an IPC endpoint failed.")]
    private static partial void LogDisposeFailed(ILogger logger, Exception exception);
}
