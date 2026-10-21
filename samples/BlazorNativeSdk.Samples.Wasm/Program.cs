using BlazorNativeSdk;
using BlazorNativeSdk.Samples;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Routes>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddBlazorNativeSdk(sdk => sdk
    // Notifications go through the service worker registered in index.html (wwwroot/service-worker.js) when it is active:
    // that enables action buttons and routes taps after the page was closed (?bns-notification-click=). This is the default.
    .AddBrowser(o => o.UseServiceWorkerNotifications = true)
    .AddInProcessBackgroundWork(o => o.RunWhileInBackground = false)
    .AddSampleConfiguration()
    .ConfigureDeepLinks(links => links.Schemes.Add("web+" + SampleServices.Scheme)));

await builder.Build().RunAsync();
