using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using BlazorNativeSdk.Desktop.Internal;
using BlazorNativeSdk.Desktop.Ipc;
using BlazorNativeSdk.Desktop.SingleInstance;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk.Desktop;

/// <summary>
/// Single-instance enforcement for desktop apps. The first process to acquire the per-user lock becomes the primary
/// instance and serves an IPC channel; later processes forward their arguments to it and should exit.
/// </summary>
/// <remarks>
/// <para>
/// Ownership is an exclusive lock on a file in the app-data directory (see <see cref="DesktopNativeOptions.SingleInstanceLockDirectory"/>).
/// The OS drops the lock when the owning process exits for any reason, so a crashed primary never leaves an abandoned
/// lock behind. Named mutexes are not used: they are Windows-only, and an abandoned mutex must be detected and handled
/// explicitly, whereas the lock file behaves identically on every OS.
/// </para>
/// <para>
/// Forwarded arguments are published through <see cref="IActivationPublisher"/> (see <see cref="ActivationArguments"/>
/// for the mapping), so the primary receives deep links while already running through
/// <see cref="IAppActivationService"/>. The channel is always restricted to the current user.
/// </para>
/// </remarks>
public sealed partial class DesktopSingleInstanceService : ISingleInstanceService, IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan s_retryDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan s_maxConnectAttempt = TimeSpan.FromMilliseconds(500);

    private readonly IIpcService _ipc;
    private readonly IActivationPublisher _publisher;
    private readonly IAppStorage _appStorage;
    private readonly NativeSdkOptions _sdkOptions;
    private readonly DesktopNativeOptions _options;
    private readonly ILogger<DesktopSingleInstanceService> _logger;
    private readonly ConcurrentDictionary<SingleInstanceLease, byte> _leases = new(ReferenceEqualityComparer.Instance);
    private int _disposed;

    /// <summary>Creates the service.</summary>
    public DesktopSingleInstanceService(
        IIpcService ipc,
        IActivationPublisher publisher,
        IAppStorage appStorage,
        IOptions<NativeSdkOptions> sdkOptions,
        IOptions<DesktopNativeOptions> options,
        ILogger<DesktopSingleInstanceService> logger)
    {
        ArgumentNullException.ThrowIfNull(sdkOptions);
        ArgumentNullException.ThrowIfNull(options);
        _ipc = ipc;
        _publisher = publisher;
        _appStorage = appStorage;
        _sdkOptions = sdkOptions.Value;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.SingleInstance;

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(IpcTransport.IsSupported
            ? CapabilityStatus.Available(Capability, DesktopAdapter.Name, "argument-forwarding", "deep-links", "files")
            : CapabilityStatus.Unsupported(Capability, "Single-instance enforcement is implemented for Windows, Linux and macOS.", DesktopAdapter.Name));

    /// <inheritdoc />
    public async ValueTask<NativeResult<ISingleInstanceLease>> AcquireAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IpcTransport.IsSupported)
        {
            return NativeError.Unsupported(Capability, "Single-instance enforcement is implemented for Windows, Linux and macOS.");
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            return new NativeError(NativeResultStatus.Failed, NativeErrorCodes.HostNotReady, "The single-instance service has been disposed.") { Capability = Capability };
        }

        var channel = _options.SingleInstanceChannel;
        if (!IpcEndpoints.IsValidChannel(channel))
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "DesktopNativeOptions.SingleInstanceChannel is not a valid channel name.");
        }

        var lockPath = ResolveLockPath(channel);
        if (!lockPath.TryGetValue(out var path))
        {
            return lockPath.Error!;
        }

        var security = (_options.SingleInstanceSecurity ?? new IpcSecurityOptions()) with { CurrentUserOnly = true };
        var timeout = _options.SingleInstanceConnectTimeout > TimeSpan.Zero ? _options.SingleInstanceConnectTimeout : TimeSpan.FromSeconds(5);
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (ExclusiveFileLock.TryAcquire(path, out var fileLock, out var failure))
            {
                case FileLockOutcome.Acquired:
                    return await BecomePrimaryAsync(fileLock!, channel, security, arguments, cancellationToken).ConfigureAwait(false);
                case FileLockOutcome.Failed:
                    return NativeError.Failed(Capability, NativeErrorCodes.PlatformError,
                        $"The single-instance lock '{path}' could not be acquired: {failure?.Message}", failure);
            }

            // Another process holds the lock: it is the primary instance, possibly still starting its server.
            var remaining = timeout - stopwatch.Elapsed;
            var attempt = remaining < s_maxConnectAttempt ? remaining : s_maxConnectAttempt;
            var connection = await _ipc.ConnectAsync(channel, security, attempt > TimeSpan.Zero ? attempt : TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
            if (connection.TryGetValue(out var client))
            {
                await using (client.ConfigureAwait(false))
                {
                    var forwarded = await ForwardAsync(client, arguments, timeout - stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);
                    LogSecondary(_logger, forwarded);
                    return NativeResult.Success<ISingleInstanceLease>(SingleInstanceLease.Secondary(forwarded));
                }
            }

            if (connection.Error is { Status: not NativeResultStatus.Unavailable } || stopwatch.Elapsed >= timeout)
            {
                // Either the primary refused us (authentication, policy) or it never started listening.
                LogForwardFailed(_logger, connection.Error!.Message);
                return NativeResult.Success<ISingleInstanceLease>(SingleInstanceLease.Secondary(argumentsForwarded: false));
            }

            await Task.Delay(s_retryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Publishes this process's own launch arguments as cold-start activations (<see cref="AppActivation.IsColdStart"/> =
    /// <see langword="true"/>): deep links, files, or a <see cref="ActivationKind.Launch"/> activation. Called automatically
    /// by <see cref="AcquireAsync"/> for the primary instance when <see cref="DesktopNativeOptions.PublishLaunchActivation"/>
    /// is enabled.
    /// </summary>
    public async ValueTask PublishLaunchActivationAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        foreach (var activation in ActivationArguments.Parse(arguments, _sdkOptions.DeepLinks, ActivationKind.Launch, isColdStart: true, Environment.CurrentDirectory, _logger))
        {
            await _publisher.PublishAsync(activation, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Releases every primary lease still held by this service.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var lease in _leases.Keys.ToArray())
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Releases every primary lease still held by this service.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    internal async ValueTask<IpcMessage?> HandleForwardAsync(IpcMessage message, IpcPeer peer, CancellationToken cancellationToken)
    {
        if (message.Type != SingleInstanceProtocol.Forward || message.Payload is null)
        {
            return IpcMessage.Create(SingleInstanceProtocol.Invalid, "Unknown message.");
        }

        ForwardedLaunch? launch;
        try
        {
            launch = JsonSerializer.Deserialize(message.Payload, DesktopJsonContext.Default.ForwardedLaunch);
        }
        catch (JsonException)
        {
            launch = null;
        }

        var maxArguments = _options.MaxForwardedArguments > 0 ? _options.MaxForwardedArguments : 512;
        if (launch?.Arguments is not { } args || args.Count > maxArguments || args.Any(a => a is null))
        {
            LogInvalidForward(_logger, peer.ProcessId);
            return IpcMessage.Create(SingleInstanceProtocol.Invalid, "The forwarded launch is malformed or has too many arguments.");
        }

        var workingDirectory = launch.WorkingDirectory is { } wd && Path.IsPathFullyQualified(wd) ? wd : null;
        var activations = ActivationArguments.Parse(args, _sdkOptions.DeepLinks, ActivationKind.SecondaryInstance, isColdStart: false, workingDirectory, _logger);
        foreach (var activation in activations)
        {
            await _publisher.PublishAsync(activation, cancellationToken).ConfigureAwait(false);
        }

        LogForwardReceived(_logger, peer.ProcessId, args.Count, activations.Count);
        return IpcMessage.Create(SingleInstanceProtocol.Ack);
    }

    private async Task<NativeResult<ISingleInstanceLease>> BecomePrimaryAsync(
        ExclusiveFileLock fileLock,
        string channel,
        IpcSecurityOptions security,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        NativeResult<IIpcServer> server;
        try
        {
            server = await _ipc.StartServerAsync(channel, HandleForwardAsync, security, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            fileLock.Dispose();
            throw;
        }

        if (!server.TryGetValue(out var running))
        {
            fileLock.Dispose();
            LogPrimaryServerFailed(_logger, server.Error!.Message);
            return server.Error!;
        }

        var lease = SingleInstanceLease.Primary(fileLock, running, l => _leases.TryRemove(l, out _));
        _leases.TryAdd(lease, 0);
        LogPrimary(_logger, channel);
        if (_options.PublishLaunchActivation)
        {
            await PublishLaunchActivationAsync(arguments, cancellationToken).ConfigureAwait(false);
        }

        return NativeResult.Success<ISingleInstanceLease>(lease);
    }

    private async Task<bool> ForwardAsync(IIpcConnection connection, IReadOnlyList<string> arguments, TimeSpan remaining, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(
            new ForwardedLaunch { Arguments = [.. arguments.Where(a => a is not null)], WorkingDirectory = Environment.CurrentDirectory },
            DesktopJsonContext.Default.ForwardedLaunch);

        // Always leave the primary a reasonable time to answer, even if connecting used most of the budget.
        var timeout = remaining > TimeSpan.FromSeconds(2) ? remaining : TimeSpan.FromSeconds(2);
        var response = await connection.RequestAsync(IpcMessage.Create(SingleInstanceProtocol.Forward, payload), timeout, cancellationToken).ConfigureAwait(false);
        if (response.TryGetValue(out var reply) && reply.Type == SingleInstanceProtocol.Ack)
        {
            return true;
        }

        LogForwardFailed(_logger, response.IsSuccess ? reply!.Payload ?? reply.Type : response.Error!.Message);
        return false;
    }

    private NativeResult<string> ResolveLockPath(string channel)
    {
        string directory;
        if (!string.IsNullOrWhiteSpace(_options.SingleInstanceLockDirectory))
        {
            directory = Path.GetFullPath(_options.SingleInstanceLockDirectory);
        }
        else
        {
            var appData = _appStorage.GetDirectory(AppDirectory.AppData);
            if (!appData.TryGetValue(out var root))
            {
                return NativeError.Failed(Capability, NativeErrorCodes.HostNotReady,
                    "The app-data directory is unavailable; set DesktopNativeOptions.SingleInstanceLockDirectory. " + appData.Error!.Message);
            }

            directory = Path.Combine(root, "locks");
        }

        // The user key keeps the lock per-user even if the directory is shared.
        return Path.Combine(directory, IpcEndpoints.BuildName(_sdkOptions.AppId, channel, IpcEndpoints.UserKey, maxPrefixLength: 48) + ".lock");
    }

    [LoggerMessage(EventId = 5201, Level = LogLevel.Information, Message = "This process is the primary instance (single-instance channel '{Channel}').")]
    private static partial void LogPrimary(ILogger logger, string channel);

    [LoggerMessage(EventId = 5202, Level = LogLevel.Information, Message = "Another instance is running; arguments forwarded: {Forwarded}.")]
    private static partial void LogSecondary(ILogger logger, bool forwarded);

    [LoggerMessage(EventId = 5203, Level = LogLevel.Warning, Message = "Forwarding arguments to the primary instance failed: {Reason}")]
    private static partial void LogForwardFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 5204, Level = LogLevel.Error, Message = "The primary instance could not start its single-instance server: {Reason}")]
    private static partial void LogPrimaryServerFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 5205, Level = LogLevel.Debug, Message = "Received {ArgumentCount} forwarded argument(s) from pid {ProcessId}; published {ActivationCount} activation(s).")]
    private static partial void LogForwardReceived(ILogger logger, int? processId, int argumentCount, int activationCount);

    [LoggerMessage(EventId = 5206, Level = LogLevel.Warning, Message = "Ignored a malformed forwarded launch from pid {ProcessId}.")]
    private static partial void LogInvalidForward(ILogger logger, int? processId);
}
