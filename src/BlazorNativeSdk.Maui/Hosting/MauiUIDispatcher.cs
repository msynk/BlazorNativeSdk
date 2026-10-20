using Microsoft.Maui.Dispatching;
using MauiApplication = Microsoft.Maui.Controls.Application;

namespace BlazorNativeSdk.Maui;

/// <summary>
/// Marshals work to the MAUI UI thread. The application dispatcher is captured on the UI thread when the MAUI app is built
/// (every MAUI platform builds the app on its main thread); later it falls back to the dispatcher of
/// <see cref="MauiApplication.Current"/>. Work runs inline when the caller is already on the UI thread.
/// </summary>
internal sealed class MauiUIDispatcher
{
    private IDispatcher? _dispatcher;

    /// <summary>Captures the dispatcher of the calling thread when it is a UI thread.</summary>
    public void CaptureCurrentThread()
    {
        if (Volatile.Read(ref _dispatcher) is null && Dispatcher.GetForCurrentThread() is { } dispatcher)
        {
            Interlocked.CompareExchange(ref _dispatcher, dispatcher, null);
        }
    }

    /// <summary><see langword="true"/> when a UI dispatcher is known.</summary>
    public bool IsAvailable => Current is not null;

    /// <summary><see langword="true"/> when the caller runs on the UI thread.</summary>
    public bool CheckAccess() => Current is { } dispatcher && !dispatcher.IsDispatchRequired;

    private IDispatcher? Current
    {
        get
        {
            var dispatcher = Volatile.Read(ref _dispatcher);
            if (dispatcher is null && MauiApplication.Current?.Dispatcher is { } fromApplication)
            {
                dispatcher = Interlocked.CompareExchange(ref _dispatcher, fromApplication, null) ?? fromApplication;
            }

            return dispatcher;
        }
    }

    /// <summary>Queues <paramref name="action"/> on the UI thread without waiting. Returns <see langword="false"/> when it could not be queued.</summary>
    public bool Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Current is { } dispatcher && dispatcher.Dispatch(action);
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread (inline when already there) and completes when its task completes.</summary>
    /// <exception cref="InvalidOperationException">No UI dispatcher is available, or the UI thread is shutting down.</exception>
    public Task InvokeAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var dispatcher = Current ?? throw NoDispatcher();
        if (!dispatcher.IsDispatchRequired)
        {
            return action();
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = dispatcher.Dispatch(async () =>
        {
            try
            {
                await action();
                completion.TrySetResult();
            }
            catch (OperationCanceledException ex)
            {
                completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return queued ? completion.Task : Task.FromException(ShuttingDown());
    }

    /// <summary>Runs <paramref name="func"/> on the UI thread (inline when already there) and returns its result.</summary>
    /// <exception cref="InvalidOperationException">No UI dispatcher is available, or the UI thread is shutting down.</exception>
    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        var dispatcher = Current ?? throw NoDispatcher();
        if (!dispatcher.IsDispatchRequired)
        {
            return Task.FromResult(func());
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = dispatcher.Dispatch(() =>
        {
            try
            {
                completion.TrySetResult(func());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return queued ? completion.Task : Task.FromException<T>(ShuttingDown());
    }

    private static InvalidOperationException NoDispatcher() =>
        new("The MAUI UI dispatcher is not available yet: no MAUI application has been created on a UI thread.");

    private static InvalidOperationException ShuttingDown() =>
        new("The MAUI UI dispatcher did not accept the work item; the UI thread is shutting down.");
}
