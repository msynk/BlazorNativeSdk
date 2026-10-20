using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.Lifecycle;
using BlazorNativeSdk.Wpf;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorNativeSdk;

/// <summary>Start-up helper for WPF Blazor Hybrid apps.</summary>
public static class WpfNativeSdk
{
    /// <summary>
    /// Starts BlazorNativeSdk in a WPF app. Call it at the very start of <c>Application.OnStartup</c>, before creating any
    /// window, and shut down when it returns <see langword="false"/>:
    /// <code>
    /// if (!await WpfNativeSdk.StartAsync(Services, e.Args)) { Shutdown(); return; }
    /// </code>
    /// </summary>
    /// <param name="services">The app's service provider (with <c>AddBlazorNativeSdk(sdk =&gt; sdk.AddWpf())</c>).</param>
    /// <param name="args">The command-line arguments (<c>StartupEventArgs.Args</c>).</param>
    /// <param name="cancellationToken">Cancels start-up.</param>
    /// <returns>
    /// <see langword="true"/> when this process should continue (the primary instance, or single instance is disabled or
    /// unavailable); <see langword="false"/> when another instance is running and received the arguments.
    /// </returns>
    /// <remarks>
    /// <list type="number">
    /// <item>With <see cref="WpfNativeOptions.SingleInstance"/> (default), acquires <see cref="ISingleInstanceService"/>: a
    /// secondary instance forwards its arguments (deep links, files) to the running instance and gets <see langword="false"/>.
    /// The primary keeps the lease for the lifetime of the app and releases it on <c>Application.Exit</c>.</item>
    /// <item>Publishes the launch arguments as a cold-start activation (<see cref="ActivationArguments"/>), routed once a
    /// <c>NativeActivationOutlet</c> renders (subject to <see cref="DesktopNativeOptions.PublishLaunchActivation"/>).</item>
    /// <item>With <see cref="WindowsPlatform.WindowsNativeOptions.SetProcessAppUserModelId"/> (default), initializes the
    /// notification identity so the process AppUserModelID is set before the first window (it decides taskbar grouping).</item>
    /// <item>With <see cref="WpfNativeOptions.RegisterProtocolSchemes"/>, registers the app's URL schemes in the background
    /// (unpackaged apps).</item>
    /// <item>Attaches the lifecycle to the WPF application, starts loading the main window's placement and starts the SDK
    /// runtime (<see cref="NativeSdkRuntime"/>).</item>
    /// </list>
    /// Calling it again returns the same task. On <c>Application.Exit</c> pending window placements are saved.
    /// </remarks>
    /// <exception cref="InvalidOperationException"><c>AddWpf()</c> was not called on the service collection.</exception>
    public static Task<bool> StartAsync(IServiceProvider services, string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(args);
        var coordinator = services.GetService<WpfStartupCoordinator>()
            ?? throw new InvalidOperationException("The WPF integration is not registered. Call services.AddBlazorNativeSdk(sdk => sdk.AddWpf()) first.");
        return coordinator.StartAsync(args, cancellationToken);
    }
}
