using System.Windows;

namespace BlazorNativeSdk.Samples.Wpf;

/// <summary>The main window: hosts the shared Blazor UI.</summary>
public partial class MainWindow : Window
{
    /// <summary>Creates the window.</summary>
    public MainWindow()
    {
        InitializeComponent();
        WebView.Services = App.Services;

        // Adopt this window as the SDK's main window: it parents pickers, the share UI and Windows Hello, drives the app
        // lifecycle, receives focus for forwarded deep links and reopens where the user left it.
        _ = this.AttachBlazorNativeSdk(App.Services).GetValueOrThrow();
    }
}
