# Windows adapter (`BlazorNativeSdk.Windows`)

Win32/WinRT implementations of the BlazorNativeSdk capabilities for WPF, Windows Forms and .NET MAUI (WinUI) Blazor
Hybrid apps. Target framework `net10.0-windows10.0.19041.0`, minimum Windows 10 1809 (10.0.17763). Root namespace
`BlazorNativeSdk.WindowsPlatform`; `AddWindows()` lives in `BlazorNativeSdk`. The package references no UI framework
(no WPF, WinForms, WinUI or Windows App SDK) and uses hand-written P/Invoke plus the WinRT projections of the Windows SDK.

## Setup

```csharp
services.AddBlazorNativeSdk(sdk => sdk
    .Configure(o => { o.AppId = "com.contoso.notes"; o.AppName = "Contoso Notes"; })
    .AddWindows(o =>
    {
        o.NotificationActivationScheme = "contoso-notes";  // optional, see "Notification activation"
        o.NotificationIconPath = @"C:\Program Files\Contoso\notes.png";
    })
    .AddDesktop());  // IPC, single instance, shell file operations, protocol registration (separate package)
```

The adapter needs to know the main window and its UI thread. Host packages (`BlazorNativeSdk.Wpf`, `BlazorNativeSdk.Maui`)
register an `IWindowsHostBridge` for you. Without one, `DefaultWindowsHostBridge` uses `Process.MainWindowHandle` and
runs work inline, which is fine when calls come from the UI thread (Blazor's renderer thread in WPF/WinForms hosts).

| Host | Bridge |
| --- | --- |
| WPF | Host package, or your own: `MainWindowHandle => new WindowInteropHelper(Application.Current.MainWindow).Handle`, `InvokeOnUIThreadAsync => Dispatcher.InvokeAsync(action).Task.Unwrap()` |
| Windows Forms | `MainWindowHandle => mainForm.Handle`, `InvokeOnUIThreadAsync` via `Control.InvokeAsync` (or `BeginInvoke` + `TaskCompletionSource`) |
| .NET MAUI (WinUI) | Host package, or `WinRT.Interop.WindowNative.GetWindowHandle(mauiWindow.Handler.PlatformView)` and `MainThread.InvokeOnMainThreadAsync` |

Register a custom bridge before or after `AddWindows()` with `services.AddSingleton<IWindowsHostBridge, MyBridge>()`.
Host adapters implement `INativeWindow` by deriving from `Win32NativeWindowBase` (all members implemented over
`Win32WindowController`; the host raises `OnStateChanged`/`OnBoundsChanged`/`OnFocusChanged`/`OnVisibilityChanged`/`OnClosing`/`OnClosed`).
`Win32WindowController.SetState(Normal)` restores a minimized window to its normal bounds even when it was maximized
before it was minimized (`SetWindowPlacement` with `SW_SHOWNORMAL`), where a plain `SW_RESTORE` would maximize it again.

## Capabilities

| Capability | Implementation | Status / features |
| --- | --- | --- |
| `desktop.screens` | `EnumDisplayMonitors`, `GetMonitorInfoW`, `GetDpiForMonitor` | Available. DIP bounds = physical / monitor scale; id = `\\.\DISPLAYn` |
| `desktop.global-shortcuts` | `RegisterHotKey` (`MOD_NOREPEAT`) on a message-only window | Available. Taken gestures fail with `operation.conflict` |
| `desktop.tray` | `Shell_NotifyIconW`, `NOTIFYICON_VERSION_4` | Available. Click, double click, context menu; survives Explorer restarts |
| `desktop.menus` | Win32 menus: `TrackPopupMenuEx`, `SetMenu` + comctl32 subclass | Available. Context menus and window menu bars |
| `secure-storage` | DPAPI (`CurrentUser`), one file per key | Available, `OperatingSystem` protection |
| `app-storage` | Package folders (MSIX) or `%LOCALAPPDATA%\{AppId}`; known folders | Available |
| `biometrics` | Windows Hello (`UserConsentVerifier`), prompt parented to the main window | Available / Unavailable with `hardware.unavailable`, `biometric.not-enrolled`, `permission.restricted` |
| `clipboard` | Win32 clipboard (`CF_UNICODETEXT`) on the STA message thread | `read-text`, `write-text`, `change-notification` |
| `files` | WinRT `FileOpenPicker` / `FileSavePicker` / `FolderPicker` | `multiple`, `save-dialog`, `folder-picker`; Unavailable (`windows.elevated-process`) when elevated |
| `camera` | Media picking through the file picker | `library`, `multiple-selection`; capture is **Unsupported** |
| `barcode` | — | **Unsupported**: no built-in decoder; register a ZXing.Net-based `IBarcodeScanner` |
| `share` | `IDataTransferManagerInterop` (`ShowShareUIForWindow`) | `text`, `uri`, `files`; no completion reporting |
| `notifications` | WinRT toasts (`ToastNotificationManager`) | `scheduled`, `actions`, `text-input`, `activation-routing` (no repeating, badges or channels) |
| `connectivity` | `NetworkInformation` internet profile + `NetworkStatusChanged` | Detects captive portals, metered (fixed/variable cost) and data saver |
| `permissions` | `AppCapability.CheckAccess` (camera, microphone), toast setting | Request returns the current state (see below) |

Not provided here: `desktop.windows` (host packages), and IPC, single instance, shell file operations and protocol
registration (`BlazorNativeSdk.Desktop`).

## Threading model

A dedicated STA thread runs a Win32 message loop that owns two hidden windows: a message-only window (hot keys,
clipboard listener, work marshalling) and a never-shown top-level tool window (tray icons and their menus — message-only
windows neither receive the `TaskbarCreated` broadcast nor can become foreground). All public members can be called from
any thread. Hot key, tray and clipboard callbacks are raised on the thread pool; exceptions in them are logged.
Pickers, menu bars, the share UI and Windows Hello run on the host's UI thread through the bridge.

## Packaged (MSIX) vs unpackaged

| Area | Unpackaged | Packaged |
| --- | --- | --- |
| App data / cache / temp | `%LOCALAPPDATA%\{AppId}`, `...\Cache`, `%TEMP%\{AppId}` | `ApplicationData.Current` local, local-cache and temporary folders |
| Secure storage | `%LOCALAPPDATA%\{AppId}\securestorage` | package local folder `\securestorage` |
| Toast identity | AUMID registered under `HKCU\Software\Classes\AppUserModelId\{AUMID}` (`DisplayName`, `IconUri`) and set with `SetCurrentProcessExplicitAppUserModelID` | package identity; no registry work |
| URL scheme for toast activation | register at runtime (`IProtocolRegistrationService`, Desktop package) | declare `windows.protocol` in the manifest |
| Camera/microphone consent | the "Let desktop apps access..." switches | per-app consent |

The AUMID is `WindowsNativeOptions.AppUserModelId ?? NativeSdkOptions.AppId` (spaces replaced, max 128 characters).
Setting it on the process also changes taskbar grouping, so resolve `INotificationService` before showing the first
window, or set `SetProcessAppUserModelId = false`. Set `RegisterAppUserModelId = false` to manage the registry yourself.

## Notification activation

Clicks are delivered as `AppActivation { Kind = Notification, NotificationId, ActionId, Route, Data, UserText }`
through `IActivationPublisher`, so Core routes them like any other activation (cold-start buffering included).

* **In-process (always):** the service keeps the most recent `ToastNotification` objects and handles their `Activated`
  event while the process that showed them runs. Typed replies (`AcceptsText`) are only delivered this way. In-process
  activation arguments are therefore only delivered for toasts shown by the running process: a toast shown by an
  earlier run or by another instance reaches the app only through protocol activation (below).
* **Protocol (`NotificationActivationScheme` set):** toasts and buttons use `activationType="protocol"` with
  `{scheme}://bns-toast/?nid=…&aid=…&route=…&d.key=…&sig=…`. Clicking launches the app through its registered scheme, or
  — with the Desktop package's single-instance service — reaches the running instance, which publishes the URL. The
  adapter registers an `IActivationTranslator` that turns these URLs back into notification activations
  (`IsColdStart` is preserved). The URL is signed with HMAC-SHA256 using a per-user key stored DPAPI-protected in the app
  data folder (`notifications\toast-activation.key`), so links crafted by web pages or other programs are not accepted
  as notification activations; they pass through as ordinary deep links subject to `DeepLinkOptions`. A key file that
  cannot be read right now (locked by a backup or antivirus scanner) is retried with a short backoff and never
  replaced; only a file that is definitively invalid (wrong length, or DPAPI cannot decrypt it) is regenerated.
* **No scheme:** clicks on toasts in the notification center after the app exited do nothing.

Scheduled toasts (`ScheduleAsync`) are delivered by Windows even when the app is not running; use a scheme so their clicks
reach the app. `Repeat` other than `None` returns `Unsupported` (schedule each occurrence). Importance maps to
`SuppressPopup` + silent audio (Low), default, and high priority + `scenario="urgent"` on Windows 11 22H2+ (High).
`Group` and `BadgeNumber` are ignored; `CreateChannelAsync` succeeds as a no-op. Cancellation uses the toast history and
the scheduled list (ids longer than 64 characters are hashed into the tag).

## Permissions

Windows desktop apps have no prompt API: camera/microphone consent is asked by Windows when a capture device is first
opened, and toasts are a Settings switch. `RequestAsync` returns the same value as `CheckAsync`; `OpenAppSettingsAsync`
opens `ms-settings:privacy-webcam`, `privacy-microphone` or `notifications` for the most recently denied permission (else
`ms-settings:privacy`). Storage, photos, biometrics, clipboard and exact alarms report `NotRequired`; unknown permissions
report `Unsupported`.

## Limitations

* Camera capture is unsupported (no system capture UI for desktop apps): capture in the WebView with `getUserMedia`
  (Browser adapter) or a custom page. Barcode scanning needs your own `IBarcodeScanner`.
* Pickers cannot run in elevated processes and need a main window; dialog titles are ignored.
* Menu bars appear in the standard non-client area: WPF `WindowChrome` windows and WinUI/MAUI windows that extend content
  into the title bar hide them. Accelerators are displayed, not registered (keys go to the WebView). Edit roles send
  `WM_COPY`/`WM_PASTE`/... to the focused native control, which WebView content generally ignores — handle them in the page
  via `ItemInvoked` if needed. `Quit` posts `WM_CLOSE` to the main window.
* Windows Hello always allows the PIN (`AllowDeviceCredential` cannot be disabled), cannot report which method was used
  (`Other`) and does not support a custom cancel label.
* Share UI does not report completion; non-file-system files are copied to `%TEMP%\BlazorNativeSdk\share` (cleaned after an hour).
* Full screen switches the window to `WS_POPUP` covering its monitor; frameworks that re-apply styles may need a
  framework-native override in the host's `Win32NativeWindowBase` subclass.
* DPAPI values are bound to the Windows user and machine; they do not roam and are lost if an administrator resets the
  user's password.
