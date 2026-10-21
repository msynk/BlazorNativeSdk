# iOS and Mac Catalyst adapter (`BlazorNativeSdk.iOS`)

Targets `net10.0-ios` and `net10.0-maccatalyst` (minimum iOS / Mac Catalyst 15.0). The root namespace is
`BlazorNativeSdk.ApplePlatform`. `AddApple()` and its alias `AddIos()` live in the `BlazorNativeSdk` namespace.

## Setup in a MAUI Blazor Hybrid app

```csharp
// MauiProgram.cs
using BlazorNativeSdk.ApplePlatform; // AppleNativeSdk, AppleActivation (inside #if IOS || MACCATALYST)

builder.Services.AddBlazorNativeSdk(sdk => sdk
    .AddApple(o => o.KeychainService = "com.contoso.notes")
    .ConfigureDeepLinks(links =>
    {
        links.Schemes.Add("contoso");     // contoso://orders/42  -> /orders/42
        links.Hosts.Add("contoso.com");   // https://contoso.com/orders/42 (Universal Link)
    }));

#if IOS || MACCATALYST
builder.ConfigureLifecycleEvents(events => events.AddiOS(ios => ios
    // Must run before launch completes: notification delegate + BGTaskScheduler handlers.
    .WillFinishLaunching((app, options) => { AppleNativeSdk.Initialize(); return true; })
    .OpenUrl((app, url, options) => AppleActivation.HandleOpenUrl(url))
    .ContinueUserActivity((app, activity, handler) => AppleActivation.HandleUserActivity(activity))
    .PerformActionForShortcutItem((app, item, done) => done(AppleActivation.HandleShortcutItem(item)))));
#endif
```

Without MAUI lifecycle events, call the same methods from your `AppDelegate` (`FinishedLaunching` before `base`,
`OpenUrl`, `ContinueUserActivity`, `PerformActionForShortcutItem`). In scene-based apps forward
`UISceneConnectionOptions.UrlContexts`, `UserActivities` and `ShortcutItem` from `SceneWillConnect` with
`isColdStart: true`, and later URLs from `scene:openURLContexts:`. Activations that arrive before the container exists
are buffered and replayed in order.

The SDK attaches its container to native callbacks when the runtime starts (the MAUI integration and
`<NativeActivationOutlet />` start it). Hosts that build the container without starting the SDK should call
`AppleNativeSdk.AttachServices(services)`; otherwise background launches wait for the container until iOS expires them.

## Info.plist

The adapter never calls a privacy-sensitive API whose usage description is missing (iOS would terminate the app). It
returns `Unavailable` with `NativeErrorCodes.MissingDeclaration` naming the key instead.

| Key | Needed for |
| --- | --- |
| `NSCameraUsageDescription` | Photo/video capture, live barcode scanning |
| `NSMicrophoneUsageDescription` | Video capture (recorded with sound) |
| `NSPhotoLibraryUsageDescription` / `NSPhotoLibraryAddUsageDescription` | `photos` / `photos-add-only` permissions (library *picking* uses PHPicker and needs neither) |
| `NSFaceIDUsageDescription` | Biometrics on Face ID devices |
| `BGTaskSchedulerPermittedIdentifiers` | `{CFBundleIdentifier}.bns.refresh` and `{CFBundleIdentifier}.bns.processing` (or your own, passed to `Initialize(refresh, processing)` and `AppleNativeOptions`) |
| `UIBackgroundModes` | `fetch` (refresh tasks) and `processing` (`IsLongRunning` work) |
| `CFBundleURLTypes` | Custom URL schemes (`contoso://`) |
| `UIFileSharingEnabled`, `LSSupportsOpeningDocumentsInPlace` | Make `AppDirectory.Documents` visible in the Files app |

```xml
<key>BGTaskSchedulerPermittedIdentifiers</key>
<array>
  <string>com.contoso.notes.bns.refresh</string>
  <string>com.contoso.notes.bns.processing</string>
</array>
<key>UIBackgroundModes</key>
<array>
  <string>fetch</string>
  <string>processing</string>
</array>
```

Entitlements: `com.apple.developer.associated-domains` (`applinks:contoso.com`) for Universal Links (plus an
`apple-app-site-association` file on the server); `keychain-access-groups` for secure storage in the simulator and on
Mac Catalyst (otherwise `errSecMissingEntitlement` -> `MissingDeclaration`); optionally
`com.apple.developer.usernotifications.time-sensitive` for `NotificationImportance.High`.

## Capabilities

| Capability | iOS | Mac Catalyst | Notes |
| --- | --- | --- | --- |
| Permissions | Yes | Yes | camera, microphone, photos (Limited = selected photos), notifications (provisional = Limited), biometrics (Denied when Face ID was refused, else NotRequired). Storage, exact alarms and clipboard read are NotRequired. `OpenAppSettingsAsync` opens the app's Settings page. |
| Camera | Capture + library | Library only | Essentials `MediaPicker`; mixed photo+video picking uses PHPicker. Camera choice and max duration are not honored by the system UI. |
| Barcode | Live + image | Live + image | Live: `AVCaptureMetadataOutput` full-screen scanner (close button, optional prompt, timeout). Image: Vision with pixel bounds. VisionKit `DataScannerViewController` is Swift-only and has no .NET binding. UPC-A is reported as EAN-13 by Apple and normalized when UPC-A is requested. Live scanning recognizes Codabar from iOS / Mac Catalyst 15.4 (image detection on every supported version). |
| Files | Yes | Yes | Open (multiple), save via export picker (`ExportSheet` on iOS, `SaveDialog` on Mac), folder picker with security-scoped access held for the process lifetime. |
| App storage | Yes | Yes | AppData = `Library`, Cache = `Library/Caches`, Temp = `tmp`, Documents = `Documents`; Downloads is Unsupported. |
| Secure storage | `HardwareBacked` | `OperatingSystem` | Keychain generic passwords, `AfterFirstUnlockThisDeviceOnly` by default. Simulator reports `OperatingSystem`. Items survive app deletion on iOS. |
| Share | Yes | Yes | `UIActivityViewController` with completion reporting; popover anchored on iPad/Mac. |
| Clipboard | Yes | Yes | iOS 16+ shows the "Allow Paste" prompt when reading another app's text; `HasStrings` is checked first. Change notifications include changes made while the app was in the background. |
| Biometrics | Face ID / Touch ID / Optic ID | Touch ID / password | `AllowDeviceCredential` uses the passcode policy; the method used is then not reported. |
| Notifications | Yes | Yes | Immediate, scheduled (calendar, local time), daily/weekly repeats, actions, text input, badges, thread grouping; banners while in the foreground. Channels are a no-op. |
| Deep links | Yes | Yes | Custom schemes, Universal Links, quick actions, documents opened with the app (`File` activations). |
| Lifecycle | Yes | Yes | `OsGranted` background time via `BeginBackgroundTask`. |
| Connectivity | Yes | Yes | `NWPathMonitor`; metered = expensive, data saver = Low Data Mode. |
| Background work | BGTaskScheduler + in-process | In-process | See below. |
| Transfers | Background `URLSession` | In-process | iOS: `AppleBackgroundTransferService`, performed by the system while the app is suspended or closed (see *Background transfers*). Mac Catalyst: Core `HttpTransferService`, which keeps running because Mac apps are not suspended. |

## Background execution: guarantees and limits

| | iOS | Mac Catalyst |
| --- | --- | --- |
| Foreground | Work runs in process when due. | Same. |
| Background | iOS may launch the app for due work: refresh tasks get about 30 s; `IsLongRunning` work runs as a processing task (usually overnight while charging; `RequiresNetworkConnectivity` / `RequiresExternalPower` from its constraints). | Apps are not suspended; work keeps running in process (App Nap may delay it). |
| After termination | Yes, when the system decides; never after the user force-quits the app until it is reopened. | No; resumes on next launch. |
| Reboot | Not documented by Apple; work resumes on the next launch. | Resumes on next launch. |
| Timing | Never exact; no minimum periodic interval is guaranteed; Background App Refresh, Low Power Mode and usage patterns decide. | Close to requested while running. |

The scheduler persists work even when Info.plist entries or `Initialize()` are missing; it then reports `Unavailable`
(`MissingDeclaration` or `HostNotReady`) and runs work only in the foreground. Requests are never submitted for an
identifier without a registered launch handler, because iOS terminates apps that receive such a task. Workers must be
idempotent and honor the cancellation token: if a worker ignores expiration, the task is completed after a 2 s grace
period. In the simulator `BGTaskScheduler` is unavailable; status reports it.

`IAppLifecycle.RequestBackgroundTimeAsync` wraps `BeginBackgroundTask` (usually up to about 30 s; `ExpirationToken`
fires shortly before iOS revokes the time). Lifecycle transition handlers for backgrounding run inside such a lease.

## Background transfers

On iOS, `ITransferService` is `AppleBackgroundTransferService`: downloads and uploads run in a background `URLSession`
(identifier `{CFBundleIdentifier}.bns.transfers`, or `AppleNativeOptions.BackgroundTransferSessionIdentifier`) that the
system performs in its own process. Set `AppleNativeOptions.UseBackgroundUrlSession = false` to use the in-process Core
queue instead.

When transfers finish while the app is suspended or closed, iOS relaunches it in the background and calls
`application:handleEventsForBackgroundURLSession:completionHandler:`. .NET MAUI has no lifecycle event for it, so export
it from the app delegate:

```csharp
// Platforms/iOS/AppDelegate.cs
[Export("application:handleEventsForBackgroundURLSession:completionHandler:")]
public void HandleEventsForBackgroundUrl(UIApplication application, string sessionIdentifier, Action completionHandler)
{
    if (!AppleBackgroundTransfers.HandleEventsForBackgroundUrl(sessionIdentifier, completionHandler))
    {
        completionHandler();
    }
}
```

The SDK processes and persists the session's events, then calls the completion handler so iOS can suspend the app again.
Without the method, events are still processed when the app runs, but iOS is never told the work is done.

| | Guarantee | Limit |
| --- | --- | --- |
| Execution | Transfers continue while the app is suspended or terminated; results are delivered on the next (background) launch. | The system decides when transfers run (Low Power Mode, network quality, thermal state). When the user force-quits the app, iOS cancels its transfers; they are retried on the next launch. |
| Retries | Network loss is handled by the system. HTTP 408/429/5xx and transient network errors are retried with exponential backoff up to `MaxRetries`, honoring `Retry-After`, through new tasks with an `earliestBeginDate`, so retries happen while the app is suspended too. | Other HTTP errors fail the transfer; `RetryAsync` starts it again. |
| Downloads | Resume from the system's resume data when the server supports it. The file is moved to `DestinationPath` as soon as the system delivers it; error responses never replace the destination. | |
| Uploads | Multipart bodies are written to `Library/bns-transfers` first, because background sessions upload from files only. | Interrupted uploads start again from the beginning. |
| `RequireUnmeteredNetwork` | Maps to `allowsExpensiveNetworkAccess = false` (no cellular or personal hotspot). | |
| Progress | `TransferChanged` while the app runs (at most every 250 ms per transfer). | No progress events while the app is suspended. |
