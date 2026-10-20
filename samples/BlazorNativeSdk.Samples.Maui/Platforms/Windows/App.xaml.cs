namespace BlazorNativeSdk.Samples.Maui.WinUI;

/// <summary>
/// The WinUI application. Building the MAUI app enforces the single instance (a second launch forwards its arguments, such as a
/// bnssample:// link, and exits before a window appears) and publishes the launch activation.
/// </summary>
public partial class App : MauiWinUIApplication
{
    /// <summary>Initializes the application object.</summary>
    public App()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
