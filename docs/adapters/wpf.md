# WPF host integration (`BlazorNativeSdk.Wpf`)

Connects BlazorNativeSdk to WPF Blazor Hybrid apps (`BlazorWebView`). Target framework `net10.0-windows10.0.19041.0`,
minimum Windows 10 1809 (10.0.17763). Root namespace `BlazorNativeSdk.Wpf`; `AddWpf()`, `WpfNativeSdk.StartAsync()` and
`window.AttachBlazorNativeSdk()` live in `BlazorNativeSdk`.

`AddWpf()` declares the host (`Windows` / `Wpf` / `Desktop`) and registers:

| Piece | Provides |
| --- | --- |
| `AddDesktop()` | IPC, single instance, shell file operations, protocol registration, file state store, background work and transfers ([desktop.md](desktop.md)) |
| `AddWindows()` | Displays, tray, global shortcuts, menus, DPAPI secure storage, Windows Hello, clipboard, pickers, share, toasts, connectivity, permissions ([windows.md](windows.md)) |
| `WpfHostBridge` | `IWindowsHostBridge`: the attached main window's HWND and the WPF dispatcher, so pickers, the share UI, Windows Hello and menu bars are parented and marshalled correctly |
| `WpfWindowManager` | `IWindowManager` (`desktop.windows`): `BlazorWebView` windows, adopted XAML windows, placement persistence |
| `WpfAppLifecycle` | `IAppLifecycle` from `Application` and window events (`BackgroundExecutionGuarantee.NotRequired`) |
| `WpfActivationFocusHandler` | Brings the main window to the front when a deep link, second launch or toast click reaches the running app |

It also calls `AddWpfBlazorWebView()` (idempotent), which windows opened by the window manager need.

## Setup

```csharp
public partial class App : Application
{
    private ServiceProvider? _services;
    public static IServiceProvider Services => ((App)Current)._services!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
        services.AddBlazorNativeSdk(sdk => sdk
            .Configure(o => { o.AppId = "com.contoso.notes"; o.AppName = "Contoso Notes"; })
            .ConfigureDeepLinks(d => d.Schemes.Add("contoso-notes"))
            .AddWpf(o =>
            {
                o.DefaultRootComponent = typeof(Routes);          // for WindowOptions.Route windows
                o.RegisterProtocolSchemes = true;                  // unpackaged: register contoso-notes://
                o.ConfigureWindowsAdapter(w => w.NotificationActivationScheme = "contoso-notes");
                o.ConfigureDesktopAdapter(d => d.AllowedUriSchemes.Add("vscode"));
            }));
        _services = services.BuildServiceProvider();

        if (!await WpfNativeSdk.StartAsync(_services, e.Args))
        {
            Shutdown();   // another instance is running and received the arguments
            return;
        }

        new MainWindow().Show();
    }
}
```

Remove `StartupUri` from `App.xaml`: the main window must be created after `StartAsync` decides whether this process is
the primary instance. In the main window, host the `BlazorWebView` as usual and adopt the window:

```csharp
public MainWindow()
{
    InitializeComponent();
    WebView.Services = App.Services;
    _ = this.AttachBlazorNativeSdk(App.Services).GetValueOrThrow();   // id "main", placement persisted
}
```

Place `<NativeActivationOutlet />` in the layout so launch and forwarded activations are routed once the UI renders.
An `app.manifest` with `<dpiAwareness>PerMonitorV2</dpiAwareness>` is recommended: WebView2 renders crisply on every
monitor, and WPF's coordinates then match the SDK's per-monitor DIPs.

| `WpfNativeOptions` | Default | Purpose |
| --- | --- | --- |
| `HostPage` | `wwwroot\index.html` | Host page of windows opened by the window manager |
| `RootComponentSelector` | `#app` | Selector of the root component in the host page |
| `DefaultRootComponent` | `null` | Root component for `WindowOptions.Route` windows; falls back to the first root component of the main window's `BlazorWebView` |
| `SingleInstance` | `true` | Enforce a single instance in `StartAsync` |
| `RegisterProtocolSchemes` | `false` | Register `DeepLinkOptions.Schemes` and `NotificationActivationScheme` for the current user (unpackaged apps) |
| `FocusMainWindowOnActivation` | `true` | Focus the main window for foreground activations of the running app |
| `PlacementSaveDelay` | 1 s | Debounce of placement saves while a window moves or resizes |
| `RouteActivationsToMainWindow` | `true` | Outlets in windows opened by `WpfWindowManager` are non-primary, so activations navigate the main window |
| `ConfigureWindow` | `null` | `(Window, BlazorWebView, WindowOptions)` callback for windows the manager creates (icon, styles, `UrlLoading`) |
| `ConfigureWindowsAdapter(...)` / `ConfigureDesktopAdapter(...)` | — | Configure `WindowsNativeOptions` / `DesktopNativeOptions` |

## Start-up and single instance

`WpfNativeSdk.StartAsync(services, args)` should run first in `OnStartup`:

1. With `SingleInstance` it acquires `ISingleInstanceService`. A secondary instance forwards its arguments (deep links,
   files, other arguments) to the running instance over an authenticated per-user pipe and `StartAsync` returns
   `false`. The primary keeps the lease for the lifetime of the app and releases it on `Application.Exit`. If
   single-instance infrastructure fails, the app continues as an independent instance (a warning is logged).
2. The launch arguments become a cold-start activation (`ActivationArguments`: allow-listed URLs → `DeepLink`,
   existing files → `File`, else `Launch`). With single instance the desktop service publishes it; set
   `DesktopNativeOptions.PublishLaunchActivation = false` to suppress it.
3. With `SetProcessAppUserModelId` (default) the toast identity is initialized so the process AppUserModelID is set
   before the first window (it determines taskbar grouping).
4. With `RegisterProtocolSchemes`, schemes are registered in the background under `HKCU\Software\Classes` (MSIX apps
   declare `windows.protocol` in the manifest instead; registration then logs and skips). Running through the `dotnet`
   host cannot register a handler; run the app's executable.
5. The lifecycle attaches to the application, the main window's placement starts loading, and the SDK runtime starts.

Forwarded activations reach the primary while it runs: the activation pipeline routes them (only allow-listed schemes
and hosts become routes) and the main window is restored and brought to the front. Windows' foreground lock may refuse
activation; the taskbar button then flashes.

On `Application.Exit` (hooked by `StartAsync`) pending placements are saved and the lease is released, bounded to two
seconds.

## Windows

```csharp
var result = await windows.OpenAsync(new WindowOptions
{
    Id = "settings",            // opening an open id focuses it; also the placement key
    Title = "Settings",
    Route = "/settings",        // DefaultRootComponent starting at this route
    Width = 640, Height = 480, MinWidth = 400, MinHeight = 300,
    OwnerId = "main",           // stays above its owner, centered on it, closes with it
});
```

* `RootComponent` (+ `Parameters`) renders a dedicated component instead of a route. Routes must be app-relative;
  absolute URLs, `//host` and backslashes are rejected with `operation.invalid-argument`.
* Every window gets its own `BlazorWebView` (and DI scope) with the app's service provider; it is disposed when the
  window closes.
* Without `X`/`Y` a window is centered on its owner or the screen. Positions and sizes use the SDK's DIP convention
  (physical pixels / monitor scale, like `IScreenService`) and are applied to the native window before it is shown.
* `Resizable = false` maps to `ResizeMode.CanMinimize`; `AlwaysOnTop` to `Topmost`; `FullScreen` switches to a
  borderless window covering its monitor after the window is shown (WPF has no full-screen state).
* Opening fails with `host.not-ready` when no WPF `Application` runs, `operation.not-found` for an unknown `OwnerId`
  and `operation.invalid-argument` for invalid options or when no root component is known.

`WpfNativeWindow` (the `INativeWindow`) can be used from any thread. Title, topmost, visibility and minimize/maximize go
through WPF so `Window.WindowState`, `Visibility` and `Topmost` stay consistent; bounds, state, focus and visibility
are read from the native window. Events are raised on the UI thread (`StateChanged` also for full screen;
`INativeWindow.VisibilityChanged` for show/hide) and handler exceptions are logged instead of crashing the dispatcher. `Closing`
handlers can cancel; `CloseAsync` posts `WM_CLOSE`, so `Closing` runs asynchronously. `Window` exposes the WPF window
for framework-specific needs (on its dispatcher).

Windows the app creates itself can be adopted with `WpfWindowManager.AttachWindow(window, id)`; the main window with
`AttachMainWindow` / `AttachBlazorNativeSdk`. If the app attaches nothing, the first window opened by the manager
becomes the main window when WPF made it `Application.MainWindow`.

## Placement persistence

With `PersistPlacement` (default) and an explicit id, the window reopens where the user left it:

* The saved placement is loaded through `IWindowPlacementStore`, which validates it against the connected displays
  (windows saved on a disconnected monitor reappear on screen), and applied to the hidden native window before it is
  shown. For an attached window it is applied in `SourceInitialized`; the load starts when the window is attached (for
  `main`, already in `StartAsync`), and the UI thread waits at most 500 ms for it, otherwise it is applied when ready.
* A saved state wins over `WindowOptions.InitialState`, except an explicit `Minimized`; windows never reopen
  minimized. A window minimized from maximized reopens maximized. A saved size wins over `SizeToContent` and a saved
  position over `WindowStartupLocation`.
* The restored (normal) bounds from `GetWindowPlacement`, the state and the display are saved when the window closes
  and `PlacementSaveDelay` after it stops moving, resizing or changing state. Unchanged placements are not rewritten
  and saves are serialized. `FlushPlacementsAsync()` saves immediately.

Placements live in the `INativeStateStore` (`{AppData}\state` with `AddDesktop()`).

## Lifecycle

| State | Signal |
| --- | --- |
| `Launching` | Until a tracked window is shown or the app is activated |
| `Active` | `Application.Activated` (an app window is the foreground window) |
| `Inactive` | `Application.Deactivated`, or a window restored while another app has the focus |
| `Background` | Every tracked window minimized or hidden, or all closed while the process keeps running (tray apps) |
| `Terminating` | `Application.SessionEnding` (unless cancelled) and `Application.Exit` |

Desktop processes keep running in the background, so `BackgroundExecutionGuarantee` is `NotRequired` and background
work and transfers continue. WPF has no memory-pressure notification; `MemoryPressure` is never raised.

## Limitations

* `StartAsync` must run before any window is created; with `StartupUri` WPF creates the window too early.
* A window with `ShowActivated = false` cannot be shown maximized (WPF and Win32 cannot maximize without activation):
  such windows reopen with their restored bounds.
* Windows opened by `WpfWindowManager` render their root component inside Core's `NativeWindowRoot`, so an outlet in
  their layout is non-primary and deep links keep navigating the main window (`RouteActivationsToMainWindow`). Windows
  the app creates and attaches itself are not wrapped: their outlets are primary and the most recently opened one
  receives route navigations. Render a layout without the outlet, or with `IsPrimary="false"`, in such windows.
* Windows are created on the WPF application's dispatcher; apps with several UI threads must attach windows from their
  own threads and cannot use windows of another thread as owners.
* From background threads, `MainWindowHandle` falls back to the process's main window handle until a main window is
  attached.
* Full screen restyles the window (`WS_POPUP`); `WindowStyle`/`ResizeMode` changes made by the app while in full screen
  are overwritten when it leaves full screen.
* The WebView2 runtime must be installed (it ships with Windows 11 and current Windows 10); `BlazorWebView` fails to
  initialize otherwise.
