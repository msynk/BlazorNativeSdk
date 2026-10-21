using BlazorNativeSdk;
using BlazorNativeSdk.Samples;
using BlazorNativeSdk.Samples.Server.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Blazor Server: .NET runs here, but every capability runs in the *user's* browser over the circuit. AddBrowser() therefore
// registers the browser services per circuit (scoped) and replaces Core's application-wide activation pipeline with one per
// circuit, so a notification tap or deep link in one user's browser can only navigate that user's session. Each circuit's
// listeners are started by <BrowserNativeSdkInitializer /> (Components/SampleRoot.razor).
//
// The in-process work scheduler is one per server process. It observes the server's connectivity and lifecycle (registered
// by AddBrowser() under NativeServiceKeys.Process), not a circuit's browser, and its workers run on the server. Also note
// that the sample's ActivityLog and the SDK's state store (queued work, workflow checkpoints) are one per process on a
// server, shared by every circuit; a real multi-user app keys such state by user.
//
// Try the per-circuit pipeline without a notification: open /?bns-link=web%2Bbnssample%3A%2F%2Forders%2F7 and the circuit
// publishes a cold-start deep link that is routed to /orders/7.
builder.Services.AddBlazorNativeSdk(sdk => sdk
    .AddBrowser()
    .AddInProcessBackgroundWork()
    .AddSampleConfiguration()
    .ConfigureDeepLinks(links => links.Schemes.Add("web+" + SampleServices.Scheme)));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    // The pages (/devices, /orders/{id}, ...) live in the shared Razor class library.
    .AddAdditionalAssemblies(typeof(Routes).Assembly);

await app.RunAsync();
