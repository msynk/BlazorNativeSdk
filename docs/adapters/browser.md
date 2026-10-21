# Browser adapter (`BlazorNativeSdk.Browser`)

Provides BlazorNativeSdk capabilities through web platform APIs, for Blazor WebAssembly and Blazor Server /
interactive server rendering. Everything runs in the **user's browser** through one lazily imported ES module
(`_content/BlazorNativeSdk.Browser/blazor-native-sdk.js`); no third-party scripts.

## Setup

```csharp
builder.Services.AddBlazorNativeSdk(sdk => sdk
    .AddBrowser(o =>
    {
        o.EnableBestEffortSecureStorage = false;   // default; see "Secure storage"
        o.OverlayText.Capture = "Take photo";      // localize the camera/scanner overlay
    })
    .ConfigureDeepLinks(d => d.Schemes.Add("web+myapp")));
```

```razor
@* MainLayout.razor *@
<NativeActivationOutlet />
<BrowserNativeSdkInitializer />   @* required for Blazor Server, optional for WebAssembly *@
```

`BrowserNativeOptions`: `EnableBestEffortSecureStorage`, `SecureStorageDatabaseName`, `ProtocolHandlerQueryParameter`
(default `bns-link`), `UseFileSystemAccessApi`, `UseServiceWorkerNotifications`, `ModulePath`, `InteropTimeout`
(short calls only; UI operations are never timed out), `OverlayText`.

Inject `BrowserProtocolHandlers` and call `RegisterAsync("web+myapp")` from a click handler to register a protocol
handler (`navigator.registerProtocolHandler('web+myapp', '/?bns-link=%s')`).

## Capability matrix

C = Chromium (Chrome, Edge, Opera, Samsung Internet), F = Firefox, S = Safari/WebKit (all iOS browsers).

| Capability | C | F | S | Implementation and notes |
| --- | --- | --- | --- | --- |
| Camera: photo | ✔ | ✔ | ✔ | `getUserMedia` + built-in overlay (preview, shutter, cancel, switch camera; Esc cancels), JPEG via `canvas.toBlob` honoring `MaxDimension`/`Quality`. Without `getUserMedia` (insecure context): `<input capture>`. |
| Camera: video (`video`) | ✔ | ✔ | ✔ 14.1+ | `MediaRecorder` in the overlay (WebM or MP4, whatever the browser records). `MaxDuration` honored. Falls back to `<input capture accept=video/*>`. |
| Camera: library | ✔ | ✔ | ✔ | File input filtered by MIME type. `SelectionLimit` is enforced after selection (browsers cannot limit it). |
| Barcode | Android, ChromeOS, macOS | ✘ | ✘ | `BarcodeDetector` (Shape Detection API). **Unsupported** on Firefox, Safari and Chromium on Windows/Linux: the API does not exist there. Register your own `IBarcodeScanner` (e.g. a JS/WASM decoder) to cover them. |
| Files: open | ✔ `showOpenFilePicker` | ✔ input | ✔ input | Cancel is detected with the input `cancel` event, or a focus-return heuristic in old browsers. |
| Files: save | ✔ `save-dialog` | `download` | `download` | `showSaveFilePicker` streams from .NET after the user picked the location; elsewhere an anchor download (`FileSaveMethod.Download`, final location unknown). |
| Files: folder | ✘ | ✘ | ✘ | **Unsupported**: directory handles are sandboxed, not addressable paths. |
| Secure storage | opt-in | opt-in | opt-in | **Unsupported by default**: browsers cannot protect secrets from scripts in the page. Opt-in: AES-GCM 256, non-extractable WebCrypto key, random 96-bit IV per value, key name bound as AAD, all in IndexedDB (`Protection = BestEffort`). Needs a secure context. |
| Share | Android, ChromeOS, Windows | Android only | ✔ | `navigator.share`; `files` only when `canShare({files})`. Dismissal → `Cancelled`; no user activation → `Unavailable` / `browser.user-gesture-required`. |
| Clipboard | ✔ | ✔ (paste prompt) | ✔ (paste prompt) | `readText`/`writeText`, write fallback `execCommand('copy')`. No change notification. |
| Permissions | ✔ | ✔ | ✔ 16+ | `camera`, `microphone`, `notifications`, `clipboard-read`; other names → `Unsupported`. Requests use `getUserMedia` / `Notification.requestPermission`. `OpenAppSettingsAsync` → `Unsupported`. |
| Notifications | ✔ | ✔ | ✔ (iOS: home-screen apps only) | Shown through the service worker registration when one is active (enables `actions`), else `new Notification()`. Chrome on Android requires a service worker. `ScheduleAsync` → **Unsupported** (no local scheduling API on the web; use Web Push). Channels: successful no-op. Badges via the Badging API where available. |
| Lifecycle | ✔ | ✔ | ✔ | `visibilitychange`, `focus`/`blur`, `pagehide` (Terminating), `freeze`/`resume`, `pageshow` (bfcache). `BackgroundExecutionGuarantee.BestEffort`. |
| Connectivity | ✔ | onLine only | onLine only | `navigator.onLine` + events; type and Save-Data from `navigator.connection` (Chromium only). Online means "has a network", not "server reachable". |
| Deep links | ✔ | ✔ | partial | Protocol handlers (`?bns-link=`, not in Safari/mobile) and PWA `launchQueue` (Chromium) become `DeepLink` activations; the first one is `IsColdStart`. |
| State store | WASM | WASM | WASM | `localStorage` with prefix `bns:` (WebAssembly only). |
| Biometrics | ✘ | ✘ | ✘ | **Unsupported** with an explicit reason: WebAuthn/passkeys need server-side verification; local-only checks are impossible. |
| App storage | ✘ | ✘ | ✘ | **Unsupported**: no addressable file system. |

Every `GetStatusAsync` is prompt-free and reports `RequiresPermission`, `PermissionDenied` or `Unavailable` (for example
`browser.insecure-context`) honestly, with the optional features listed above.

## Error mapping

Browser failures come back as structured `{ ok:false, name, message }` values and are mapped deterministically:
`AbortError` → Cancelled; `NotAllowedError` → PermissionDenied (or Unavailable `browser.user-gesture-required` when the
page had no user activation); `SecurityError` → PermissionDenied/`permission.restricted` (permissions policy, frames);
`NotFoundError` → Unavailable `hardware.unavailable` for media, else `operation.not-found`; `NotReadableError` and
`OverconstrainedError` → Unavailable `hardware.unavailable`; `NotSupportedError` → Unsupported; `InvalidStateError` →
Failed `operation.conflict`; `TypeError`/`DataError` → Failed `operation.invalid-argument`; `QuotaExceededError` →
`operation.too-large`; `TimeoutError` → `operation.timeout`. Each error carries `Details["dom-exception"]`.
Cancelling the caller's token throws `OperationCanceledException` and closes any overlay in the browser.

## Blazor Server vs WebAssembly

| | WebAssembly | Server / interactive server |
| --- | --- | --- |
| Service lifetime | Singleton | Scoped (one set per circuit, because `IJSRuntime` is per circuit) |
| Where capabilities run | the browser | the **user's** browser, over the circuit |
| Activation pipeline | Core singleton | made per circuit by `AddBrowser()`, so a tap or link in one user's browser can never navigate another user's session |
| Listener startup | SDK startup task (or `BrowserNativeSdkInitializer`) | `BrowserNativeSdkInitializer` per circuit; lifecycle/connectivity also start lazily on first use |
| `INativeStateStore` | `localStorage` | unchanged (server-side store; the SDK's own state belongs to the server process) |
| Files | Blob stays in the browser until read | content is streamed over the circuit when read (`OpenReadAsync`), so mind SignalR bandwidth and limits |
| Background work, transfers | in-process scheduler while the page is open; transfers `Unsupported` | in-process scheduler and `HttpTransferService` on the server, one per process. They observe the **server's** connectivity and lifecycle, which `AddBrowser()` registers under `NativeServiceKeys.Process`, not a circuit's browser |
| `IActivationHandler` | resolved from a new scope | resolved from a new scope, **not** the circuit's: they cannot use per-circuit services (JS interop, the circuit's `NavigationManager`). Subscribe through `IAppActivationService` in a component for circuit-specific handling |

`BrowserNativeSdkInitializer` can live in any interactive root, for example a server-only component that wraps a shared
`Routes` (see `samples/BlazorNativeSdk.Samples.Server`).

During prerendering or static rendering no browser is attached: statuses are `Unknown` and operations return
`Unavailable` / `host.not-ready`; the module import is retried on the next interactive call. Call capabilities from
`OnAfterRenderAsync` or event handlers. APIs that need transient user activation (share, file pickers, clipboard,
protocol handlers) must be called directly from a click handler; with Blazor Server the round trip normally stays within
the browser's activation window (about 5 s in Chromium) but slow connections can exceed it.

## Service worker

Notification actions and taps after the page was closed need a service worker that imports the snippet:

```js
// wwwroot/service-worker.js
importScripts('./_content/BlazorNativeSdk.Browser/blazor-native-sdk-sw.js');
```

On `notificationclick` the snippet ignores notifications not created by the SDK, focuses an open app window and posts
`{ type: 'bns-notification-click', id, action, route, data }` to it; with no window open it opens the notification route
(same-origin, inside the worker scope only) with the tap encoded in `?bns-notification-click=`, which the adapter
publishes as a cold-start `Notification` activation and then removes from the address bar.

* Register the worker before Blazor starts (see `samples/BlazorNativeSdk.Samples.Wasm/wwwroot/index.html`). On the first
  visit the worker may still be installing; the adapter waits up to 3 seconds for it to activate before showing a
  notification, and up to half a second when it reports status.
* Browsers have no equivalent of `NotificationAction.OpensApp = false` or `AcceptsText`: every action click focuses or
  opens a window of the app, and actions never collect text. The `actions` feature is reported only when a service worker
  is active and `Notification.maxActions` is greater than zero (Chromium shows at most two).

## Guarantees and limitations

- No background execution: hidden tabs are throttled, frozen or discarded at the browser's discretion; `pagehide` and
  `freeze` are best effort and may not reach a Blazor Server circuit.
- Secure storage (when enabled) protects against casual inspection and copying of storage files, not against XSS.
- Media tracks are always stopped and overlay DOM removed on capture, cancel, error, disposal or caller cancellation.
  The overlay is keyboard accessible (focus trap, Esc, labelled buttons), restores focus, injects its CSS once
  (`bns-sdk-` prefix) and honors `prefers-reduced-motion`.
- Logs contain operation names and error codes only, never clipboard text, secrets or file contents.
