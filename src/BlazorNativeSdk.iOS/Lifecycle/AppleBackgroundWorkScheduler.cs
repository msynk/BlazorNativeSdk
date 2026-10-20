using System.Text.Json;
using BackgroundTasks;
using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>
/// <see cref="IBackgroundWorkScheduler"/> that persists and runs work with the Core <see cref="InProcessBackgroundWorkScheduler"/>
/// and asks iOS to launch the app for due work through <c>BGTaskScheduler</c>.
/// </summary>
/// <remarks>
/// <para>
/// After every change the adapter submits one <c>BGAppRefreshTaskRequest</c> for the earliest pending work, and one
/// <c>BGProcessingTaskRequest</c> for <see cref="WorkRequest.IsLongRunning"/> work (with <c>RequiresNetworkConnectivity</c> and
/// <c>RequiresExternalPower</c> derived from its constraints). When iOS launches the task, due work runs with
/// <see cref="WorkContext.IsOsInitiated"/> set and the remaining work is resubmitted.
/// </para>
/// <para>
/// Setup: list both identifiers (<see cref="AppleNativeSdk.DefaultRefreshTaskIdentifier"/>,
/// <see cref="AppleNativeSdk.DefaultProcessingTaskIdentifier"/> or your own) in Info.plist <c>BGTaskSchedulerPermittedIdentifiers</c>,
/// add <c>fetch</c> and <c>processing</c> to <c>UIBackgroundModes</c>, and call <see cref="AppleNativeSdk.Initialize()"/> from
/// <c>FinishedLaunching</c>. When something is missing, work is still persisted and runs in process while the app is in the
/// foreground, and status / <see cref="ScheduleAsync"/> report <see cref="NativeErrorCodes.MissingDeclaration"/> or
/// <see cref="NativeErrorCodes.HostNotReady"/>.
/// </para>
/// <para>
/// Mac Catalyst apps are not suspended in the background, so there the adapter does not use <c>BGTaskScheduler</c>: work runs in
/// process whenever the app is running, in the foreground or background.
/// </para>
/// </remarks>
public sealed partial class AppleBackgroundWorkScheduler : IBackgroundWorkScheduler, IDisposable, IAsyncDisposable
{
    private const string MetadataPrefix = "bns.apple.work.";
    private const string FetchMode = "fetch";
    private const string ProcessingMode = "processing";
    private const string OsScheduledFeature = "os-scheduled";
    private readonly InProcessBackgroundWorkScheduler _inner;
    private readonly INativeStateStore _store;
    private readonly AppleNativeOptions _options;
    private readonly ILogger<AppleBackgroundWorkScheduler> _logger;
    private readonly SemaphoreSlim _submitLock = new(1, 1);
    private string? _lastSubmitError;
    private int _disposed;

    /// <summary>Creates the scheduler.</summary>
    public AppleBackgroundWorkScheduler(
        InProcessBackgroundWorkScheduler inner,
        INativeStateStore store,
        IOptions<AppleNativeOptions> options,
        ILogger<AppleBackgroundWorkScheduler> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);
        _inner = inner;
        _store = store;
        _options = options.Value;
        _logger = logger;
        Policy = OperatingSystem.IsMacCatalyst() ? CreateMacCatalystPolicy() : CreateIosPolicy();
        _inner.WorkChanged += OnInnerWorkChanged;
    }

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.BackgroundWork;

    /// <inheritdoc />
    public BackgroundExecutionPolicy Policy { get; }

    /// <summary>The <c>BGAppRefreshTask</c> identifier in use.</summary>
    public string RefreshTaskIdentifier =>
        _options.RefreshTaskIdentifier ?? AppleBackgroundTasks.RequestedRefreshIdentifier ?? AppleNativeSdk.DefaultRefreshTaskIdentifier;

    /// <summary>The <c>BGProcessingTask</c> identifier in use.</summary>
    public string ProcessingTaskIdentifier =>
        _options.ProcessingTaskIdentifier ?? AppleBackgroundTasks.RequestedProcessingIdentifier ?? AppleNativeSdk.DefaultProcessingTaskIdentifier;

    /// <inheritdoc />
    public event EventHandler<WorkInfo>? WorkChanged;

    /// <inheritdoc />
    public async ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var inner = await _inner.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var adapter = Policy.Adapter;
        if (OperatingSystem.IsMacCatalyst())
        {
            return inner with { Adapter = adapter };
        }

        if (CheckSetup(longRunning: false) is { } setupError)
        {
            return CapabilityStatus.FromError(setupError, adapter).WithFeatures(inner.Features);
        }

        var refreshStatus = await AppleUI.OnMainThreadAsync(() => UIApplication.SharedApplication.BackgroundRefreshStatus).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (refreshStatus != UIBackgroundRefreshStatus.Available)
        {
            return inner with
            {
                Adapter = adapter,
                Reason = refreshStatus == UIBackgroundRefreshStatus.Denied
                    ? "Background App Refresh is turned off for this app in Settings; work runs only while the app is in the foreground."
                    : "Background App Refresh is restricted (Low Power Mode, Screen Time or MDM); work runs only while the app is in the foreground.",
            };
        }

        if (Volatile.Read(ref _lastSubmitError) is { } submitError)
        {
            return inner with { Adapter = adapter, Reason = submitError };
        }

        return inner.WithFeatures(OsScheduledFeature) with { Adapter = adapter };
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns <see cref="NativeResultStatus.Unavailable"/> (<see cref="NativeErrorCodes.MissingDeclaration"/> or
    /// <see cref="NativeErrorCodes.HostNotReady"/>) when iOS cannot be asked to run the work in the background; the work is
    /// persisted anyway and runs in process while the app is in the foreground.
    /// </remarks>
    public async ValueTask<NativeResult<WorkInfo>> ScheduleAsync(WorkRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var kept = request.ExistingPolicy == ExistingWorkPolicy.Keep && await IsPendingAsync(request.Id, cancellationToken).ConfigureAwait(false);
        var result = await _inner.ScheduleAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result;
        }

        if (!kept)
        {
            await SaveMetadataAsync(request.Id, new AppleWorkMetadata { IsLongRunning = request.IsLongRunning, Constraints = request.Constraints }, cancellationToken).ConfigureAwait(false);
        }

        if (OperatingSystem.IsMacCatalyst())
        {
            return result;
        }

        if (CheckSetup(request.IsLongRunning) is { } setupError)
        {
            LogNotOsScheduled(_logger, setupError.Code);
            return NativeError.Unavailable(Capability, setupError.Code,
                $"{setupError.Message} The work was saved and runs in process while the app is in the foreground.");
        }

        await SubmitPendingAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public async ValueTask<NativeResult<bool>> CancelAsync(string workId, CancellationToken cancellationToken = default)
    {
        var result = await _inner.CancelAsync(workId, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            await _store.RemoveAsync(MetadataPrefix + workId, cancellationToken).ConfigureAwait(false);
            await SubmitPendingAsync(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<IReadOnlyList<WorkInfo>>> GetWorkAsync(CancellationToken cancellationToken = default) =>
        _inner.GetWorkAsync(cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _inner.WorkChanged -= OnInnerWorkChanged;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Runs due work for a system-launched task, then resubmits requests for the remaining work.</summary>
    internal async Task<int> RunForSystemTaskAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _inner.RunDueWorkAsync(isOsInitiated: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Resubmit even when the system expired the run, so remaining work gets another chance.
            await SubmitPendingAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private NativeError? CheckSetup(bool longRunning)
    {
        var identifier = longRunning ? ProcessingTaskIdentifier : RefreshTaskIdentifier;
        var mode = longRunning ? ProcessingMode : FetchMode;
        var missing = new List<string>();
        if (!AppleInfoPlist.ArrayContains(AppleInfoPlist.BackgroundTaskIdentifiers, identifier))
        {
            missing.Add($"'{identifier}' in {AppleInfoPlist.BackgroundTaskIdentifiers}");
        }

        if (!AppleInfoPlist.ArrayContains(AppleInfoPlist.BackgroundModes, mode))
        {
            missing.Add($"'{mode}' in {AppleInfoPlist.BackgroundModes}");
        }

        if (missing.Count > 0)
        {
            return NativeError.Unavailable(Capability, NativeErrorCodes.MissingDeclaration,
                $"Info.plist is missing {string.Join(" and ", missing)}, so iOS cannot run background work.");
        }

        if (!AppleBackgroundTasks.IsRegistered(identifier))
        {
            return NativeError.Unavailable(Capability, NativeErrorCodes.HostNotReady,
                $"No launch handler is registered for '{identifier}'. Call AppleNativeSdk.Initialize() from AppDelegate.FinishedLaunching " +
                "(with the same identifiers as AppleNativeOptions) so iOS can launch the app for background work.");
        }

        return null;
    }

    private async Task SubmitPendingAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsMacCatalyst())
        {
            return;
        }

        await _submitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var work = await _inner.GetWorkAsync(cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            DateTimeOffset? refreshAt = null;
            DateTimeOffset? processingAt = null;
            var requiresNetwork = false;
            var requiresPower = false;
            foreach (var item in work.Value ?? [])
            {
                if (item.Status is not (WorkStatus.Enqueued or WorkStatus.Running))
                {
                    continue;
                }

                var next = item.EarliestNextRun ?? now;
                var metadata = await LoadMetadataAsync(item.Id, cancellationToken).ConfigureAwait(false);
                if (metadata?.IsLongRunning == true)
                {
                    processingAt = processingAt is { } p && p <= next ? p : next;
                    requiresNetwork |= (metadata.Constraints & (WorkConstraints.NetworkConnected | WorkConstraints.UnmeteredNetwork)) != 0;
                    requiresPower |= (metadata.Constraints & WorkConstraints.Charging) != 0;
                }
                else
                {
                    refreshAt = refreshAt is { } r && r <= next ? r : next;
                }
            }

            Update(RefreshTaskIdentifier, refreshAt, now, static id => new BGAppRefreshTaskRequest(id));
            Update(ProcessingTaskIdentifier, processingAt, now, id => new BGProcessingTaskRequest(id)
            {
                RequiresNetworkConnectivity = requiresNetwork,
                RequiresExternalPower = requiresPower,
            });
        }
        finally
        {
            _submitLock.Release();
        }
    }

    private void Update(string identifier, DateTimeOffset? earliest, DateTimeOffset now, Func<string, BGTaskRequest> create)
    {
        // Never submit a request without a registered launch handler: iOS terminates the app when it launches such a task.
        if (!AppleBackgroundTasks.IsRegistered(identifier))
        {
            return;
        }

        if (earliest is null)
        {
            BGTaskScheduler.Shared.Cancel(identifier);
            return;
        }

        using var request = create(identifier);
        request.EarliestBeginDate = NSDate.FromTimeIntervalSinceNow(Math.Max(0, (earliest.Value - now).TotalSeconds));
        if (BGTaskScheduler.Shared.Submit(request, out var error))
        {
            Volatile.Write(ref _lastSubmitError, null);
            LogSubmitted(_logger, identifier, earliest.Value);
            return;
        }

        var code = error?.Code ?? 0;
        var message = code switch
        {
            (nint)(long)BGTaskSchedulerErrorCode.Unavailable =>
                "iOS reports background tasks as unavailable (the simulator, Background App Refresh off, or Low Power Mode); work runs only while the app is in the foreground.",
            (nint)(long)BGTaskSchedulerErrorCode.NotPermitted =>
                $"iOS did not permit '{identifier}': check BGTaskSchedulerPermittedIdentifiers and UIBackgroundModes in Info.plist.",
            (nint)(long)BGTaskSchedulerErrorCode.TooManyPendingTaskRequests =>
                "Too many background task requests are pending for this app.",
            _ => error?.LocalizedDescription ?? "Submitting the background task request failed.",
        };
        Volatile.Write(ref _lastSubmitError, message);
        LogSubmitFailed(_logger, identifier, (long)code);
    }

    private async Task<bool> IsPendingAsync(string workId, CancellationToken cancellationToken)
    {
        var work = await _inner.GetWorkAsync(cancellationToken).ConfigureAwait(false);
        return work.Value?.Any(w => w.Id == workId && w.Status is WorkStatus.Enqueued or WorkStatus.Running) == true;
    }

    private ValueTask SaveMetadataAsync(string workId, AppleWorkMetadata metadata, CancellationToken cancellationToken) =>
        _store.SetAsync(MetadataPrefix + workId, JsonSerializer.Serialize(metadata, AppleJsonContext.Default.AppleWorkMetadata), cancellationToken);

    private async Task<AppleWorkMetadata?> LoadMetadataAsync(string workId, CancellationToken cancellationToken)
    {
        var json = await _store.GetAsync(MetadataPrefix + workId, cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, AppleJsonContext.Default.AppleWorkMetadata);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void OnInnerWorkChanged(object? sender, WorkInfo info) => WorkChanged?.Invoke(this, info);

    private static BackgroundExecutionPolicy CreateIosPolicy() => new()
    {
        Adapter = "ios-bgtasks",
        RunsInBackground = true,
        RunsAfterTermination = true,
        SurvivesReboot = false,
        PersistsAcrossLaunches = true,
        ExactTiming = false,
        MinimumPeriodicInterval = null,
        MaximumExecutionTime = TimeSpan.FromSeconds(30),
        SupportedConstraints = WorkConstraints.NetworkConnected | WorkConstraints.UnmeteredNetwork | WorkConstraints.Charging,
        Summary =
            "Work is persisted and runs in the app process while the app is in the foreground. In the background, or after the app " +
            "was terminated by the system, iOS may launch the app through BGTaskScheduler to run due work: refresh tasks get about " +
            "30 seconds, and IsLongRunning work runs as a processing task (typically overnight while charging). iOS alone decides " +
            "when and whether tasks run, based on usage patterns, battery, Low Power Mode and the Background App Refresh setting, " +
            "so timing is never exact and there is no minimum periodic interval. Nothing runs after the user force-quits the app " +
            "until it is opened again, and Apple does not document that pending requests survive a reboot (work resumes on the next " +
            "launch). Network constraints are checked before every run; Charging is enforced by iOS for IsLongRunning work only.",
    };

    private static BackgroundExecutionPolicy CreateMacCatalystPolicy() => new()
    {
        Adapter = "maccatalyst-in-process",
        RunsInBackground = true,
        RunsAfterTermination = false,
        SurvivesReboot = false,
        PersistsAcrossLaunches = true,
        ExactTiming = false,
        SupportedConstraints = WorkConstraints.NetworkConnected | WorkConstraints.UnmeteredNetwork,
        Summary =
            "Mac Catalyst apps are not suspended in the background, so persisted work runs in the app process whenever the app is " +
            "running (App Nap may delay it). Nothing runs while the app is closed; pending work resumes on the next launch.",
    };

    [LoggerMessage(EventId = 3801, Level = LogLevel.Debug, Message = "Submitted background task request {Identifier} (earliest {Earliest:o}).")]
    private static partial void LogSubmitted(ILogger logger, string identifier, DateTimeOffset earliest);

    [LoggerMessage(EventId = 3802, Level = LogLevel.Warning, Message = "Submitting background task request {Identifier} failed with BGTaskScheduler error {Code}.")]
    private static partial void LogSubmitFailed(ILogger logger, string identifier, long code);

    [LoggerMessage(EventId = 3803, Level = LogLevel.Warning, Message = "Work was saved but cannot be scheduled with iOS ({Code}); it runs only while the app is in the foreground.")]
    private static partial void LogNotOsScheduled(ILogger logger, string code);
}
