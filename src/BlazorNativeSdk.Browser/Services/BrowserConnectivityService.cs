using BlazorNativeSdk.Browser.Interop;
using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.Logging;

namespace BlazorNativeSdk.Browser.Services;

/// <summary>
/// Connectivity from <c>navigator.onLine</c>, the <c>online</c>/<c>offline</c> events and, where available, the Network
/// Information API (<c>navigator.connection</c>: type and Save-Data).
/// </summary>
/// <remarks>
/// <c>navigator.onLine</c> only says that the device has a network connection; it does not prove that the internet or your
/// server is reachable. The Network Information API exists only in Chromium-based browsers; elsewhere connection types and
/// the data-saver flag are unknown.
/// </remarks>
internal sealed partial class BrowserConnectivityService : IConnectivityService, IDisposable, IAsyncDisposable
{
    private readonly BrowserInterop _interop;
    private readonly TimeProvider _time;
    private readonly ILogger<BrowserConnectivityService> _logger;
    private readonly BrowserStartGate _start;
    private readonly Lock _gate = new();
    private ConnectivitySnapshot _current = ConnectivitySnapshot.Unknown;
    private EventHandler<ConnectivityChangedEventArgs>? _changed;
    private int _disposed;

    public BrowserConnectivityService(BrowserInterop interop, TimeProvider time, ILogger<BrowserConnectivityService> logger)
    {
        _interop = interop;
        _time = time;
        _logger = logger;
        _start = new BrowserStartGate(StartCoreAsync);
        _interop.Callbacks.ConnectivityChanged += OnConnectivityChanged;
    }

    public CapabilityId Capability => NativeCapabilities.Connectivity;

    public ConnectivitySnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Subscribing starts the browser listeners when they are not running yet.</summary>
    public event EventHandler<ConnectivityChangedEventArgs>? Changed
    {
        add
        {
            lock (_gate)
            {
                _changed += value;
            }

            _ = StartInBackgroundAsync();
        }
        remove
        {
            lock (_gate)
            {
                _changed -= value;
            }
        }
    }

    /// <summary>Reads the initial state and attaches the listeners once.</summary>
    public Task<NativeResult> StartAsync(CancellationToken cancellationToken = default) => _start.EnsureStartedAsync(cancellationToken);

    public async ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var started = await StartAsync(cancellationToken).ConfigureAwait(false);
        return started.IsSuccess
            ? CapabilityStatus.Available(Capability, BrowserConstants.AdapterName)
            : BrowserInteropExtensions.ToStatus(Capability, started.Error);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _interop.Callbacks.ConnectivityChanged -= OnConnectivityChanged;
            lock (_gate)
            {
                _changed = null;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task StartInBackgroundAsync()
    {
        var result = await StartAsync().ConfigureAwait(false);
        if (!result.IsSuccess && !BrowserInterop.IsHostNotReady(result.Error))
        {
            LogStartFailed(_logger, result.Error.Code);
        }
    }

    private async ValueTask<NativeResult> StartCoreAsync(CancellationToken cancellationToken)
    {
        var result = await _interop.InvokeAsync(Capability, "startConnectivity", [], cancellationToken).ConfigureAwait(false);
        var dto = BrowserInterop.Read(Capability, result, BrowserJsonContext.Default.ConnectivityDto);
        if (!dto.IsSuccess)
        {
            return dto.Error;
        }

        Update(dto.Value);
        return NativeResult.Ok;
    }

    private void OnConnectivityChanged(ConnectivityDto dto)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            Update(dto);
        }
    }

    private void Update(ConnectivityDto dto)
    {
        var next = ConnectivityMapper.ToSnapshot(dto, _time.GetUtcNow());
        ConnectivitySnapshot previous;
        EventHandler<ConnectivityChangedEventArgs>? handlers;
        lock (_gate)
        {
            previous = _current;
            if (IsSame(previous, next))
            {
                return;
            }

            _current = next;
            handlers = _changed;
        }

        try
        {
            handlers?.Invoke(this, new ConnectivityChangedEventArgs(previous, next));
        }
        catch (Exception ex)
        {
            LogHandlerFailed(_logger, ex);
        }
    }

    private static bool IsSame(ConnectivitySnapshot a, ConnectivitySnapshot b) =>
        a.Access == b.Access &&
        a.IsMetered == b.IsMetered &&
        a.IsDataSaverEnabled == b.IsDataSaverEnabled &&
        a.ConnectionTypes.SequenceEqual(b.ConnectionTypes);

    [LoggerMessage(EventId = 3901, Level = LogLevel.Warning, Message = "Starting browser connectivity monitoring failed: {Code}.")]
    private static partial void LogStartFailed(ILogger logger, string code);

    [LoggerMessage(EventId = 3902, Level = LogLevel.Error, Message = "A connectivity change handler failed.")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception);
}
