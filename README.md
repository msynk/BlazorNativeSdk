# BlazorNativeSdk

**One API for device, desktop and lifecycle features in every Blazor host. When a host can't do something, the API
tells you so instead of hiding it.**

BlazorNativeSdk is a modular device-capability SDK for Blazor, comparable to Capacitor plugins, React Native native
modules or Flutter plugins. The same component code runs in:

- the browser (Blazor WebAssembly, and Blazor Server / interactive server rendering)
- .NET MAUI Blazor Hybrid on Android, iOS, Mac Catalyst and Windows
- WPF Blazor Hybrid (and other Windows desktop hosts)

Each platform is an **independent adapter package**. A WebAssembly app never downloads Android code, and each adapter
can be tested on its own.

```razor
@inject IBarcodeScanner Scanner

<CapabilityView Capability="NativeCapabilities.Barcode">
    <Available><button @onclick="ScanAsync">Scan ticket</button></Available>
    <NotAvailable Context="status">Scanning isn't available here: @status.Reason</NotAvailable>
</CapabilityView>

@code {
    async Task ScanAsync()
    {
        var result = await Scanner.ScanAsync(new() { Formats = BarcodeFormats.QrCode });
        switch (result.Status)
        {
            case NativeResultStatus.Success: Open(result.Value.RawValue); break;
            case NativeResultStatus.Cancelled: break;
            case NativeResultStatus.PermissionDenied: ShowSettingsHint(); break;
            default: ShowError(result.Error!.Message); break;
        }
    }
}
```

## Why it's different

- **Honest results.** Every call returns `NativeResult` with one of these statuses: `Success`, `Unsupported`,
  `PermissionDenied`, `Cancelled`, `Unavailable` (with a precise code such as `biometric.not-enrolled` or
  `browser.user-gesture-required`) or `Failed`. Platform differences are not hidden behind silent no-ops.
- **Runtime capability detection.** `ICapabilityRegistry` and `<CapabilityView>` report what works *before* you call it,
  including optional features such as video capture, save dialogs or notification actions.
- **Cold-start-safe deep links and notification routing.** Activations are queued until the UI is ready, matched against
  allow-listed schemes and hosts, and then routed to handlers or navigated to.
- **Documented background guarantees.** Every scheduler and transfer adapter publishes a `BackgroundExecutionPolicy`
  instead of promising unrestricted background execution.
- **Modular and testable.** You install only the adapters you need. `BlazorNativeSdk.Testing` provides fakes for every
  capability and contract checks that adapters must pass.
- **Trimming- and AOT-friendly.** No reflection-based JSON. The `net10.0` libraries are marked AOT-compatible.

## Packages

| Package | Use it for |
| --- | --- |
| `BlazorNativeSdk.Abstractions` | Contracts only. Reference it from libraries that consume capabilities. |
| `BlazorNativeSdk.Core` | Runtime: DI builder, registry, fallbacks, activation routing, scheduler, transfers, checkpoints, `<CapabilityView>`, `<NativeActivationOutlet>`. |
| `BlazorNativeSdk.Browser` | Blazor WebAssembly and Server: camera, barcode (Shape Detection API), files, share, clipboard, notifications, permissions, lifecycle, connectivity. |
| `BlazorNativeSdk.Android` | Android: MediaPicker, Google Code Scanner, BiometricPrompt, notifications with exact and inexact alarms, WorkManager, App Links. |
| `BlazorNativeSdk.iOS` | iOS and Mac Catalyst: VisionKit and AVFoundation scanning, LocalAuthentication, Keychain, UNUserNotificationCenter, BGTaskScheduler, Universal Links. |
| `BlazorNativeSdk.Windows` | Win32 and WinRT for WPF, WinForms and MAUI on Windows: Windows Hello, DPAPI, toasts, pickers, share, tray, global hotkeys, menus, displays. |
| `BlazorNativeSdk.Desktop` | Host-neutral desktop services: authenticated IPC, single instance with argument forwarding, shell file operations, protocol registration. |
| `BlazorNativeSdk.Maui` | .NET MAUI integration: one call wires lifecycle events, deep links and multi-window support. |
| `BlazorNativeSdk.Wpf` | WPF integration: windows that host Blazor, lifecycle, and the main-window bridge. |
| `BlazorNativeSdk.Testing` | Fakes and contract checks. |

## Getting started

### Blazor WebAssembly

```csharp
builder.Services.AddBlazorNativeSdk(sdk => sdk
    .AddBrowser()
    .AddInProcessBackgroundWork(o => o.RunWhileInBackground = false)
    .ConfigureDeepLinks(links => links.Schemes.Add("web+myapp")));
```

### .NET MAUI Blazor Hybrid

```csharp
builder.UseBlazorNativeSdk(sdk => sdk
    .ConfigureDeepLinks(links =>
    {
        links.Schemes.Add("myapp");          // myapp://orders/42  ->  /orders/42
        links.Hosts.Add("contoso.com");      // https://contoso.com/orders/42 (App Links / Universal Links)
    })
    .AddBackgroundWorker<SyncWorker>("sync"));
```

### WPF Blazor Hybrid

```csharp
services.AddWpfBlazorWebView();
services.AddBlazorNativeSdk(sdk => sdk.AddWpf().AddDesktop());
```

Then add the outlet once to your layout, so deep links and notification taps received during startup are replayed:

```razor
<NativeActivationOutlet />
```

To handle activations without any UI, register an `IActivationHandler`. It also covers a notification action button that
doesn't open the app and arrives in a process the OS started just for it:

```csharp
sdk.AddActivationHandler<ArchiveMessageHandler>();

sealed class ArchiveMessageHandler(IMessageStore store) : IActivationHandler
{
    public async ValueTask<bool> HandleAsync(AppActivation activation, CancellationToken ct)
    {
        if (activation.ActionId != "archive") return false;
        await store.ArchiveAsync(activation.Data["messageId"], ct);
        return true;
    }
}
```

## Capability overview

| Capability | Browser | Android | iOS | Windows | Desktop (any OS) |
| --- | --- | --- | --- | --- | --- |
| Camera capture / media pick | ✅ getUserMedia overlay / ✅ | ✅ / ✅ | ✅ / ✅ | ⛔ capture (no system UI) / ✅ | |
| Barcode scan / image detect | ✅ Chromium only / ✅ Chromium only | ✅ Code Scanner / ⛔ | ✅ / ✅ Vision | ⛔ (plug in ZXing) | |
| Files open / save / folder | ✅ / ✅ dialog or download / ⛔ | ✅ / ✅ SAF / ✅ | ✅ / ✅ / ✅ | ✅ / ✅ / ✅ | |
| Secure storage | opt-in, best effort | Keystore | Keychain | DPAPI | |
| Share sheet | ✅ (user gesture) | ✅ | ✅ with completion | ✅ | |
| Biometrics | ⛔ (use passkeys) | ✅ | ✅ | ✅ Windows Hello | |
| Clipboard | ✅ | ✅ + change events | ✅ + change events | ✅ + change events | |
| Local notifications | ✅ immediate | ✅ + scheduled, actions, reply | ✅ + scheduled, actions, reply | ✅ toasts + scheduled | |
| Deep links | protocol handler, PWA launch | intents / App Links | URL types / Universal Links | protocol + single instance | ✅ forwarding |
| Background work | in-process (foreground) | WorkManager | BGTaskScheduler | in-process | in-process |
| Windows, tray, hotkeys, menus | ⛔ | ⛔ | ⛔ | ✅ | IPC, single instance, shell ops |

✅ supported · ⛔ reports `Unsupported` with a reason. See [docs/adapters](docs/adapters) for every detail and caveat.

## Documentation

- [Architecture](docs/architecture.md)
- [Background execution: guarantees and limits](docs/background-execution.md)
- [Security model](docs/security.md)
- [Writing an adapter](docs/adapter-authoring.md)
- Adapter guides: [Browser](docs/adapters/browser.md) · [Android](docs/adapters/android.md) · [iOS](docs/adapters/ios.md) ·
  [Windows](docs/adapters/windows.md) · [Desktop](docs/adapters/desktop.md) · [MAUI](docs/adapters/maui.md) · [WPF](docs/adapters/wpf.md)

## Building

Requirements: .NET SDK 10.0.400 or later. To build the mobile adapters you also need the `android`, `ios` and
`maccatalyst` workloads. To run the Windows adapter tests you need Windows.

```shell
dotnet build BlazorNativeSdk.slnx
dotnet test --project tests/BlazorNativeSdk.Core.Tests
```

## License

MIT
