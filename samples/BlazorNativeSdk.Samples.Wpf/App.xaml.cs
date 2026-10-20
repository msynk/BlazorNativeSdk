using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorNativeSdk.Samples.Wpf;

/// <summary>The WPF Blazor Hybrid host of the BlazorNativeSdk samples.</summary>
public partial class App : Application
{
    private ServiceProvider? _services;

    /// <summary>The application's services.</summary>
    public static IServiceProvider Services =>
        (Current as App)?._services ?? throw new InvalidOperationException("The services are created in App.OnStartup.");

    /// <inheritdoc />
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        services.AddBlazorNativeSdk(sdk => sdk
            .AddWpf(o =>
            {
                // Windows opened with WindowOptions.Route render the shared router.
                o.DefaultRootComponent = typeof(Routes);

                // Unpackaged: register bnssample:// for this executable so links and toast clicks launch (or reach) the app.
                o.RegisterProtocolSchemes = true;
                o.ConfigureWindowsAdapter(w => w.NotificationActivationScheme = SampleServices.Scheme);
            })
            .AddSampleConfiguration());
        _services = services.BuildServiceProvider();

        // Single instance: a second launch (for example bnssample://orders/7 from a browser) forwards its arguments to this
        // process, which routes them to /orders/7, and exits.
        if (!await WpfNativeSdk.StartAsync(_services, e.Args))
        {
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
