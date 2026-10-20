# Architecture

BlazorNativeSdk gives Blazor apps one API for device, desktop-shell and lifecycle features, while staying honest about
what each host can do. The same component code runs in a browser (WebAssembly or Server), in .NET MAUI Blazor Hybrid on
Android, iOS, Mac Catalyst and Windows, and in WPF Blazor Hybrid. Where a host can't do something, the API says so with
a typed result instead of throwing or silently doing nothing.

## Layers

```
┌───────────────────────────────────────────────────────────────────────────────┐
│ Your Blazor components / services                                             │
│   @inject ICameraService, IBarcodeScanner, IAppActivationService, ...         │
├───────────────────────────────────────────────────────────────────────────────┤
│ BlazorNativeSdk.Abstractions   contracts, NativeResult, CapabilityStatus      │
├───────────────────────────────────────────────────────────────────────────────┤
│ BlazorNativeSdk.Core           builder, registry, fallbacks, activation       │
│                                router, lifecycle base, scheduler, transfers,  │
│                                checkpoints, state stores, components          │
├──────────────┬──────────────┬──────────────┬──────────────┬──────────────────┤
│ .Browser     │ .Android     │ .iOS         │ .Windows     │ .Desktop         │
│ JS interop   │ Essentials + │ Essentials + │ Win32/WinRT  │ IPC, single      │
│ Web APIs     │ AndroidX,    │ UIKit,       │ (no WPF/WinUI│ instance, shell  │
│              │ WorkManager  │ BGTasks      │  dependency) │ ops, protocols   │
├──────────────┴──────────────┴──────────────┴──────────────┴──────────────────┤
│ Host integration:  .Maui (lifecycle wiring, multi-window)   .Wpf               │
└───────────────────────────────────────────────────────────────────────────────┘
```

* **Abstractions** has no dependencies. Libraries that only *consume* capabilities reference it alone.
* **Core** is the runtime every adapter builds on. It registers a fallback for every capability, so injecting any
  capability interface always works. A fallback reports `Unsupported` with code `adapter.missing` and names the adapter
  to add.
* **Adapters** are independent packages. A WebAssembly app references only `BlazorNativeSdk.Browser`; a MAUI app
  references `BlazorNativeSdk.Maui`, which brings in the platform adapter for each target framework.
* **Host integration** packages wire host-specific events (MAUI lifecycle events, WPF windows) into the adapters and
  provide host-specific services such as window management.

## The result model

Every operation returns `NativeResult` or `NativeResult<T>`:

| Status | Meaning | Typical UI response |
| --- | --- | --- |
| `Success` | Done. | Continue. |
| `Unsupported` | This platform or host can't do it at all. | Hide the feature or offer an alternative. |
| `PermissionDenied` | The user or OS denied a permission (`Error.PermissionStatus` says which state). | Explain; link to settings. |
| `Cancelled` | The user dismissed the native UI. | Do nothing. |
| `Unavailable` | Supported, but not right now (`Error.Code`: no hardware, not enrolled, insecure context, missing user gesture, missing manifest entry). | Explain the specific condition. |
| `Failed` | Unexpected failure. | Report or retry. |

Cancelling the caller's `CancellationToken` throws `OperationCanceledException`, like any .NET API. Only user dismissal
produces a `Cancelled` result.

## Runtime capability detection

`ICapabilityRegistry.GetStatusAsync(capability)` (or `INativeCapability.GetStatusAsync()`) reports availability without
prompting the user: `Available`, `RequiresPermission`, `PermissionDenied`, `Unavailable`, `Unsupported` or `Unknown`,
plus optional features such as `CameraFeatures.Video` or `FilePickerFeatures.SaveDialog`. `<CapabilityView>` renders UI
from that status.

## Activation pipeline

Deep links, notification taps, shortcuts and arguments forwarded from a second desktop instance all arrive as
`AppActivation` values:

1. A platform adapter publishes the activation through `IActivationPublisher`. Publishing works from any thread, before
   the UI exists.
2. Registered `IActivationTranslator`s rewrite adapter-specific encodings into canonical activations, or drop forged ones.
3. `ActivationService` keeps activations in a bounded, ordered queue until a `<NativeActivationOutlet />` renders. This
   is what makes cold-start deep links work in Blazor Hybrid, where the `NavigationManager` doesn't exist yet when the OS
   delivers the link. Background activations (`IsBackground`, such as notification actions that don't open the app) skip
   the queue and are never navigated to.
4. `IActivationHandler`s registered in DI run first. They exist from startup, so they can handle activations in a process
   that never shows UI. Handlers registered with `Subscribe` run next. Either kind can claim an activation.
5. URLs are mapped to app routes only if their scheme or host is allow-listed in `DeepLinkOptions`. Explicit routes are
   checked as well: they must be app-relative, with no scheme and no protocol-relative `//`.
6. Route handlers registered with `MapRoute("/orders/{id}")` run, or the outlet navigates to the route.

On Blazor Server the pipeline is scoped per circuit, so one user's notification tap or deep link can never navigate
another user's session.

## Lifecycle and background work

`AppLifecycleBase` normalizes platform events to `Active`, `Inactive`, `Background` and `Terminating`. Transition
handlers that run while the app moves to the background get the expiration token of a background-time lease.
Background execution is described by data rather than promises: each scheduler and transfer adapter publishes a
`BackgroundExecutionPolicy` or `TransferPolicy`. See [background-execution.md](background-execution.md).

## Durable state

`INativeStateStore` holds small JSON documents: queued work, transfer progress, workflow checkpoints and window
placement. The default store is file-based, with atomic writes, in hybrid and desktop hosts, and `localStorage` in
WebAssembly. Serialization uses source-generated `JsonSerializerContext` types, so the libraries are trimming- and
AOT-safe.

## Testing strategy

* Core logic (routing, scheduling, transfers, placement validation, result semantics) is unit-tested with
  `FakeTimeProvider` and fake HTTP handlers.
* `BlazorNativeSdk.Testing` provides fakes for app-level tests (bUnit) and `CapabilityContracts`, a set of behavioral
  checks that every adapter's real implementation must pass.
* Pure platform-mapping logic (error codes, permission states, symbologies) lives in small internal classes that are
  tested without a device.
