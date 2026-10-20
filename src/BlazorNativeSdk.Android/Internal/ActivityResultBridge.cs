using Android.App;
using AndroidX.Activity;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
using Microsoft.Maui.ApplicationModel;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>
/// Launches activities for a result through the AndroidX <see cref="ActivityResultRegistry"/> of the current
/// <see cref="ComponentActivity"/> (MAUI's <c>MauiAppCompatActivity</c> is one), so no <c>OnActivityResult</c> override is needed.
/// </summary>
/// <remarks>
/// Each launch registers a unique key and unregisters it after the result arrives. If the activity is destroyed while
/// the other activity is in front (process death or a configuration change the app does not handle), the result cannot
/// be delivered and the operation fails with <see cref="NativeErrorCodes.HostNotReady"/>.
/// </remarks>
internal static class ActivityResultBridge
{
    /// <summary>Launches an <see cref="Android.Content.Intent"/> and returns the result code and data.</summary>
    public static async Task<(Result ResultCode, Android.Content.Intent? Data)> StartActivityForResultAsync(
        CapabilityId capability,
        Android.Content.Intent intent,
        CancellationToken cancellationToken)
    {
        var result = await LaunchAsync(capability, static () => new ActivityResultContracts.StartActivityForResult(), intent, cancellationToken)
            .ConfigureAwait(false);
        if (result is null)
        {
            return (Result.Canceled, null);
        }

        using var activityResult = Android.Runtime.Extensions.JavaCast<ActivityResult>(result);
        return ((Result)activityResult.ResultCode, activityResult.Data);
    }

    /// <summary>Launches a contract and returns its parsed output (may be <see langword="null"/>).</summary>
    /// <exception cref="NativeCapabilityException">No usable activity, or the activity was destroyed before the result arrived.</exception>
    public static async Task<Java.Lang.Object?> LaunchAsync(
        CapabilityId capability,
        Func<ActivityResultContract> contractFactory,
        Java.Lang.Object input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<Java.Lang.Object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Registration? registration = null;

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (AndroidContext.CurrentActivity is not ComponentActivity activity)
            {
                throw new NativeCapabilityException(AndroidContext.CurrentActivity is null
                    ? AndroidContext.NoActivity(capability)
                    : NativeError.Unavailable(capability, NativeErrorCodes.HostNotReady,
                        "The current activity does not derive from AndroidX ComponentActivity, so it has no ActivityResultRegistry. " +
                        "Derive MainActivity from MauiAppCompatActivity or AndroidX.AppCompat.App.AppCompatActivity."));
            }

            registration = Registration.Launch(activity, capability, completion, contractFactory(), input);
        }).ConfigureAwait(false);

        try
        {
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (registration is not null)
            {
                await MainThread.InvokeOnMainThreadAsync(registration.Dispose).ConfigureAwait(false);
            }
        }
    }

    private sealed class Registration : IDisposable
    {
        private readonly Activity _activity;
        private readonly CapabilityId _capability;
        private readonly TaskCompletionSource<Java.Lang.Object?> _completion;
        private ActivityResultLauncher? _launcher;
        private ResultCallback? _callback;
        private int _disposed;

        private Registration(Activity activity, CapabilityId capability, TaskCompletionSource<Java.Lang.Object?> completion)
        {
            _activity = activity;
            _capability = capability;
            _completion = completion;
        }

        public static Registration Launch(
            ComponentActivity activity,
            CapabilityId capability,
            TaskCompletionSource<Java.Lang.Object?> completion,
            ActivityResultContract contract,
            Java.Lang.Object input)
        {
            var registration = new Registration(activity, capability, completion);
            registration._callback = new ResultCallback(completion);
            var key = "bns.activity-result." + Guid.NewGuid().ToString("N");
            registration._launcher = activity.ActivityResultRegistry.Register(key, contract, registration._callback);
            Platform.ActivityStateChanged += registration.OnActivityStateChanged;
            try
            {
                registration._launcher.Launch(input);
            }
            catch
            {
                registration.Dispose();
                throw;
            }

            return registration;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Platform.ActivityStateChanged -= OnActivityStateChanged;
            if (_launcher is not null)
            {
                _launcher.Unregister();
                _launcher.Dispose();
            }

            _callback?.Detach();
        }

        private void OnActivityStateChanged(object? sender, ActivityStateChangedEventArgs e)
        {
            if (e.State == ActivityState.Destroyed && ReferenceEquals(e.Activity, _activity) && !_activity.IsChangingConfigurations)
            {
                _completion.TrySetException(new NativeCapabilityException(NativeError.Failed(_capability, NativeErrorCodes.HostNotReady,
                    "The activity was destroyed before the result was delivered.")));
            }
            else if (e.State == ActivityState.Destroyed && ReferenceEquals(e.Activity, _activity))
            {
                _completion.TrySetException(new NativeCapabilityException(NativeError.Failed(_capability, NativeErrorCodes.HostNotReady,
                    "The activity was recreated (configuration change) before the result was delivered. Handle configuration " +
                    "changes in MainActivity (MAUI's default ConfigurationChanges) to keep results.")));
            }
        }
    }

    private sealed class ResultCallback : Java.Lang.Object, IActivityResultCallback
    {
        private TaskCompletionSource<Java.Lang.Object?>? _completion;

        public ResultCallback(TaskCompletionSource<Java.Lang.Object?> completion) => _completion = completion;

        public void OnActivityResult(Java.Lang.Object? result) => Interlocked.Exchange(ref _completion, null)?.TrySetResult(result);

        public void Detach() => _completion = null;
    }
}
