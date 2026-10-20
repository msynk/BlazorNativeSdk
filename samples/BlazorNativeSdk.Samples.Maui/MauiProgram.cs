namespace BlazorNativeSdk.Samples.Maui;

/// <summary>Builds the MAUI app.</summary>
public static class MauiProgram
{
    /// <summary>Creates the app: Blazor Hybrid plus BlazorNativeSdk with the configuration shared by every sample host.</summary>
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

        // One call: the platform adapter for this target, lifecycle and deep-link wiring, SDK start-up at launch and, on
        // Windows and Mac Catalyst, window management.
        builder.UseBlazorNativeSdk(
            sdk =>
            {
                sdk.AddSampleConfiguration();
#if WINDOWS
                // Toasts activate through the sample's URL scheme, so clicks also reach the app after it exited.
                sdk.AddWindows(o => o.NotificationActivationScheme = SampleServices.Scheme);
#endif
            },
            options =>
            {
                // Windows opened with WindowOptions.Route render the shared router (the Desktop page opens "/").
                options.DefaultRootComponent = typeof(Routes);

                // Windows, unpackaged: register bnssample:// for this executable on startup (only when missing).
                options.RegisterProtocolSchemes = true;
            });

        return builder.Build();
    }
}
