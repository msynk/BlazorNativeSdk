using System.IO;
using System.Windows.Threading;
using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BlazorNativeSdk.Wpf.Tests;

/// <summary>Builds service providers with the WPF integration and no machine-wide side effects.</summary>
internal sealed class TestHost : IDisposable
{
    public TestHost() => Directory.CreateDirectory(TempDirectory);

    public string TempDirectory { get; } = Path.Combine(Path.GetTempPath(), "bns-wpf-tests", Guid.NewGuid().ToString("N"));

    public ServiceProvider Build(Action<WpfNativeOptions>? configure = null, string? appId = null, Action<INativeSdkBuilder>? sdk = null)
    {
        var services = new ServiceCollection();
        services.AddBlazorNativeSdk(builder =>
        {
            builder.Configure(o =>
            {
                o.AppId = appId ?? "BlazorNativeSdk.Wpf.Tests";
                o.DeepLinks.Schemes.Add("bnstest");
            });
            builder.AddWpf(o =>
            {
                o.ConfigureWindowsAdapter(w =>
                {
                    w.RegisterAppUserModelId = false;
                    w.SetProcessAppUserModelId = false;
                    w.SecureStorageDirectory = Path.Combine(TempDirectory, "secure");
                });
                o.ConfigureDesktopAdapter(d => d.SingleInstanceLockDirectory = Path.Combine(TempDirectory, "locks"));
                configure?.Invoke(o);
            });
            sdk?.Invoke(builder);
        });

        // Keep state (window placement, work queues) in memory.
        services.Replace(ServiceDescriptor.Singleton<INativeStateStore>(new InMemoryStateStore()));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(TempDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>A dedicated STA thread running a WPF dispatcher loop.</summary>
internal sealed class StaDispatcherThread : IAsyncDisposable
{
    private readonly Thread _thread;

    private StaDispatcherThread(Thread thread, Dispatcher dispatcher)
    {
        _thread = thread;
        Dispatcher = dispatcher;
    }

    public Dispatcher Dispatcher { get; }

    public static async Task<StaDispatcherThread> StartAsync()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WPF test dispatcher",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return new StaDispatcherThread(thread, await ready.Task);
    }

    public Task<T> InvokeAsync<T>(Func<T> func) => Dispatcher.InvokeAsync(func).Task;

    public Task InvokeAsync(Action action) => Dispatcher.InvokeAsync(action).Task;

    /// <summary>Waits until the dispatcher processed everything queued so far, including posted window messages.</summary>
    public Task IdleAsync() => Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle).Task;

    public ValueTask DisposeAsync()
    {
        Dispatcher.InvokeShutdown();
        _ = _thread.Join(TimeSpan.FromSeconds(5));
        return ValueTask.CompletedTask;
    }
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, CancellationToken cancellationToken, int timeoutMs = 5000)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    public static async Task UntilAsync(Func<Task<bool>> condition, CancellationToken cancellationToken, int timeoutMs = 5000)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        while (!await condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}

internal static class BoundsAssert
{
    public static void Close(WindowBounds expected, WindowBounds actual, double tolerance = 1.0)
    {
        Assert.True(
            Math.Abs(expected.X - actual.X) <= tolerance && Math.Abs(expected.Y - actual.Y) <= tolerance &&
            Math.Abs(expected.Width - actual.Width) <= tolerance && Math.Abs(expected.Height - actual.Height) <= tolerance,
            $"Expected {expected}, got {actual}.");
    }
}
