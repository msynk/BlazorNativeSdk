# Background execution: guarantees and limits

Mobile operating systems decide when apps run. iOS and Android apply different, changing background-execution
policies, and browsers throttle or freeze background tabs. BlazorNativeSdk doesn't hide these limits. Each adapter
publishes what it actually guarantees in `IBackgroundWorkScheduler.Policy` and `ITransferService.Policy`, so apps can
design around them and explain them to users.

## Work scheduling (`IBackgroundWorkScheduler`)

| Adapter | Runs in background | Runs after app is closed | Survives reboot | Timing | Notes |
| --- | --- | --- | --- | --- | --- |
| Android (WorkManager) | Yes | Yes (not after *Force stop*) | Yes | Inexact; periodic minimum 15 min | Runs are capped at about 10 minutes. Doze and App Standby buckets defer work. |
| iOS (BGTaskScheduler + in-process) | When the system grants time | Yes, unless the user force-quit the app | Not documented by Apple | Inexact; the system learns usage patterns | Refresh tasks get about 30 s. Processing tasks run while idle or charging. Identifiers must be listed in Info.plist. |
| Desktop (in-process) | Yes, while the process runs | No; resumes on next launch | Work is persisted | Close to requested | Machine sleep pauses everything. |
| Browser (in-process) | No; only while the page is open and visible | No; resumes when the page reopens | Work is persisted in `localStorage` | Close to requested in foreground | Background tabs are throttled and may be frozen or discarded. |

The in-process scheduler persists work through `INativeStateStore`. When only the in-memory store is registered (the
default on Blazor Server), its policy and status say that scheduled work is lost when the app exits instead of
promising that it resumes.

Its `WorkConstraints.NetworkConnected` accepts internet or local-network access (`ConnectivitySnapshot.IsConnected`),
but not a captive portal. Windows reports local access when its own internet probe fails, which happens behind some
proxies and filtered networks although servers are reachable; a worker that cannot reach its server returns
`WorkResult.Retry()`.

Rules for workers:

1. **Be idempotent.** Every platform may run a worker more than once.
2. **Honor the cancellation token.** It fires when the OS revokes execution time.
3. **Persist progress** in small steps, and return `WorkResult.Retry()` when interrupted.
4. **Don't touch the UI.** When `WorkContext.IsOsInitiated` is true, no Blazor renderer, JS interop or navigation may exist.

## Short tasks after backgrounding (`IAppLifecycle.RequestBackgroundTimeAsync`)

| Host | Guarantee | Meaning |
| --- | --- | --- |
| iOS | `OsGranted` | Usually up to about 30 s. `ExpirationToken` fires shortly before the grant ends. |
| Android | `BestEffort` | No time is guaranteed without a foreground service. The process may be cached or killed. |
| Browser | `BestEffort` | `pagehide` and `visibilitychange` are the last reliable signals. Use `navigator.sendBeacon` for final telemetry. |
| Desktop | `NotRequired` | The process keeps running while minimized. |

## Transfers (`ITransferService`)

Every transfer adapter persists its transfers, waits for connectivity (or an unmetered network when requested), retries
transient failures with exponential backoff, honors `Retry-After` and resumes interrupted downloads. What differs is who
keeps them running while the app is not in the foreground:

| Adapter | Runs in background | Runs after app is closed | Notes |
| --- | --- | --- | --- |
| Android (`HttpTransferService` + WorkManager) | Yes, in WorkManager runs | Yes (not after *Force stop*) | While any transfer is unfinished, WorkManager work `bns.transfers` (network required) keeps the queue moving in the app process. Each run is capped at about 10 minutes; the next run resumes downloads from the last byte. Uploads restart from the beginning. |
| iOS (background `URLSession`) | Yes, performed by the system | Yes, unless the user force-quit the app | The system decides when transfers run and relaunches the app in the background to deliver results. Force-quit cancels them; they are retried on the next launch. Uploads restart from the beginning. |
| Mac Catalyst, Windows, desktop (`HttpTransferService`) | Yes, while the process runs | No; resumes on next launch | Machine sleep pauses transfers. |
| Blazor Server (`HttpTransferService`) | Runs on the server | Server process lifetime | Uses the server's network, not the user's browser. |
| Blazor WebAssembly | – | – | Not available: reports `Unsupported`. |

The Core `HttpTransferService` also runs transfers on local-network access (see the scheduler's network constraint
above). A transient failure without reported internet access does not use up a retry: the transfer waits for
connectivity and, on local access, tries again after `HttpTransferOptions.MaximumBackoff`. Like the scheduler, it
reports when no durable state store is registered and queued transfers would be lost on exit.

The Core `HttpTransferService` resumes downloads with `Range` and `If-Range` requests. With
`HttpTransferOptions.UseBackgroundWork` it hands its queue to the registered `IBackgroundWorkScheduler`; the Android
adapter enables this when WorkManager is used. The iOS adapter (`AppleBackgroundTransferService`) needs one line in the
app delegate, see [ios.md](adapters/ios.md#background-transfers).

## Resuming interrupted workflows (`IWorkflowCheckpointStore`)

The OS may kill a backgrounded app at any time, for example while the camera or another app is in the foreground. Save
a checkpoint before you leave the app and after each meaningful step. On startup, or in `<NativeActivationOutlet />`
handlers, call `GetPendingAsync()` to offer "Continue where you left off". Checkpoints expire after a time-to-live you
choose and are stored unencrypted, so don't put secrets in them.
