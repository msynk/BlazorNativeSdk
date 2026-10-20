using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace BlazorNativeSdk.Samples.Maui;

/// <summary>The main page: a <see cref="BlazorWebView"/> rendering the shared sample router.</summary>
public sealed class MainPage : ContentPage
{
    /// <summary>Creates the page.</summary>
    public MainPage()
    {
        var webView = new BlazorWebView { HostPage = "wwwroot/index.html" };

        // MainLayout (from BlazorNativeSdk.Samples.Shared) contains <NativeActivationOutlet />, which replays deep links and
        // notification taps that arrived before the UI was ready.
        webView.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(Routes) });
        webView.RootComponents.Add(new RootComponent { Selector = "head::after", ComponentType = typeof(HeadOutlet) });
        Content = webView;
    }
}
