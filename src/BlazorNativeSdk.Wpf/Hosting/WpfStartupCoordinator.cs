using System.Windows;
using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;
using BlazorNativeSdk.WindowsPlatform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk.Wpf;

/// <summary>
/// Implements <see cref="WpfNativeSdk.StartAsync"/>: single-instance enforcement, launch activation, protocol registration
/// and SDK start-up, and releases the single-instance lease when the application exits.
/// </summary>
internal sealed partial class WpfStartupCoordinator : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan s_exitTimeout = TimeSpan.FromSeconds(2);

    private readonly IServiceProvider _services;
    private readonly WpfNativeOptions _options;
    private readonly NativeSdkOptions _sdkOptions;
    private readonly DesktopNativeOptions _desktopOptions;
    private readonly WindowsNativeOptions _windowsOptions;
    private readonly ILogger<WpfStartupCoordinator> _logger;
    private readonly Lock _gate = new();
    private Task<bool>? _start;
    private ISingleInstanceLease? _lease;
    private Application? _application;
    private bool _processExitHooked;
    private int _disposed;

    public WpfStartupCoordinator(
        IServiceProvider services,
        IOptions<WpfNativeOptions> options,
        IOptions<NativeSdkOptions> sdkOptions,
        IOptions<DesktopNativeOptions> desktopOptions,
        IOptions<WindowsNativeOptions> windowsOptions,
        ILogger<WpfStartupCoordinator> logger)
    {
        _services = services;
        _options = options.Value;
        _sdkOptions = sdkOptions.Value;
        _desktopOptions = desktopOptions.Value;
        _windowsOptions = windowsOptions.Value;
        _logger = logger;
    }

    /// <summary>The single-instance lease held by the primary instance (diagnostics and tests).</summary>
    internal ISingleInstanceLease? Lease => Volatile.Read(ref _lease);

    public Task<bool> StartAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_gate)
        {
            if (_start is { IsCanceled: false } started)
            {
                return started;
            }

            // Runs on the caller's thread (normally the UI thread in OnStartup), before the first await.
            HookShutdown();
            return _start = StartCoreAsync(arguments, cancellationToken);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Unhook();
        var release = ReleaseLeaseAsync();
        if (!release.IsCompleted && !release.Wait(s_exitTimeout))
        {
            LogExitTimedOut(_logger);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Unhook();
        await ReleaseLeaseAsync().ConfigureAwait(false);
    }

    private async Task<bool> StartCoreAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var launchPublished = false;
        if (_options.SingleInstance)
        {
            var singleInstance = _services.GetRequiredService<ISingleInstanceService>();
            var acquired = await singleInstance.AcquireAsync(arguments, cancellationToken).ConfigureAwait(false);
            if (acquired.TryGetValue(out var lease))
            {
                if (!lease.IsPrimary)
                {
                    LogSecondary(_logger, lease.ArgumentsForwarded);
                    await lease.DisposeAsync().ConfigureAwait(false);
                    return false;
                }

                if (!KeepLease(lease))
                {
                    // Disposed while starting: the application is shutting down.
                    await lease.DisposeAsync().ConfigureAwait(false);
                    return false;
                }

                // The desktop service publishes the primary's own arguments when PublishLaunchActivation is enabled.
                launchPublished = singleInstance is DesktopSingleInstanceService;
                LogPrimary(_logger);
            }
            else
            {
                // Never block the app because of single-instance infrastructure; run as an independent instance.
                LogSingleInstanceUnavailable(_logger, acquired.Error!.ToString());
            }
        }

        if (!launchPublished && _desktopOptions.PublishLaunchActivation)
        {
            await PublishLaunchActivationAsync(arguments, cancellationToken).ConfigureAwait(false);
        }

        if (_services.GetService<WpfWindowManager>() is { } windows)
        {
            windows.PreloadPlacement(WpfWindowManager.DefaultMainWindowId);
        }

        if (_windowsOptions.SetProcessAppUserModelId)
        {
            await ApplyAppUserModelIdAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_options.RegisterProtocolSchemes)
        {
            _ = RegisterProtocolSchemesAsync();
        }

        _services.GetRequiredService<NativeSdkRuntime>().EnsureStarted();
        return true;
    }

    private async Task PublishLaunchActivationAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            var publisher = _services.GetRequiredService<IActivationPublisher>();
            var activations = ActivationArguments.Parse(arguments, _sdkOptions.DeepLinks, ActivationKind.Launch, isColdStart: true, Environment.CurrentDirectory, _logger);
            foreach (var activation in activations)
            {
                await publisher.PublishAsync(activation, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogLaunchActivationFailed(_logger, ex);
        }
    }

    // The process AUMID decides how the taskbar groups the app's windows, so it must be set before the first window is
    // shown. Querying the notification status initializes the toast identity (and the AUMID) without prompting.
    private async Task ApplyAppUserModelIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            var status = await _services.GetRequiredService<INotificationService>().GetStatusAsync(cancellationToken).ConfigureAwait(false);
            LogNotificationIdentity(_logger, status.Availability);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogNotificationIdentityFailed(_logger, ex);
        }
    }

    private async Task RegisterProtocolSchemesAsync()
    {
        try
        {
            var schemes = new SortedSet<string>(_sdkOptions.DeepLinks.Schemes, StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(_windowsOptions.NotificationActivationScheme))
            {
                _ = schemes.Add(_windowsOptions.NotificationActivationScheme);
            }

            var registration = _services.GetRequiredService<IProtocolRegistrationService>();
            var options = new ProtocolRegistrationOptions { DisplayName = _sdkOptions.DisplayName };
            foreach (var scheme in schemes)
            {
                var result = await registration.RegisterAsync(scheme, options).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    LogSchemeRegistered(_logger, scheme);
                }
                else if (result.Status == NativeResultStatus.Unsupported)
                {
                    LogSchemeSkipped(_logger, scheme, result.Error!.Message);
                }
                else
                {
                    LogSchemeFailed(_logger, scheme, result.Error!.ToString());
                }
            }
        }
        catch (Exception ex)
        {
            LogSchemeRegistrationCrashed(_logger, ex);
        }
    }

    private bool KeepLease(ISingleInstanceLease lease)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            _lease = lease;
            return true;
        }
    }

    private async Task ReleaseLeaseAsync()
    {
        var lease = Interlocked.Exchange(ref _lease, null);
        if (lease is null)
        {
            return;
        }

        try
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogReleaseFailed(_logger, ex);
        }
    }

    private void HookShutdown()
    {
        if (_application is not null || _processExitHooked)
        {
            return;
        }

        var application = Application.Current;
        if (application is null)
        {
            // Started before the WPF Application exists (custom Main): release on process exit instead.
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            _processExitHooked = true;
            return;
        }

        _application = application;
        WpfDispatch.RunOrPost(application.Dispatcher, () => application.Exit += OnApplicationExit);
        if (_services.GetService<IAppLifecycle>() is WpfAppLifecycle lifecycle)
        {
            lifecycle.AttachToApplication(application);
        }
    }

    private void Unhook()
    {
        Application? application;
        lock (_gate)
        {
            application = _application;
            _application = null;
            if (_processExitHooked)
            {
                AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
                _processExitHooked = false;
            }
        }

        if (application is not null && !application.Dispatcher.HasShutdownStarted)
        {
            WpfDispatch.RunOrPost(application.Dispatcher, () => application.Exit -= OnApplicationExit);
        }
    }

    private void OnApplicationExit(object? sender, ExitEventArgs e) => FlushAndRelease();

    private void OnProcessExit(object? sender, EventArgs e) => FlushAndRelease();

    // The process is about to end: persist window placements and stop the single-instance server, within a bounded time.
    private void FlushAndRelease()
    {
        var work = new List<Task>(2);
        try
        {
            if (_services.GetService<WpfWindowManager>() is { } windows)
            {
                work.Add(windows.FlushPlacementsAsync());
            }
        }
        catch (ObjectDisposedException)
        {
        }

        work.Add(ReleaseLeaseAsync());
        try
        {
            if (!Task.WaitAll([.. work], s_exitTimeout))
            {
                LogExitTimedOut(_logger);
            }
        }
        catch (AggregateException ex)
        {
            LogReleaseFailed(_logger, ex);
        }
    }

    [LoggerMessage(EventId = 7141, Level = LogLevel.Information, Message = "This process is the primary instance.")]
    private static partial void LogPrimary(ILogger logger);

    [LoggerMessage(EventId = 7142, Level = LogLevel.Information, Message = "Another instance is running (arguments forwarded: {Forwarded}); this instance should exit.")]
    private static partial void LogSecondary(ILogger logger, bool forwarded);

    [LoggerMessage(EventId = 7143, Level = LogLevel.Warning, Message = "Single-instance enforcement is unavailable; continuing as an independent instance. {Reason}")]
    private static partial void LogSingleInstanceUnavailable(ILogger logger, string reason);

    [LoggerMessage(EventId = 7144, Level = LogLevel.Error, Message = "Publishing the launch activation failed.")]
    private static partial void LogLaunchActivationFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7145, Level = LogLevel.Debug, Message = "Registered the URL scheme '{Scheme}' for this app.")]
    private static partial void LogSchemeRegistered(ILogger logger, string scheme);

    [LoggerMessage(EventId = 7146, Level = LogLevel.Debug, Message = "Did not register the URL scheme '{Scheme}': {Reason}")]
    private static partial void LogSchemeSkipped(ILogger logger, string scheme, string reason);

    [LoggerMessage(EventId = 7147, Level = LogLevel.Warning, Message = "Registering the URL scheme '{Scheme}' failed: {Reason}")]
    private static partial void LogSchemeFailed(ILogger logger, string scheme, string reason);

    [LoggerMessage(EventId = 7148, Level = LogLevel.Error, Message = "Registering URL schemes failed.")]
    private static partial void LogSchemeRegistrationCrashed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7149, Level = LogLevel.Warning, Message = "Releasing the single-instance lease failed.")]
    private static partial void LogReleaseFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7150, Level = LogLevel.Warning, Message = "Saving window placements and releasing the single-instance lease did not finish before exit.")]
    private static partial void LogExitTimedOut(ILogger logger);

    [LoggerMessage(EventId = 7151, Level = LogLevel.Debug, Message = "Initialized the notification identity before the first window ({Availability}).")]
    private static partial void LogNotificationIdentity(ILogger logger, CapabilityAvailability availability);

    [LoggerMessage(EventId = 7152, Level = LogLevel.Warning, Message = "Initializing the notification identity failed.")]
    private static partial void LogNotificationIdentityFailed(ILogger logger, Exception exception);
}
