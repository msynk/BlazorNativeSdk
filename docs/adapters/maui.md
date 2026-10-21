# .NET MAUI integration (`BlazorNativeSdk.Maui`)

Host integration for .NET MAUI Blazor Hybrid apps. One call registers the platform adapter of each target framework, wires
the MAUI lifecycle to the activation pipeline, starts the SDK when the app is built and provides window management on the
desktop targets. Targets `net10.0-android`, `net10.0-ios`, `net10.0-maccatalyst` and `net10.0-windows10.0.19041.0` (minimum
Android 7.0 / API 24, iOS and Mac Catalyst 15.0, Windows 10 1809). Root namespace `BlazorNativeSdk.Maui`;
`UseBlazorNativeSdk()` lives in `BlazorNativeSdk`.

| Target | Adapters registered | Also from this package |
| --- | --- | --- |
| Android | `AddAndroid()` ([android.md](android.md)) | intent wiring, SDK start in `MauiApplication.OnCreate` |
| iOS | `AddApple()` ([ios.md](ios.md)) | `AppleNativeSdk.Initialize`, URL / Universal Link / quick action wiring (with and without scenes) |
| Mac Catalyst | `AddApple()` | same as iOS, plus `IWindowManager` |
| Windows | `AddDesktop()` + `AddWindows()` ([desktop.md](desktop.md), [windows.md](windows.md)), host = Windows / MAUI / Desktop | host bridge, single instance, launch activation, `IAppLifecycle`, `IWindowManager` |

## Setup

```xml
<PackageReference Include="BlazorNativeSdk.Maui" />
```

```csharp
// MauiProgram.cs
var builder = MauiApp.CreateBuilder();
builder.UseMauiApp<App>();
builder.Services.AddMauiBlazorWebView();

builder.UseBlazorNativeSdk(
    sdk =>
    {
        sdk.ConfigureDeepLinks(links =>
        {
            links.Schemes.Add("contoso");      // contoso://orders/42 -> /orders/42
            links.Hosts.Add("contoso.com");    // https://contoso.com/orders/42 (App Links / Universal Links)
        });
        sdk.AddBackgroundWorker<SyncWorker>("sync");
#if WINDOWS
        sdk.AddWindows(o => o.NotificationActivationScheme = "contoso");  // adapter options: call the adapter method again
#elif ANDROID
        sdk.AddAndroid(o => o.NotificationSmallIcon = "ic_stat_notify");
#endif
    },
    options =>
    {
        options.DefaultRootComponent = typeof(Routes);  // windows opened with WindowOptions.Route render the router
        options.RegisterProtocolSchemes = true;         // Windows, unpackaged: register contoso:// for this executable
    });

return builder.Build();
```

Add `<NativeActivationOutlet />` to the main layout. No code is needed in `MainActivity`, `MainApplication` or the WinUI `App`.
On iOS, export one method from `AppDelegate` so that background transfers that finish while the app is closed are delivered
(MAUI has no lifecycle event for it; see [ios.md](ios.md#background-transfers)). `configure` runs after the platform adapter is registered, so anything it registers overrides the defaults.
Calling `UseBlazorNativeSdk` again only adds configuration.

## What gets wired

The SDK starts in an `IMauiInitializeService`, that is while MAUI builds the app on the main thread and before any window
exists: in `MauiApplication.OnCreate` (Android), `application:willFinishLaunchingWithOptions:` (iOS, Mac Catalyst) and
`OnLaunched` (Windows). A process that the OS started for a WorkManager worker, a BGTaskScheduler task or a notification
action therefore finds the services running although no component ever renders.

| | Android | iOS / Mac Catalyst | Windows |
| --- | --- | --- | --- |
| Launch | `OnCreate` of a `MauiAppCompatActivity` without saved state → `AndroidActivation.HandleIntent(intent, isColdStart)`; cold start = first UI launch of the process | `AppleNativeSdk.Initialize(refresh, processing)` with the identifiers from `AppleNativeOptions` before launch completes; launch-option URL / user activity / quick action are published as the cold start when iOS delivers them | single instance, then the launch activation from the command line (unpackaged) or from `AppInstance.GetActivatedEventArgs()` (packaged: protocol, file, launch) |
| Running app | `OnNewIntent` → `HandleIntent(intent, false)` | `OpenUrl`, `ContinueUserActivity`, `PerformActionForShortcutItem`; scenes: `SceneWillConnect` (URL contexts, user activities, quick action), `SceneOpenUrl`, `SceneContinueUserActivity` | forwarded arguments from a second instance; packaged: `AppInstance.Activated` |
| Lifecycle | `AndroidAppLifecycle` | `AppleAppLifecycle` | `MauiWindowLifecycle` (below) |
| Windows | – | Mac Catalyst: `IWindowManager` | `IWindowManager`, `IWindowsHostBridge` |

* **Android**: helper activities (Essentials media/file pickers, web authenticator) are ignored. A restored activity
  (non-null saved state) never replays its launch intent, following the Android adapter's guidance.
* **iOS / Mac Catalyst**: iOS delivers launch-option URLs, activities and quick actions again through the delegate callbacks,
  and MAUI replays a scene's launch quick action when the scene activates. The integration publishes each one once and
  flags the right one as the cold start. MAUI's internal window activities (`__MAUI…`) are not published. Quick-action
  callbacks always acknowledge their completion handler, as MAUI 10 requires.
* **Windows lifecycle** (`MauiWindowLifecycle`, also the fallback on any platform whose adapter has no lifecycle):
  `Active` while one of the app's windows is activated, `Inactive` when a window is visible but another app has focus,
  `Background` when every window is minimized or hidden, `Terminating` when the last window is destroyed. Switching between
  two windows of the app does not report a transient `Inactive` (transitions are coalesced on the UI thread).
  `BackgroundGuarantee` is `NotRequired`; memory pressure is not reported.
* **Host bridge (Windows)**: `MainWindowHandle` is the HWND of the app's first MAUI window
  (`WinRT.Interop.WindowNative.GetWindowHandle` of its `Microsoft.UI.Xaml.Window`), then the first open window after it
  closed; UI work runs through the MAUI application dispatcher (inline on the UI thread). It replaces the Windows adapter's
  default bridge; register your own after `UseBlazorNativeSdk` to override it.

## Multiple windows (Windows, Mac Catalyst)

```csharp
@inject IWindowManager Windows

var result = await Windows.OpenAsync(new WindowOptions
{
    Id = "settings",          // unique: opening "settings" again focuses the open window; key for placement persistence
    Title = "Settings",
    Route = "/settings",      // rendered by MauiNativeOptions.DefaultRootComponent through BlazorWebView.StartPath
    Width = 640, Height = 480, MinWidth = 480, MinHeight = 320,
    OwnerId = "main",         // Windows only: stays above the main window and closes with it
});
```

* Windows are `new Window(new BlazorWindowPage(...))` opened with `Application.Current.OpenWindow`. `BlazorWindowPage` hosts a
  `BlazorWebView` with `MauiNativeOptions.HostPage` (default `wwwroot/index.html`), the root component at
  `RootComponentSelector` (default `#app`): `WindowOptions.RootComponent` with its `Parameters`, or `DefaultRootComponent` at
  `WindowOptions.Route`. A `HeadOutlet` is added at `HeadOutletSelector` (default `head::after`). Use
  `MauiNativeOptions.ConfigureWindow` to customize the MAUI window or the `BlazorWebView` (for example `UrlLoading`).
* Every MAUI window is tracked, including the main window (id `MauiNativeOptions.MainWindowId`, default `main`) and windows
  the app opens through MAUI directly (`window-1`, `window-2`, ...). `WindowOpened` / `WindowClosed` are raised on the UI
  thread.
* **Placement persistence** (`PersistPlacement` with an explicit `Id`): the placement is loaded before the window opens,
  validated against the connected displays by `IWindowPlacementStore`, applied before the window is first shown on Windows,
  saved one second after the last move or resize (`PlacementSaveDelay`) and always when the window closes. On Windows the
  main window's placement is persisted too (`PersistMainWindowPlacement`). Minimized state is never restored.
* **Navigation**: secondary windows usually render the same layout, so they contain a `<NativeActivationOutlet />` as well.
  The window's root component is wrapped in Core's `NativeWindowRoot`, which attaches the `BlazorWebView`'s service scope
  (`NativeWindowScope`) to the window. Outlets in such a scope are non-primary unless the window routes activations, so deep
  links and notification taps keep navigating the main window (`RouteActivationsToMainWindow`, default `true`; set it to
  `false` to let the most recently opened window receive them). An explicit `IsPrimary` on the outlet always wins.

| `INativeWindow` | Windows | Mac Catalyst |
| --- | --- | --- |
| Title | MAUI `Window.Title` | MAUI `Window.Title` |
| Bounds, move, resize | Win32 controller (DIPs, per-monitor, like `IScreenService`) | MAUI `X`/`Y`/`Width`/`Height` → `UIWindowSceneGeometryPreferencesMac` (macOS 13+) |
| Minimize, maximize, restore | ✓ | Unsupported (UIKit has no API; `State` is always `Normal`) |
| Full screen | WinUI `AppWindow` full-screen presenter | Unsupported |
| Always on top | `OverlappedPresenter.IsAlwaysOnTop` | Unsupported |
| Visibility | ✓ (`VisibilityChanged` on show/hide) | Unsupported (close the window instead); `VisibilityChanged` when the scene is stopped or resumed |
| Focus | foreground with the foreground-lock workaround; taskbar flash when Windows refuses | `Application.ActivateWindow` (scene activation) |
| `Closing` cancellation | user closes (`AppWindow.Closing`) and `CloseAsync` | `CloseAsync` only; UIKit cannot intercept the close button |
| Owner, `Resizable = false`, `InitialState` | ✓ (`GWLP_HWNDPARENT`, presenter) | `Resizable = false` (min = max size); owner and initial state ignored |
| New window position | centered on the display of the owner or main window | centered over the main window |

`SupportedOperations` and `GetStatusAsync` features (`MauiWindowFeatures`) report exactly this. On Mac Catalyst the window
manager needs multiple scenes; without them `OpenAsync` and `GetStatusAsync` report `Unavailable` with
`app.missing-declaration`:

```xml
<!-- Platforms/MacCatalyst/Info.plist -->
<key>UIApplicationSceneManifest</key>
<dict>
  <key>UIApplicationSupportsMultipleScenes</key><true/>
  <key>UISceneConfigurations</key>
  <dict>
    <key>UIWindowSceneSessionRoleApplication</key>
    <array>
      <dict>
        <key>UISceneConfigurationName</key><string>__MAUI_DEFAULT_SCENE_CONFIGURATION__</string>
        <key>UISceneDelegateClassName</key><string>SceneDelegate</string>
      </dict>
    </array>
  </dict>
</dict>
```

```csharp
// Platforms/MacCatalyst/SceneDelegate.cs
[Register("SceneDelegate")]
public class SceneDelegate : MauiUISceneDelegate { }
```

Android and iOS register no window manager: `IWindowManager` reports `Unsupported` there.

## Single instance on Windows

`MauiNativeOptions.SingleInstance` (default `true`) runs while the app is built, before a window exists and before the SDK
starts persisted work, so a second instance never shows a window or touches the primary's state.

| | Unpackaged (`WindowsPackageType=None`) | Packaged (MSIX) |
| --- | --- | --- |
| Mechanism | Desktop `ISingleInstanceService`: per-user lock file + authenticated named pipe | Windows App SDK `AppInstance.FindOrRegisterForKey` / `RedirectActivationToAsync` |
| What is forwarded | the command line (URLs registered with `RegisterProtocolSchemes` / `IProtocolRegistrationService` arrive as arguments) | the full activation: protocol URL, files, launch arguments, execution alias |
| Primary receives | `DeepLink` / `File` / `SecondaryInstance` activations (`ActivationArguments`) | the same, from `AppInstance.Activated` |
| URL scheme | `RegisterProtocolSchemes = true` (or `IProtocolRegistrationService` yourself) | `windows.protocol` in `Package.appxmanifest` |

The secondary instance hands over the foreground right (`AllowSetForegroundWindow`) and ends with exit code 0 through
`TerminateProcess`, deliberately skipping shutdown code: nothing has been initialized that needs it, and WinUI cannot close
cleanly before its first window. With `FocusMainWindowOnActivation` (default `true`) the primary brings its main window to
the front for every activation that reaches the running app (forwarded launch, deep link, toast click); Windows may refuse
and flash the taskbar button instead. The redirect of a packaged app waits at most 10 seconds.

Toast clicks after the app exited need a URL scheme: set `WindowsNativeOptions.NotificationActivationScheme` to one of the
app's schemes (see [windows.md](windows.md#notification-activation)).

## Required declarations

Declare what the capabilities you use need; the platform documents list the details.

* **Android** ([android.md](android.md#androidmanifestxml)): permissions (`CAMERA`, `POST_NOTIFICATIONS`,
  `RECEIVE_BOOT_COMPLETED`, `SCHEDULE_EXACT_ALARM`, `ACCESS_NETWORK_STATE`, media permissions), camera `<queries>`, backup
  exclusions for secure storage, and on `MainActivity`: `LaunchMode = LaunchMode.SingleTop` plus `[IntentFilter]`s for your
  scheme and verified `https` host (`AutoVerify = true`, `assetlinks.json` on the server).
* **iOS / Mac Catalyst** ([ios.md](ios.md#infoplist)): the `application:handleEventsForBackgroundURLSession:completionHandler:`
  export in `AppDelegate` for background transfers, usage descriptions, `CFBundleURLTypes`,
  `BGTaskSchedulerPermittedIdentifiers` (`{bundle id}.bns.refresh`, `{bundle id}.bns.processing`), `UIBackgroundModes`
  (`fetch`, `processing`), associated-domains and keychain entitlements; Mac Catalyst windows need the scene manifest above.
* **Windows** ([windows.md](windows.md), [desktop.md](desktop.md)): unpackaged apps register schemes at runtime
  (`RegisterProtocolSchemes`); packaged apps declare `windows.protocol` (and file type associations) in the manifest.

`samples/BlazorNativeSdk.Samples.Maui` contains a complete, working set of these files.

## Options

| `MauiNativeOptions` | Default | Purpose |
| --- | --- | --- |
| `HostPage` | `wwwroot/index.html` | Host page of windows opened by the window manager |
| `RootComponentSelector` | `#app` | Selector of their root component |
| `DefaultRootComponent` / `DefaultRootComponentParameters` | `null` | Root component for windows opened with a route (typically `Routes`) |
| `HeadOutletSelector` | `head::after` | `HeadOutlet` in those windows (`null` for none) |
| `MainWindowId` | `main` | Id of the app's first window |
| `SingleInstance` | `true` | Windows: enforce one instance |
| `RegisterProtocolSchemes` | `false` | Windows, unpackaged: register deep-link and toast schemes at startup |
| `FocusMainWindowOnActivation` | `true` | Windows: bring the main window to the front for activations of the running app |
| `PersistMainWindowPlacement` | `true` | Windows: persist the main window's placement |
| `PlacementSaveDelay` | 1 s | Debounce of placement saves |
| `WindowOpenTimeout` | 15 s | How long `OpenAsync` waits for the platform window |
| `RouteActivationsToMainWindow` | `true` | Secondary windows' outlets are non-primary |
| `ConfigureWindow` | `null` | Customize each window and its `BlazorWebView` |

## Limitations

* **Windows**: user-initiated closes of owned windows when their owner closes do not raise `Closing`. The lifecycle does not
  report memory pressure. A secondary instance that cannot reach a hung primary still exits (`ArgumentsForwarded = false`,
  logged). `RegisterProtocolSchemes` writes `HKCU\Software\Classes` and only replaces registrations the app owns.
* **Mac Catalyst**: no minimize/maximize/full screen/always-on-top/visibility control (no UIKit API; AppKit bridging through
  private classes was rejected as unreliable); move/resize need macOS 13; `Closing` cannot cancel the close button; the main
  window's frame is restored by macOS, not by the SDK. The lifecycle comes from the Apple adapter.
* **iOS**: no window manager (iPad multi-window scenes are routed for activations but not managed).
* **Android**: no window manager; restored activities do not replay their launch intent (by design).
