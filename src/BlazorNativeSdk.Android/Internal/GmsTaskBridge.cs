using Android.Gms.Tasks;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>How a Google Play services <see cref="Android.Gms.Tasks.Task"/> completed.</summary>
internal enum GmsTaskStatus
{
    Succeeded,
    Canceled,
    Failed,
}

/// <summary>The outcome of a Google Play services task.</summary>
/// <param name="Status">How the task completed.</param>
/// <param name="Result">The result on success.</param>
/// <param name="Error">The failure on <see cref="GmsTaskStatus.Failed"/>.</param>
internal readonly record struct GmsTaskOutcome(GmsTaskStatus Status, Java.Lang.Object? Result, Java.Lang.Exception? Error);

/// <summary>Bridges Google Play services task callbacks to a .NET <see cref="System.Threading.Tasks.Task"/>.</summary>
internal static class GmsTaskBridge
{
    /// <summary>Attaches success, cancellation and failure listeners and returns a task that completes with the outcome.</summary>
    public static System.Threading.Tasks.Task<GmsTaskOutcome> AsTask(Android.Gms.Tasks.Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var listener = new Listener();
        task.AddOnSuccessListener(listener);
        task.AddOnCanceledListener(listener);
        task.AddOnFailureListener(listener);
        return listener.Completion;
    }

    private sealed class Listener : Java.Lang.Object, IOnSuccessListener, IOnCanceledListener, IOnFailureListener
    {
        private readonly TaskCompletionSource<GmsTaskOutcome> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public System.Threading.Tasks.Task<GmsTaskOutcome> Completion => _completion.Task;

        public void OnSuccess(Java.Lang.Object? result) => _completion.TrySetResult(new GmsTaskOutcome(GmsTaskStatus.Succeeded, result, null));

        public void OnCanceled() => _completion.TrySetResult(new GmsTaskOutcome(GmsTaskStatus.Canceled, null, null));

        public void OnFailure(Java.Lang.Exception e) => _completion.TrySetResult(new GmsTaskOutcome(GmsTaskStatus.Failed, null, e));
    }
}
