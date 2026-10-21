using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorNativeSdk.Samples;

/// <summary>Registers the services used by the sample pages on every host.</summary>
public static class SampleServices
{
    /// <summary>The custom URL scheme used by the samples (<c>bnssample://orders/42</c>).</summary>
    public const string Scheme = "bnssample";

    /// <summary>Applies the sample configuration shared by all hosts.</summary>
    public static INativeSdkBuilder AddSampleConfiguration(this INativeSdkBuilder sdk)
    {
        ArgumentNullException.ThrowIfNull(sdk);
        sdk.Configure(o =>
        {
            o.AppId = "dev.blazornativesdk.samples";
            o.AppName = "BlazorNativeSdk Samples";
            o.DeepLinks.Schemes.Add(Scheme);
            o.DeepLinks.Hosts.Add("samples.blazornativesdk.dev");
        });
        sdk.AddBackgroundWorker<DemoSyncWorker>(DemoSyncWorker.Name);
        sdk.AddActivationHandler<ArchiveOrderHandler>();
        sdk.Services.AddSingleton<ActivityLog>();
        return sdk;
    }
}

/// <summary>
/// Handles the "Archive" button of the sample's order notification instead of navigating to the notification route.
/// Handlers registered with <c>AddActivationHandler</c> run before any component and also receive background activations
/// (Android and iOS deliver <see cref="Devices.NotificationAction.OpensApp"/> = <see langword="false"/> actions to a
/// process that may never show UI). Browsers always focus or open the app for an action, because .NET runs in the page.
/// </summary>
public sealed class ArchiveOrderHandler(ActivityLog log) : IActivationHandler
{
    /// <summary>The id of the action handled here.</summary>
    public const string ActionId = "archive";

    /// <inheritdoc />
    public ValueTask<bool> HandleAsync(AppActivation activation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activation);
        if (activation.Kind != ActivationKind.Notification || activation.ActionId != ActionId)
        {
            return ValueTask.FromResult(false); // Not ours: other handlers, then route navigation, take over.
        }

        var order = activation.Data.TryGetValue("orderId", out var id) ? id : "?";
        log.Add($"Order #{order} archived from its notification{(activation.IsBackground ? " in the background" : string.Empty)}, without navigating.");
        return ValueTask.FromResult(true); // Handled: the notification route is not navigated to.
    }
}

/// <summary>A displayable summary of a <see cref="NativeResult"/>.</summary>
/// <param name="Status">The status.</param>
/// <param name="Code">The error code, if any.</param>
/// <param name="Message">A message.</param>
public sealed record Outcome(NativeResultStatus Status, string? Code, string Message)
{
    /// <summary>Describes a non-generic result.</summary>
    public static Outcome From(NativeResult result, string successMessage) =>
        result.IsSuccess ? new(NativeResultStatus.Success, null, successMessage) : new(result.Status, result.Error.Code, result.Error.Message);

    /// <summary>Describes a typed result.</summary>
    public static Outcome From<T>(NativeResult<T> result, Func<T, string> describe)
    {
        ArgumentNullException.ThrowIfNull(describe);
        return result.IsSuccess ? new(NativeResultStatus.Success, null, describe(result.Value)) : new(result.Status, result.Error.Code, result.Error.Message);
    }
}

/// <summary>A tiny in-memory log shown by the sample pages.</summary>
public sealed class ActivityLog
{
    private readonly Lock _gate = new();
    private readonly List<string> _entries = [];

    /// <summary>Raised when an entry is added.</summary>
    public event Action? Changed;

    /// <summary>The newest entries first.</summary>
    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>Adds an entry.</summary>
    public void Add(string message)
    {
        lock (_gate)
        {
            _entries.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
            if (_entries.Count > 200)
            {
                _entries.RemoveAt(_entries.Count - 1);
            }
        }

        Changed?.Invoke();
    }
}

/// <summary>A sample worker that simulates a sync and reports how many items it processed.</summary>
public sealed class DemoSyncWorker(ActivityLog log) : IBackgroundWorker
{
    /// <summary>The worker name.</summary>
    public const string Name = "demo-sync";

    /// <inheritdoc />
    public async ValueTask<WorkResult> ExecuteAsync(WorkContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        var items = Random.Shared.Next(1, 20);
        log.Add($"Worker '{context.WorkId}' attempt {context.Attempt} synced {items} items (OS initiated: {context.IsOsInitiated}).");
        return WorkResult.Success(new Dictionary<string, string> { ["items"] = items.ToString(System.Globalization.CultureInfo.InvariantCulture) });
    }
}
