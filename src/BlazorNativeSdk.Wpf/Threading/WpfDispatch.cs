using System.Windows;
using System.Windows.Threading;

namespace BlazorNativeSdk.Wpf;

/// <summary>Marshals work to a WPF <see cref="Dispatcher"/>, running inline when already on its thread.</summary>
internal static class WpfDispatch
{
    /// <summary>The dispatcher of the running WPF application, or <see langword="null"/> when there is none.</summary>
    public static Dispatcher? ApplicationDispatcher => Application.Current?.Dispatcher;

    /// <summary>Runs <paramref name="action"/> on <paramref name="dispatcher"/> and completes when the returned task completes.</summary>
    /// <exception cref="NativeCapabilityException">The dispatcher has shut down (<see cref="NativeErrorCodes.HostNotReady"/>).</exception>
    public static Task InvokeAsync(Dispatcher dispatcher, Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(action);
        if (dispatcher.CheckAccess())
        {
            return action();
        }

        return dispatcher.HasShutdownStarted ? Task.FromException(ShutDown()) : InvokeCoreAsync(dispatcher, action);
    }

    /// <summary>Runs <paramref name="func"/> on <paramref name="dispatcher"/> and returns its value.</summary>
    /// <exception cref="NativeCapabilityException">The dispatcher has shut down (<see cref="NativeErrorCodes.HostNotReady"/>).</exception>
    public static Task<T> InvokeAsync<T>(Dispatcher dispatcher, Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(func);
        if (dispatcher.CheckAccess())
        {
            try
            {
                return Task.FromResult(func());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        return dispatcher.HasShutdownStarted ? Task.FromException<T>(ShutDown()) : InvokeCoreAsync(dispatcher, func);
    }

    /// <summary>Queues <paramref name="action"/> on <paramref name="dispatcher"/> without waiting; ignored after shutdown.</summary>
    public static void Post(Dispatcher dispatcher, Action action)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(action);
        if (!dispatcher.HasShutdownStarted)
        {
            _ = dispatcher.BeginInvoke(action);
        }
    }

    /// <summary>Runs <paramref name="action"/> inline when on the dispatcher thread, otherwise queues it; ignored after shutdown.</summary>
    public static void RunOrPost(Dispatcher dispatcher, Action action)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            Post(dispatcher, action);
        }
    }

    private static async Task InvokeCoreAsync(Dispatcher dispatcher, Func<Task> action)
    {
        var operation = dispatcher.InvokeAsync(action);
        Task inner;
        try
        {
            inner = await operation.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operation.Status == DispatcherOperationStatus.Aborted)
        {
            throw ShutDown();
        }

        await inner.ConfigureAwait(false);
    }

    private static async Task<T> InvokeCoreAsync<T>(Dispatcher dispatcher, Func<T> func)
    {
        var operation = dispatcher.InvokeAsync(func);
        try
        {
            return await operation.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operation.Status == DispatcherOperationStatus.Aborted)
        {
            throw ShutDown();
        }
    }

    private static NativeCapabilityException ShutDown() =>
        new(NativeError.Unavailable(NativeCapabilities.Windows, NativeErrorCodes.HostNotReady, "The WPF dispatcher has shut down."));
}
