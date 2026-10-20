# Writing a BlazorNativeSdk adapter

An adapter makes one platform or host provide some of the capability interfaces defined in
`BlazorNativeSdk.Abstractions`. Adapters are independent packages: an app references only the adapters for the hosts it
targets, and each adapter can be tested on its own.

## Package layout

| Package | Target framework(s) | Root namespace | Provides |
| --- | --- | --- | --- |
| `BlazorNativeSdk.Abstractions` | `net10.0` | `BlazorNativeSdk` | Contracts only |
| `BlazorNativeSdk.Core` | `net10.0` | `BlazorNativeSdk` | DI builder, registry, fallbacks, activation routing, scheduler, transfers, state stores |
| `BlazorNativeSdk.Browser` | `net10.0` (Razor class library) | `BlazorNativeSdk.Browser` | Web platform APIs through JS interop |
| `BlazorNativeSdk.Desktop` | `net10.0` | `BlazorNativeSdk.Desktop` | Host-neutral desktop services: IPC, single instance, shell file operations, protocol registration |
| `BlazorNativeSdk.Windows` | `net10.0-windows10.0.19041.0` | `BlazorNativeSdk.WindowsPlatform` | Win32/WinRT services usable from WPF, WinForms and MAUI |
| `BlazorNativeSdk.Android` | `net10.0-android` | `BlazorNativeSdk.AndroidPlatform` | Android services |
| `BlazorNativeSdk.iOS` | `net10.0-ios`, `net10.0-maccatalyst` | `BlazorNativeSdk.ApplePlatform` | iOS / Mac Catalyst services |
| `BlazorNativeSdk.Maui` | android, ios, maccatalyst, windows | `BlazorNativeSdk.Maui` | MAUI host integration (lifecycle wiring, multi-window) |
| `BlazorNativeSdk.Wpf` | `net10.0-windows10.0.19041.0` | `BlazorNativeSdk.Wpf` | WPF host integration (windows, lifecycle, startup, single instance) |
| `BlazorNativeSdk.Testing` | `net10.0` | `BlazorNativeSdk.Testing` | Fakes and contract checks |

**Namespace rule.** Never create a namespace segment named `Android`, `Windows`, `iOS`, `UIKit`, `Foundation` or any
other platform root namespace below `BlazorNativeSdk`. C# name lookup would resolve `Android.App.Activity` to
`BlazorNativeSdk.Android.App` and fail. That is why platform adapters use `AndroidPlatform`, `ApplePlatform` and
`WindowsPlatform`. Public registration methods (`AddAndroid()`, `AddWindows()`, ...) live in the `BlazorNativeSdk`
namespace so apps need no extra `using`.

## Rules every adapter follows

1. **Results, not exceptions.** Every operation returns `NativeResult`/`NativeResult<T>`. Map platform outcomes precisely:
   - user dismissed native UI → `NativeError.Cancelled(...)`
   - permission denied → `NativeError.PermissionDenied(capability, permission, status)`
   - platform cannot do it at all → `NativeError.Unsupported(capability, reason)`
   - supported but not possible now (no hardware, not enrolled, insecure context, missing user gesture) →
     `NativeError.Unavailable(capability, code, message)` with a code from `NativeErrorCodes`
   - unexpected failure → `NativeError.Failed(...)` or `NativeError.FromException(...)`

   Wrap platform calls with `NativeOperation.RunAsync`/`TryAsync` so unexpected exceptions become `Failed` results.
2. **Cancellation contract.** If the caller's `CancellationToken` is cancelled, throw `OperationCanceledException`
   (standard .NET). User dismissal is a `Cancelled` *result*.
3. **Honest status.** `GetStatusAsync` must never prompt. Report `RequiresPermission` when using the capability will
   prompt, `PermissionDenied` when it was denied, `Unavailable` with a reason code when hardware/configuration is missing,
   and list optional features with the constants from the `*Features` classes.
4. **No silent no-ops for user-visible actions.** Configuration-only calls that have no meaning on a platform (for example
   Android notification channels on iOS) succeed as no-ops; anything the user would expect to *see* returns `Unsupported`.
5. **Register through the builder.** Use `builder.AddCapability<TService, TImplementation>(NativeCapabilities.X, lifetime)`.
   It replaces any previous provider and registers the capability with the registry. One implementation class may
   implement several interfaces; it is registered once and shared.
6. **Thread affinity.** Public members may be called from any thread. Marshal to the UI thread internally.
7. **Logging.** Use `[LoggerMessage]` source-generated methods. Never log secrets, file contents or clipboard contents.
8. **Trimming/AOT.** Avoid reflection. Use source-generated `JsonSerializerContext` for JSON. `net10.0` libraries set
   `IsAotCompatible`.
9. **Documentation.** Every public type and member has XML docs. Document platform guarantees and limits in remarks.
10. **Analyzers.** `Release` builds treat warnings as errors. Build both `Debug` and `Release`.

## Activation (deep links, notification taps, forwarded launches)

Adapters never navigate. They publish an `AppActivation` through `IActivationPublisher.PublishAsync`. Core buffers
activations until a `<NativeActivationOutlet />` is rendered (cold start), runs handlers, maps URLs to app routes using
the allow-listed schemes/hosts in `DeepLinkOptions`, and navigates. Set `IsColdStart = true` for the activation that
launched the process. Notification taps set `Kind = ActivationKind.Notification`, `NotificationId`, `ActionId`,
`UserText`, `Data` and `Route` (copied from `LocalNotification.Route`).

* Set `IsBackground = true` for activations that do not bring the app to the foreground (notification actions with
  `OpensApp = false`). Core dispatches them immediately to `IActivationHandler`s and subscribers, without waiting for UI,
  and never navigates.
* When a platform delivers activations in an adapter-specific encoding (for example signed toast-launch URLs on Windows),
  register an `IActivationTranslator` with `builder.AddActivationTranslator<T>()` or `TryAddEnumerable`. Translators run
  in order on every published activation and may return `null` to drop forged input. Never wrap or replace
  `IActivationPublisher` itself: hosts such as Blazor Server replace the pipeline per circuit.

## Lifecycle

Derive from `AppLifecycleBase`, call `ReportState(...)` from platform callbacks and `ReportMemoryPressure()` on low
memory. Override `RequestBackgroundTimeCoreAsync` when the platform grants background time (iOS
`BeginBackgroundTask`), returning a `BackgroundTimeLease` whose `Expire()` you call from the expiration handler.

## Background work

The Core `InProcessBackgroundWorkScheduler` persists work and runs it while the process is alive. OS adapters either
wrap it (call `RunDueWorkAsync(isOsInitiated: true, token)` when the OS wakes the app, as the iOS adapter does with
`BGTaskScheduler`) or replace it with a native scheduler (Android `WorkManager`). Always publish an accurate
`BackgroundExecutionPolicy`.

## Testing

- Pure mapping logic (error-code mapping, option translation) lives in small internal static classes so it can be unit
  tested without a device.
- `BlazorNativeSdk.Testing` provides fakes for every capability and contract checks (`SecureStorageContract`,
  `StateStoreContract`, ...) that adapter test suites run against real implementations.
