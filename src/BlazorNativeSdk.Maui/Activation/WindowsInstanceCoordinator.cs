#if WINDOWS
using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;
using BlazorNativeSdk.Maui.Interop;
using BlazorNativeSdk.WindowsPlatform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Windows.AppLifecycle;
using WinActivation = Windows.ApplicationModel.Activation;

namespace BlazorNativeSdk.Maui;

/// <summary>
/// Windows launch handling for MAUI apps: single-instance enforcement, the launch activation and forwarded activations, and
/// optional runtime protocol registration.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Unpackaged</b>: the Desktop <see cref="ISingleInstanceService"/> (per-user lock file + authenticated named pipe).
/// A second instance forwards its command line and exits; the primary publishes the forwarded arguments (deep links, files)
/// and, by default, its own launch arguments.</item>
/// <item><b>Packaged (MSIX)</b>: Windows App SDK <see cref="AppInstance"/> keyed redirection. Protocol and file activations
/// do not arrive on the command line in packaged apps, so they are read from <see cref="AppActivationArguments"/> both at
/// launch and when another instance redirects to this one.</item>
/// </list>
/// A secondary instance terminates immediately after handing over its activation, before MAUI creates a window.
/// </remarks>
internal sealed partial class WindowsInstanceCoordinator : IDisposable, IAsyncDisposable
{
    /// <summary>The <see cref="AppActivation.Source"/> of activations read from Windows App SDK activation arguments.</summary>
    public const string AppInstanceSource = "maui.windows.app-instance";

    private const uint RedirectTimeoutMilliseconds = 10_000;

    private readonly ISingleInstanceService _singleInstance;
    private readonly IProtocolRegistrationService _protocols;
    private readonly IActivationPublisher _publisher;
    private readonly IOptions<NativeSdkOptions> _sdkOptions;
    private readonly IOptions<DesktopNativeOptions> _desktopOptions;
    private readonly IOptions<WindowsNativeOptions> _windowsOptions;
    private readonly IOptions<MauiNativeOptions> _mauiOptions;
    private readonly ILogger<WindowsInstanceCoordinator> _logger;
    private ISingleInstanceLease? _lease;
    private AppInstance? _appInstance;
    private int _initialized;
    private int _disposed;

    /// <summary>Creates the coordinator.</summary>
    public WindowsInstanceCoordinator(
        ISingleInstanceService singleInstance,
        IProtocolRegistrationService protocols,
        IActivationPublisher publisher,
        IOptions<NativeSdkOptions> sdkOptions,
        IOptions<DesktopNativeOptions> desktopOptions,
        IOptions<WindowsNativeOptions> windowsOptions,
        IOptions<MauiNativeOptions> mauiOptions,
        ILogger<WindowsInstanceCoordinator> logger)
    {
        _singleInstance = singleInstance;
        _protocols = protocols;
        _publisher = publisher;
        _sdkOptions = sdkOptions;
        _desktopOptions = desktopOptions;
        _windowsOptions = windowsOptions;
        _mauiOptions = mauiOptions;
        _logger = logger;
    }

    /// <summary>
    /// Enforces a single instance (a secondary instance hands over its activation and the process ends here) and publishes
    /// the launch activation. Runs once, synchronously, while the MAUI app is built and before any window exists.
    /// </summary>
    public void InitializeLaunch()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return;
        }

        var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (Win32Process.IsPackaged)
        {
            InitializePackaged(arguments);
        }
        else
        {
            InitializeUnpackaged(arguments);
        }
    }

    /// <summary>Starts work that must not delay the launch (runtime protocol registration).</summary>
    public void StartBackgroundWork()
    {
        if (!_mauiOptions.Value.RegisterProtocolSchemes)
        {
            return;
        }

        if (Win32Process.IsPackaged)
        {
            LogProtocolsFromManifest(_logger);
            return;
        }

        _ = Task.Run(RegisterProtocolSchemesAsync);
    }

    /// <inheritdoc />
    /// <remarks>Releases the single-instance lease on the thread pool, so disposing from the UI thread cannot deadlock.</remarks>
    public void Dispose() => Task.Run(() => DisposeAsync().AsTask()).GetAwaiter().GetResult();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _appInstance, null) is { } instance)
        {
            instance.Activated -= OnRedirectedActivation;
        }

        if (Interlocked.Exchange(ref _lease, null) is { } lease)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void InitializeUnpackaged(string[] arguments)
    {
        if (_mauiOptions.Value.SingleInstance)
        {
            NativeResult<ISingleInstanceLease> acquired;
            try
            {
                // The lock and the IPC handshake take milliseconds. AcquireAsync never needs the UI thread, so blocking the
                // launch on the thread pool cannot deadlock, and a secondary instance exits before any window appears.
                acquired = Task.Run(() => _singleInstance.AcquireAsync(arguments).AsTask()).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                acquired = NativeError.FromException(NativeCapabilities.SingleInstance, ex);
            }

            if (acquired.TryGetValue(out var lease))
            {
                if (!lease.IsPrimary)
                {
                    LogSecondaryInstance(_logger, lease.ArgumentsForwarded);

                    // The primary brings its window to the front for the forwarded activation; this process received the
                    // foreground right from the shell and hands it over before exiting.
                    NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
                    Win32Process.TerminateCurrentProcess();
                    return;
                }

                _lease = lease;
                if (!_desktopOptions.Value.PublishLaunchActivation)
                {
                    PublishCommandLine(arguments);
                }

                return;
            }

            LogSingleInstanceUnavailable(_logger, acquired.Error!.ToString());
        }

        PublishCommandLine(arguments);
    }

    private void InitializePackaged(string[] arguments)
    {
        AppInstance? current = null;
        AppActivationArguments? launch = null;
        try
        {
            current = AppInstance.GetCurrent();
            launch = current.GetActivatedEventArgs();
        }
        catch (Exception ex)
        {
            LogAppInstanceFailed(_logger, ex);
        }

        if (_mauiOptions.Value.SingleInstance && current is not null)
        {
            try
            {
                var primary = AppInstance.FindOrRegisterForKey(BuildInstanceKey());
                if (!primary.IsCurrent)
                {
                    NativeMethods.AllowSetForegroundWindow(primary.ProcessId);
                    var redirected = launch is not null && Redirect(primary, launch);
                    LogSecondaryInstance(_logger, redirected);
                    Win32Process.TerminateCurrentProcess();
                    return;
                }

                current.Activated += OnRedirectedActivation;
                _appInstance = current;
            }
            catch (Exception ex)
            {
                LogAppInstanceFailed(_logger, ex);
            }
        }

        foreach (var activation in Map(launch, arguments, isColdStart: true))
        {
            Publish(activation);
        }
    }

    private string BuildInstanceKey() => _sdkOptions.Value.AppId + "/" + _desktopOptions.Value.SingleInstanceChannel;

    private bool Redirect(AppInstance primary, AppActivationArguments launch)
    {
        // RedirectActivationToAsync must not be awaited on the STA UI thread; run it on the thread pool and wait with COM
        // message pumping (the pattern the Windows App SDK documents for single-instanced WinUI apps).
        var completed = new ManualResetEvent(false);
        var succeeded = 0;
        _ = Task.Run(async () =>
        {
            try
            {
                await primary.RedirectActivationToAsync(launch);
                Interlocked.Exchange(ref succeeded, 1);
            }
            catch (Exception ex)
            {
                LogRedirectFailed(_logger, ex);
            }
            finally
            {
                completed.Set();
            }
        });

        if (WaitPumping(completed, RedirectTimeoutMilliseconds))
        {
            completed.Dispose();
        }

        return Volatile.Read(ref succeeded) == 1;
    }

    private static unsafe bool WaitPumping(WaitHandle handle, uint timeoutMilliseconds)
    {
        var safeHandle = handle.SafeWaitHandle;
        var addedRef = false;
        try
        {
            safeHandle.DangerousAddRef(ref addedRef);
            var raw = safeHandle.DangerousGetHandle();
            var hr = NativeMethods.CoWaitForMultipleObjects(NativeMethods.CWMO_DEFAULT, timeoutMilliseconds, 1, &raw, out _);
            if (hr == 0)
            {
                return true;
            }

            if (hr == RpcCallPending)
            {
                return false;
            }
        }
        finally
        {
            if (addedRef)
            {
                safeHandle.DangerousRelease();
            }
        }

        // COM is not initialized on this thread (not the UI thread): a plain wait cannot block message processing.
        return handle.WaitOne(TimeSpan.FromMilliseconds(timeoutMilliseconds));
    }

    // RPC_S_CALLPENDING: CoWaitForMultipleObjects timed out.
    private const int RpcCallPending = unchecked((int)0x80010115);

    private void OnRedirectedActivation(object? sender, AppActivationArguments args)
    {
        try
        {
            foreach (var activation in Map(args, [], isColdStart: false))
            {
                Publish(activation);
            }
        }
        catch (Exception ex)
        {
            LogAppInstanceFailed(_logger, ex);
        }
    }

    private IReadOnlyList<AppActivation> Map(AppActivationArguments? args, IReadOnlyList<string> commandLine, bool isColdStart)
    {
        var deepLinks = _sdkOptions.Value.DeepLinks;
        var fallbackKind = isColdStart ? ActivationKind.Launch : ActivationKind.SecondaryInstance;
        try
        {
            switch (args?.Kind)
            {
                case ExtendedActivationKind.Protocol when args.Data is WinActivation.IProtocolActivatedEventArgs { Uri: { } uri }:
                    return [new AppActivation { Kind = ActivationKind.DeepLink, Uri = uri, IsColdStart = isColdStart, Source = AppInstanceSource }];

                case ExtendedActivationKind.File when args.Data is WinActivation.IFileActivatedEventArgs file:
                    var files = file.Files
                        .Select(static item => item.Path)
                        .Where(static path => !string.IsNullOrEmpty(path))
                        .Select(static path => NativeFile.FromPath(path))
                        .ToArray();
                    if (files.Length > 0)
                    {
                        return [new AppActivation { Kind = ActivationKind.File, Files = files, IsColdStart = isColdStart, Source = AppInstanceSource }];
                    }

                    break;

                case ExtendedActivationKind.CommandLineLaunch when args.Data is WinActivation.ICommandLineActivatedEventArgs commandLineLaunch:
                    // The operation's command line starts with the execution alias that launched the app.
                    var operation = commandLineLaunch.Operation;
                    var forwarded = Win32Process.SplitArguments(operation.Arguments).Skip(1).ToArray();
                    return ActivationArguments.Parse(forwarded, deepLinks, fallbackKind, isColdStart, operation.CurrentDirectoryPath, _logger);

                case ExtendedActivationKind.Launch when !isColdStart && args.Data is WinActivation.ILaunchActivatedEventArgs launch:
                    return ActivationArguments.Parse(Win32Process.SplitArguments(launch.Arguments), deepLinks, fallbackKind, isColdStart, logger: _logger);
            }
        }
        catch (Exception ex)
        {
            LogAppInstanceFailed(_logger, ex);
        }

        return isColdStart
            ? ActivationArguments.Parse(commandLine, deepLinks, ActivationKind.Launch, isColdStart: true, Environment.CurrentDirectory, _logger)
            : [new AppActivation { Kind = ActivationKind.SecondaryInstance, IsColdStart = false, Source = AppInstanceSource }];
    }

    private void PublishCommandLine(IReadOnlyList<string> arguments)
    {
        foreach (var activation in ActivationArguments.Parse(arguments, _sdkOptions.Value.DeepLinks, ActivationKind.Launch, isColdStart: true, Environment.CurrentDirectory, _logger))
        {
            Publish(activation);
        }
    }

    private void Publish(AppActivation activation)
    {
        try
        {
            var pending = _publisher.PublishAsync(activation);
            if (!pending.IsCompletedSuccessfully)
            {
                _ = ObserveAsync(pending, activation.Kind);
            }
        }
        catch (Exception ex)
        {
            LogPublishFailed(_logger, activation.Kind, ex);
        }
    }

    private async Task ObserveAsync(ValueTask pending, ActivationKind kind)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogPublishFailed(_logger, kind, ex);
        }
    }

    private async Task RegisterProtocolSchemesAsync()
    {
        try
        {
            var schemes = new HashSet<string>(_sdkOptions.Value.DeepLinks.Schemes, StringComparer.OrdinalIgnoreCase);
            if (_windowsOptions.Value.NotificationActivationScheme is { Length: > 0 } toastScheme)
            {
                schemes.Add(toastScheme);
            }

            foreach (var scheme in schemes)
            {
                var registered = await _protocols.IsRegisteredAsync(scheme).ConfigureAwait(false);
                if (registered.IsSuccess && registered.Value)
                {
                    continue;
                }

                var result = await _protocols.RegisterAsync(scheme, new ProtocolRegistrationOptions { DisplayName = _sdkOptions.Value.DisplayName }).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    LogProtocolRegistered(_logger, scheme);
                }
                else
                {
                    LogProtocolRegistrationFailed(_logger, scheme, result.Error.ToString());
                }
            }
        }
        catch (Exception ex)
        {
            LogProtocolRegistrationFailed(_logger, "*", ex.Message);
        }
    }

    [LoggerMessage(EventId = 8101, Level = LogLevel.Information, Message = "Another instance of the app is running; this instance handed over its activation (delivered: {Delivered}) and exits.")]
    private static partial void LogSecondaryInstance(ILogger logger, bool delivered);

    [LoggerMessage(EventId = 8102, Level = LogLevel.Warning, Message = "Single-instance enforcement is unavailable; continuing as an independent instance. {Reason}")]
    private static partial void LogSingleInstanceUnavailable(ILogger logger, string reason);

    [LoggerMessage(EventId = 8103, Level = LogLevel.Warning, Message = "Windows App SDK app instancing failed.")]
    private static partial void LogAppInstanceFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 8104, Level = LogLevel.Warning, Message = "Redirecting the activation to the running instance failed.")]
    private static partial void LogRedirectFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 8105, Level = LogLevel.Error, Message = "Publishing a {Kind} activation failed.")]
    private static partial void LogPublishFailed(ILogger logger, ActivationKind kind, Exception exception);

    [LoggerMessage(EventId = 8106, Level = LogLevel.Information, Message = "Registered the URL scheme '{Scheme}' for this executable.")]
    private static partial void LogProtocolRegistered(ILogger logger, string scheme);

    [LoggerMessage(EventId = 8107, Level = LogLevel.Warning, Message = "Registering the URL scheme '{Scheme}' failed: {Reason}")]
    private static partial void LogProtocolRegistrationFailed(ILogger logger, string scheme, string reason);

    [LoggerMessage(EventId = 8108, Level = LogLevel.Debug, Message = "RegisterProtocolSchemes is ignored in packaged apps: declare windows.protocol extensions in Package.appxmanifest.")]
    private static partial void LogProtocolsFromManifest(ILogger logger);
}
#endif
