using Android.App;
using Android.Runtime;

namespace BlazorNativeSdk.Samples.Maui;

/// <summary>
/// The Android application. Building the MAUI app here also starts BlazorNativeSdk, so WorkManager workers and notification
/// receivers find the services when Android starts the process in the background.
/// </summary>
[Application]
public class MainApplication : MauiApplication
{
    /// <summary>Creates the application (called by the Android runtime).</summary>
    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    /// <inheritdoc />
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
