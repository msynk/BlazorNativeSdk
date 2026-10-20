using BlazorNativeSdk;
using BlazorNativeSdk.Samples;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Routes>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddBlazorNativeSdk(sdk => sdk
    .AddBrowser()
    .AddInProcessBackgroundWork(o => o.RunWhileInBackground = false)
    .AddSampleConfiguration()
    .ConfigureDeepLinks(links => links.Schemes.Add("web+" + SampleServices.Scheme)));

await builder.Build().RunAsync();
