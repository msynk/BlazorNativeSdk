using System.ComponentModel;
using Android.Content;
using Android.Runtime;
using AndroidX.Work;
using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WorkInfo = BlazorNativeSdk.Lifecycle.WorkInfo;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>
/// The WorkManager worker that runs BlazorNativeSdk background work. WorkManager instantiates it by its stable Java name
/// (<c>blazornativesdk.androidplatform.NativeSdkWorker</c>), so pending work keeps running across app updates.
/// Infrastructure: apps do not use this type directly.
/// </summary>
/// <remarks>
/// <see cref="DoWork"/> runs on a WorkManager background thread. It waits (up to
/// <see cref="AndroidNativeOptions.WorkerStartupTimeout"/>) for the SDK to start when the OS launched the process only for
/// this work, resolves the keyed <see cref="IBackgroundWorker"/> from a new scope and maps its result: success and failure
/// are final, retry uses exponential backoff until <see cref="Lifecycle.WorkRequest.MaxAttempts"/> runs have happened.
/// When WorkManager stops the work (constraints no longer met, quota, 10-minute limit), the cancellation token passed to
/// the worker fires.
/// </remarks>
[Register("blazornativesdk/androidplatform/NativeSdkWorker")]
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed partial class NativeSdkWorker : Worker
{
    private static long s_startupTimeoutTicks = TimeSpan.FromSeconds(20).Ticks;
    private readonly CancellationTokenSource _stopped = new();

    /// <summary>Called by WorkManager.</summary>
    public NativeSdkWorker(Context context, WorkerParameters workerParams)
        : base(context, workerParams)
    {
    }

    /// <summary>How long a run waits for the SDK to start; set from <see cref="AndroidNativeOptions.WorkerStartupTimeout"/>.</summary>
    internal static TimeSpan StartupTimeout
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref s_startupTimeoutTicks));
        set => Interlocked.Exchange(ref s_startupTimeoutTicks, value.Ticks);
    }

    /// <inheritdoc />
    public override Result DoWork()
    {
        var (workerName, workId, maxAttempts, input) = AndroidWorkMapper.DecodeInput(AndroidWorkManagerScheduler.FromData(InputData));
        if (string.IsNullOrEmpty(workerName) || string.IsNullOrEmpty(workId))
        {
            return Result.InvokeFailure()!;
        }

        IServiceProvider? provider;
        try
        {
            provider = AndroidServices.WaitForProviderAsync(StartupTimeout, _stopped.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return Result.InvokeRetry()!;
        }

        if (provider is null)
        {
            // The app did not start the SDK in this process (MAUI starts it at app creation; other apps must call
            // StartBlazorNativeSdkAsync in Application.OnCreate). Try again later.
            return Result.InvokeRetry()!;
        }

        var logger = provider.GetService<ILoggerFactory>()?.CreateLogger<NativeSdkWorker>() ?? (ILogger)Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        var scheduler = provider.GetService<AndroidWorkManagerScheduler>();
        var time = provider.GetService<TimeProvider>() ?? TimeProvider.System;
        var attempt = RunAttemptCount + 1;
        var startedAt = time.GetUtcNow();
        scheduler?.ReportRun(new WorkInfo { Id = workId, WorkerName = workerName, Status = WorkStatus.Running, Attempts = attempt, LastRunAt = startedAt });

        WorkResult result;
        try
        {
            result = ExecuteAsync(provider, workerName, new WorkContext
            {
                WorkId = workId,
                WorkerName = workerName,
                Input = input,
                Attempt = attempt,
                IsOsInitiated = true,
            }, _stopped.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (_stopped.IsCancellationRequested)
        {
            result = WorkResult.Retry("WorkManager stopped the run before it completed.");
        }
        catch (Exception ex)
        {
            LogWorkerFailed(logger, workId, ex);
            result = WorkResult.Retry(ex.Message);
        }

        var outcome = AndroidWorkMapper.MapResult(result.Kind, RunAttemptCount, maxAttempts);
        LogCompleted(logger, workId, result.Kind, outcome);
        scheduler?.ReportRun(new WorkInfo
        {
            Id = workId,
            WorkerName = workerName,
            Status = outcome switch
            {
                AndroidWorkOutcome.Success => WorkStatus.Succeeded,
                AndroidWorkOutcome.Retry => WorkStatus.Enqueued,
                _ => WorkStatus.Failed,
            },
            Attempts = attempt,
            LastRunAt = startedAt,
            LastMessage = result.Message,
            Output = result.Output,
        });

        return outcome switch
        {
            AndroidWorkOutcome.Success => Result.InvokeSuccess(BuildOutput(result))!,
            AndroidWorkOutcome.Retry => Result.InvokeRetry()!,
            _ => Result.InvokeFailure(BuildOutput(result))!,
        };
    }

    /// <inheritdoc />
    public override void OnStopped()
    {
        base.OnStopped();
        try
        {
            _stopped.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stopped.Dispose();
        }

        base.Dispose(disposing);
    }

    private static async Task<WorkResult> ExecuteAsync(IServiceProvider provider, string workerName, WorkContext context, CancellationToken cancellationToken)
    {
        var scope = provider.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var worker = scope.ServiceProvider.GetKeyedService<IBackgroundWorker>(workerName);
            if (worker is null)
            {
                return WorkResult.Failure($"No background worker is registered with the name '{workerName}'.");
            }

            return await worker.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Data BuildOutput(WorkResult result)
    {
        try
        {
            return AndroidWorkManagerScheduler.ToData(AndroidWorkMapper.EncodeOutput(result));
        }
        catch (Java.Lang.IllegalStateException)
        {
            // Output above WorkManager's 10 KB limit: keep a short explanation instead.
            return AndroidWorkManagerScheduler.ToData(AndroidWorkMapper.EncodeOutput(WorkResult.Failure("The worker output exceeded WorkManager's 10 KB limit and was dropped.")
                with { Kind = result.Kind }));
        }
    }

    [LoggerMessage(EventId = 3981, Level = LogLevel.Error, Message = "Background work {WorkId} threw an exception.")]
    private static partial void LogWorkerFailed(ILogger logger, string workId, Exception exception);

    [LoggerMessage(EventId = 3982, Level = LogLevel.Debug, Message = "Background work {WorkId} returned {Result}; reported {Outcome} to WorkManager.")]
    private static partial void LogCompleted(ILogger logger, string workId, WorkResultKind result, AndroidWorkOutcome outcome);
}
