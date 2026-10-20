using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.Lifecycle;
using BlazorNativeSdk.WindowsPlatform;
using BlazorNativeSdk.Wpf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk;

/// <summary>Registers the WPF host integration.</summary>
public static class WpfNativeSdkBuilderExtensions
{
    /// <summary>
    /// Declares the WPF host and adds everything a WPF Blazor Hybrid app needs: the desktop services (<c>AddDesktop()</c>:
    /// IPC, single instance, shell file operations, protocol registration, file state store, background work, transfers),
    /// the Windows adapter (<c>AddWindows()</c>), and the WPF integration: <see cref="WpfHostBridge"/> (main window and
    /// dispatcher for pickers, share UI, Windows Hello and menus), <see cref="WpfWindowManager"/> (<see cref="IWindowManager"/>)
    /// and <see cref="WpfAppLifecycle"/> (<see cref="IAppLifecycle"/>). Also calls <c>AddWpfBlazorWebView()</c>, which the
    /// windows opened by the window manager require.
    /// </summary>
    /// <param name="builder">The SDK builder.</param>
    /// <param name="configure">
    /// Configures <see cref="WpfNativeOptions"/>; use <see cref="WpfNativeOptions.ConfigureWindowsAdapter"/> and
    /// <see cref="WpfNativeOptions.ConfigureDesktopAdapter"/> for the adapters' options.
    /// </param>
    /// <remarks>
    /// Call <see cref="WpfNativeSdk.StartAsync"/> at startup (single instance, launch activation) and attach the main window
    /// with <see cref="WpfWindowExtensions.AttachBlazorNativeSdk"/>. Capabilities registered after this call override the
    /// ones it registers.
    /// </remarks>
    public static INativeSdkBuilder AddWpf(this INativeSdkBuilder builder, Action<WpfNativeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        var options = services.AddOptions<WpfNativeOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        builder.SetHost(WpfAdapter.CreateHostInfo());

        // Host-neutral desktop services first, so the Windows adapter replaces the generic ones it implements natively
        // (app storage, connectivity).
        builder.AddDesktop();
        services.AddOptions<DesktopNativeOptions>().Configure<IOptions<WpfNativeOptions>>(static (desktop, wpf) => wpf.Value.ApplyTo(desktop));
        builder.AddWindows();
        services.AddOptions<WindowsNativeOptions>().Configure<IOptions<WpfNativeOptions>>(static (windows, wpf) => wpf.Value.ApplyTo(windows));

        // Required by the BlazorWebView of every window the window manager opens (idempotent).
        services.AddWpfBlazorWebView();

        builder.AddCapability<IWindowManager, WpfWindowManager>(NativeCapabilities.Windows);
        builder.AddCapability<IAppLifecycle, WpfAppLifecycle>(NativeCapabilities.Lifecycle);
        services.Replace(ServiceDescriptor.Singleton<IWindowsHostBridge, WpfHostBridge>());
        services.TryAddSingleton<WpfStartupCoordinator>();
        builder.AddActivationHandler<WpfActivationFocusHandler>();
        return builder;
    }
}
