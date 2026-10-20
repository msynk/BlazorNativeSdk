using BackgroundTasks;
using CoreFoundation;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>
/// Registers <c>BGTaskScheduler</c> launch handlers (only possible while the app finishes launching) and runs due work when the
/// system launches a task.
/// </summary>
internal static class AppleBackgroundTasks
{
    /// <summary>How long a run may continue after the system expired it before the task is completed anyway.</summary>
    private static readonly TimeSpan s_expirationGrace = TimeSpan.FromSeconds(2);
    private static readonly Lock s_gate = new();
    private static readonly HashSet<string> s_registered = new(StringComparer.Ordinal);
    private static readonly DispatchQueue s_launchQueue = new("blazornativesdk.bgtasks");

    /// <summary>The refresh identifier passed to <see cref="AppleNativeSdk.Initialize(string?, string?)"/>.</summary>
    public static string? RequestedRefreshIdentifier { get; private set; }

    /// <summary>The processing identifier passed to <see cref="AppleNativeSdk.Initialize(string?, string?)"/>.</summary>
    public static string? RequestedProcessingIdentifier { get; private set; }

    /// <summary>Returns whether a launch handler is registered for <paramref name="identifier"/>.</summary>
    public static bool IsRegistered(string identifier)
    {
        lock (s_gate)
        {
            return s_registered.Contains(identifier);
        }
    }

    /// <summary>Registers the launch handlers. Identifiers missing from Info.plist are skipped.</summary>
    public static void Register(string refreshIdentifier, string processingIdentifier)
    {
        RequestedRefreshIdentifier = refreshIdentifier;
        RequestedProcessingIdentifier = processingIdentifier;
        if (OperatingSystem.IsMacCatalyst())
        {
            // Mac Catalyst apps keep running in the background; the adapter runs work in process instead of through BGTaskScheduler.
            return;
        }

        RegisterOne(refreshIdentifier);
        if (!string.Equals(processingIdentifier, refreshIdentifier, StringComparison.Ordinal))
        {
            RegisterOne(processingIdentifier);
        }
    }

    private static void RegisterOne(string identifier)
    {
        if (!AppleInfoPlist.ArrayContains(AppleInfoPlist.BackgroundTaskIdentifiers, identifier))
        {
            // Registering an identifier that is not permitted raises an Objective-C exception and terminates the app.
            AppleLog.BackgroundTaskNotPermitted(AppleServices.Logger, identifier);
            return;
        }

        lock (s_gate)
        {
            if (s_registered.Contains(identifier))
            {
                return;
            }

            try
            {
                if (BGTaskScheduler.Shared.Register(identifier, s_launchQueue, OnLaunch))
                {
                    s_registered.Add(identifier);
                }
                else
                {
                    AppleLog.BackgroundTaskRegistrationFailed(AppleServices.Logger, identifier, null);
                }
            }
            catch (Exception ex)
            {
                AppleLog.BackgroundTaskRegistrationFailed(AppleServices.Logger, identifier, ex);
            }
        }
    }

    private static void OnLaunch(BGTask task)
    {
        var run = new TaskRun(task);
        task.ExpirationHandler = run.Expire;
        _ = run.RunAsync();
    }

    /// <summary>One system-initiated run. Completes the task exactly once.</summary>
    private sealed class TaskRun(BGTask task)
    {
        private readonly CancellationTokenSource _expiration = new();
        private readonly string _identifier = task.Identifier;
        private int _completed;

        public void Expire()
        {
            AppleLog.BackgroundTaskExpired(AppleServices.Logger, _identifier);
            try
            {
                _expiration.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            // Workers should stop promptly; if one ignores cancellation, complete anyway so iOS does not terminate the app.
            _ = CompleteAfterGraceAsync();
        }

        public async Task RunAsync()
        {
            var success = false;
            var executed = 0;
            try
            {
                AppleLog.BackgroundTaskStarted(AppleServices.Logger, _identifier);
                var services = await AppleServices.WaitAsync(_expiration.Token).ConfigureAwait(false);
                if (services.GetService<AppleBackgroundWorkScheduler>() is { } scheduler)
                {
                    executed = await scheduler.RunForSystemTaskAsync(_expiration.Token).ConfigureAwait(false);
                }

                success = true;
            }
            catch (OperationCanceledException) when (_expiration.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                AppleLog.BackgroundTaskFailed(AppleServices.Logger, _identifier, ex);
            }
            finally
            {
                Complete(success, executed);
            }
        }

        private async Task CompleteAfterGraceAsync()
        {
            await Task.Delay(s_expirationGrace).ConfigureAwait(false);
            Complete(success: false, executed: 0);
        }

        private void Complete(bool success, int executed)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            AppleLog.BackgroundTaskCompleted(AppleServices.Logger, _identifier, executed, success);
            // The token source is not disposed: the run may still observe it after a grace-period completion, and it owns no timer.
            task.SetTaskCompleted(success);
        }
    }
}
