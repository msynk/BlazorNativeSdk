using BlazorNativeSdk.Browser.Interop;
using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.Logging;

namespace BlazorNativeSdk.Browser.Services;

/// <summary>
/// Page lifecycle: <c>visibilitychange</c> (Background), <c>focus</c>/<c>blur</c> (Active/Inactive), <c>pagehide</c>
/// (Terminating), <c>freeze</c>/<c>resume</c> and <c>pageshow</c> with <c>persisted</c> (back/forward cache restore).
/// </summary>
/// <remarks>
/// Browsers give no background execution guarantee: hidden tabs are throttled and may be frozen or discarded.
/// <c>pagehide</c> and <c>freeze</c> are delivered best effort; with Blazor Server the notification may not reach the server
/// before the page is gone.
/// </remarks>
internal sealed class BrowserAppLifecycle : AppLifecycleBase, IDisposable, IAsyncDisposable
{
    private readonly BrowserInterop _interop;
    private readonly BrowserStartGate _start;
    private int _disposed;

    public BrowserAppLifecycle(BrowserInterop interop, TimeProvider timeProvider, ILogger<BrowserAppLifecycle> logger)
        : base(timeProvider, logger)
    {
        _interop = interop;
        _start = new BrowserStartGate(StartCoreAsync);
        _interop.Callbacks.LifecycleChanged += OnLifecycleChanged;
    }

    public override BackgroundExecutionGuarantee BackgroundGuarantee => BackgroundExecutionGuarantee.BestEffort;

    protected override string AdapterName => BrowserConstants.AdapterName;

    /// <summary>Attaches the page listeners once. Retried on the next call when JavaScript was not available.</summary>
    public Task<NativeResult> StartAsync(CancellationToken cancellationToken = default) => _start.EnsureStartedAsync(cancellationToken);

    public override async ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var started = await StartAsync(cancellationToken).ConfigureAwait(false);
        return started.IsSuccess
            ? await base.GetStatusAsync(cancellationToken).ConfigureAwait(false)
            : BrowserInteropExtensions.ToStatus(Capability, started.Error);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _interop.Callbacks.LifecycleChanged -= OnLifecycleChanged;
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private async ValueTask<NativeResult> StartCoreAsync(CancellationToken cancellationToken)
    {
        var result = await _interop.InvokeAsync(Capability, "startLifecycle", [], cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result.Error;
        }

        if (result.Value.ValueKind == System.Text.Json.JsonValueKind.String &&
            LifecycleStateMapper.ToState(result.Value.GetString()) is { } state)
        {
            ReportState(state, "initial");
        }

        return NativeResult.Ok;
    }

    private void OnLifecycleChanged(string state, string reason)
    {
        if (Volatile.Read(ref _disposed) == 0 && LifecycleStateMapper.ToState(state) is { } mapped)
        {
            ReportState(mapped, reason);
        }
    }
}
