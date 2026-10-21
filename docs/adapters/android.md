# Android adapter (`BlazorNativeSdk.Android`)

Android implementations of the BlazorNativeSdk capabilities for .NET MAUI Blazor Hybrid apps and for .NET for Android
apps that initialize Microsoft.Maui.Essentials. Target framework `net10.0-android`, minimum Android 7.0 (API 24).
Root namespace `BlazorNativeSdk.AndroidPlatform`; the registration method lives in `BlazorNativeSdk`.

## Setup in a MAUI Blazor Hybrid app

```xml
<PackageReference Include="BlazorNativeSdk.Android" Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'android'" />
```

```csharp
// MauiProgram.cs
builder.Services.AddBlazorNativeSdk(sdk =>
{
#if ANDROID
    sdk.AddAndroid(o =>
    {
        o.NotificationSmallIcon = "ic_stat_notify";     // Resources/drawable/ic_stat_notify.xml (white silhouette)
        o.DefaultChannelName = "General";
    });
#endif
    sdk.ConfigureDeepLinks(d =>
    {
        d.Schemes.Add("myapp");        // myapp://orders/42 -> /orders/42
        d.Hosts.Add("contoso.com");    // https://contoso.com/orders/42 -> /orders/42 (App Links)
    });
    sdk.AddBackgroundWorker<SyncWorker>("sync");
});
```

Add `<NativeActivationOutlet />` to the layout so buffered activations (cold-start notification taps and links) are
routed once the UI is ready.

The MAUI integration package wires the activity events and starts the SDK. Without it, start the SDK when the app is
created (so WorkManager workers and notification receivers find the service provider in a process the OS started) and
forward intents from `MainActivity`:

```csharp
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable], DataScheme = "myapp")]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "https", DataHost = "contoso.com", AutoVerify = true)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (savedInstanceState is null)   // a restored activity must not replay its launch intent
        {
            AndroidActivation.HandleIntent(Intent, isColdStart: true);
        }
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        // When the OS killed the process in the background, a notification tap or link re-creates this activity from
        // saved state and arrives here before OnResume: pass isColdStart: true for that first intent of the process.
        AndroidActivation.HandleIntent(intent, isColdStart: false);
    }
}

// MainApplication.cs
public override void OnCreate()
{
    base.OnCreate();                                   // builds the MAUI app and its service provider
    _ = IPlatformApplication.Current!.Services.StartBlazorNativeSdkAsync();
}
```

`HandleIntent` handles each intent once (it marks the intent and ignores relaunches from the recents screen), verifies
notification intents (see *Security*) and publishes through `IActivationPublisher`; activations that arrive before the
SDK started are buffered.

### Plain .NET for Android apps

Use `AddAndroid(o => o.HostKind = NativeHostKind.Custom)` and, in the launcher activity (an AndroidX `AppCompatActivity`):

* call `Microsoft.Maui.ApplicationModel.Platform.Init(this, savedInstanceState)` in `OnCreate`;
* forward `OnRequestPermissionsResult` to `Platform.OnRequestPermissionsResult(...)` (otherwise permission requests never complete);
* call `AndroidActivation.HandleIntent` as shown above and `StartBlazorNativeSdkAsync()` in `Application.OnCreate`.

## AndroidManifest.xml

Declare only what the capabilities you use need:

```xml
<manifest xmlns:android="http://schemas.android.com/apk/res/android">
  <!-- Camera capture (Essentials checks it even though the camera app takes the picture) -->
  <uses-permission android:name="android.permission.CAMERA" />
  <!-- Notifications (Android 13+ runtime permission) -->
  <uses-permission android:name="android.permission.POST_NOTIFICATIONS" />
  <!-- Re-arm scheduled notifications after reboot -->
  <uses-permission android:name="android.permission.RECEIVE_BOOT_COMPLETED" />
  <!-- Optional: exact scheduled notifications (user-granted on Android 12+; Play policy restricts USE_EXACT_ALARM) -->
  <uses-permission android:name="android.permission.SCHEDULE_EXACT_ALARM" />
  <!-- Connectivity -->
  <uses-permission android:name="android.permission.ACCESS_NETWORK_STATE" />
  <!-- Optional: NativePermissions.Photos (Android 13+, plus partial access on 14+) and legacy storage -->
  <uses-permission android:name="android.permission.READ_MEDIA_IMAGES" />
  <uses-permission android:name="android.permission.READ_MEDIA_VISUAL_USER_SELECTED" />
  <uses-permission android:name="android.permission.READ_EXTERNAL_STORAGE" android:maxSdkVersion="32" />
  <uses-permission android:name="android.permission.WRITE_EXTERNAL_STORAGE" android:maxSdkVersion="28" />

  <!-- Android 11+ package visibility for camera capture -->
  <queries>
    <intent><action android:name="android.media.action.IMAGE_CAPTURE" /></intent>
    <intent><action android:name="android.media.action.VIDEO_CAPTURE" /></intent>
  </queries>

  <application android:label="@string/app_name" android:fullBackupContent="@xml/backup_rules"
               android:dataExtractionRules="@xml/data_extraction_rules">
    <!-- Optional: download the Google code scanner module at install time -->
    <meta-data android:name="com.google.mlkit.vision.DEPENDENCIES" android:value="barcode_ui" />
  </application>
</manifest>
```

* **Receivers** (`NotificationActionReceiver`, `ScheduledNotificationReceiver`, `BootCompletedReceiver`, all
  `exported="false"`) and the WorkManager worker (`blazornativesdk.androidplatform.NativeSdkWorker`) are declared
  automatically from attributes; do not rename or remove them.
* **Deep links / App Links**: add intent filters as in the `MainActivity` example. For verified `https` links publish
  `https://contoso.com/.well-known/assetlinks.json` with the app's signing-certificate SHA-256 and set `AutoVerify = true`.
  Only schemes and hosts listed in `DeepLinkOptions` are mapped to routes.
* **Backups**: Keystore keys are not backed up, so a restored copy of the Essentials secure-storage file cannot be
  decrypted. Exclude it in both rule files: `<exclude domain="sharedpref" path="{your.package}.microsoft.maui.essentials.preferences.xml" />`.

## Capability matrix

| Capability | Implementation | Features reported | Notes |
| --- | --- | --- | --- |
| Permissions | Essentials `Permissions` + custom runtime permissions | `rationale`, `app-settings` | `NotDetermined` until first requested; undeclared permissions → `Unavailable`/`app.missing-declaration`; any `android.permission.*` name accepted. |
| Camera | Essentials `MediaPicker` (camera app, photo picker) | `photo`, `video`, `library`, `multiple-selection` | No camera → capture `Unavailable`/`hardware.unavailable`, library still works. Camera facing and video duration are not enforceable. |
| Barcode | Google code scanner (Play services UI) | `live-scan`, `permissionless-scan` | `DetectAsync` is `Unsupported` (needs the bundled ML Kit model). `Prompt`, `Camera`, `Timeout` are ignored. |
| Files | Essentials `FilePicker`; SAF `ACTION_CREATE_DOCUMENT` | `multiple`, `save-dialog` | `PickFolderAsync` is `Unsupported` (tree URIs do not fit `PickedFolder`); use `AndroidFilePickerService.PickFolderTreeUriAsync`. |
| App storage | Essentials `FileSystem` | – | `Documents`/`Downloads` → `Unsupported` (scoped storage). |
| Secure storage | Essentials `SecureStorage` (EncryptedSharedPreferences + Keystore) | `android-keystore` | `Protection = OperatingSystem` (hardware backing not verified per device). |
| Share | Essentials `Share` (chooser) | `text`, `uri`, `files` | No completion reporting; text is not attached when sharing files. |
| Clipboard | Essentials `Clipboard` | `read-text`, `write-text`, `change-notification` | Android 10+: read and change events only while focused. |
| Biometrics | AndroidX `BiometricPrompt` | `fingerprint`, `face`, `iris`, `device-credential` | Strong + device credential cannot be combined on Android 9–10 (credential dropped). |
| Notifications | `NotificationCompat`, `AlarmManager`, receivers | `scheduled`, `repeating`, `actions`, `text-input`, `channels`, `activation-routing` | Max 3 actions; importance is per channel on 8.0+; badges are launcher-specific (not reported). |
| Deep links | Core `ActivationService` + `AndroidActivation` | `routing`, `custom-scheme`, `verified-links` | Notification intents are HMAC-verified. |
| Lifecycle | Activity lifecycle callbacks, `onTrimMemory` | `BestEffort` | `Terminating` only when the last activity finishes. |
| Connectivity | Essentials `Connectivity` + `ConnectivityManager` | `change-notification`, `metered`, `data-saver` | Needs `ACCESS_NETWORK_STATE`. |
| Background work | AndroidX WorkManager (`UseWorkManager = false` → Core in-process, foreground only) | `persisted`, `runs-in-background`, `runs-after-termination`, `survives-reboot` | See below. |
| Transfers | Core `HttpTransferService` driven by WorkManager | `resume-downloads`, `retry`, `persisted`, `continues-in-background`, `continues-after-termination` | WorkManager work `bns.transfers` keeps the queue running in the background and after the process was killed (see below). With `UseWorkManager = false`: in process, foreground only. |
| Workflow checkpoints | Core, file state store in `{AppData}/bns-state` | – | |

## Background execution: guarantees and limits

| | Guarantee | Limit |
| --- | --- | --- |
| WorkManager work | Persisted; runs in the background, after the app is closed and after reboots; constraints (network, unmetered, charging, battery/storage not low, idle) enforced by the OS. | Never exact (Doze, App Standby buckets, battery saver). Periodic ≥ 15 min (clamped). ~10 min per run, then the worker's token is cancelled. Exponential backoff between 10 s and 5 h (clamped); `MaxAttempts` caps retries. Input/output ≤ 10 KB. Nothing runs after a force-stop until the user opens the app. |
| Process start for work | The OS starts the process and `NativeSdkWorker` waits up to `WorkerStartupTimeout` (20 s) for the SDK to start, then retries. | The app must start the SDK at application creation (MAUI integration does). |
| `WorkChanged` | Raised for schedule/cancel and for runs in a process where the scheduler exists. | Runs in other processes are visible through `GetWorkAsync`. |
| Lifecycle lease | `RequestBackgroundTimeAsync` returns a `BestEffort` lease that never expires. | Android grants no background time: the process can be frozen or killed at any time in the background. Use WorkManager for work that must finish. |
| Scheduled notifications | Persisted; survive process death, app updates and reboots (`RECEIVE_BOOT_COMPLETED`); repeats keep local time across DST/time-zone changes. | Exact only with `SCHEDULE_EXACT_ALARM` granted; otherwise the OS may defer delivery (up to ~1 h in Doze). Force-stop clears alarms until the next launch (re-armed when the SDK starts). |
| HTTP transfers | Persisted. While any transfer is unfinished, one-time WorkManager work `bns.transfers` (network required) is kept scheduled; its runs keep the queue moving while the app is in the background and after the OS killed the process, and survive reboots. A run ends when every transfer finished (success) or the rest wait for the network or a later retry (WorkManager retries with exponential backoff from 30 s). | Each run is capped at about 10 minutes; the next run resumes downloads from the last byte (`Range`/`If-Range`), uploads restart from the beginning. Doze and App Standby defer runs. Nothing runs after a force-stop until the app is opened again. With `UseWorkManager = false` transfers only run while the app is in the foreground. |
| Background notification actions | Delivered to activation handlers when the app is running. | If Android started the process only for the broadcast and no UI attaches, the activation is not persisted; use `OpensApp = true` for actions that must be handled. |

## Barcode scanning and Google Play services

`IBarcodeScanner` uses the Google code scanner (`play-services-code-scanner`): Google Play services shows the camera UI,
so the app needs no `CAMERA` permission and no camera code. It requires Google Play services; devices without it (some
tablets, Huawei/HarmonyOS, Amazon Fire, AOSP builds) report `Unavailable` with `android.play-services-unavailable`. The
scanner module is downloaded on first use (or at install time with the `barcode_ui` meta-data); until then scans return
`Unavailable` with `android.module-unavailable` and the adapter requests the download in the background
(`InstallBarcodeScannerModule`). For devices without Play services or for image detection, register your own
`IBarcodeScanner` (ML Kit bundled model, CameraX, ZXing.Net) after `AddAndroid()`.

## Security

* PendingIntents are `FLAG_IMMUTABLE`, except inline-reply actions, which must be `FLAG_MUTABLE` on Android 12+.
* Receivers are not exported. The launcher activity is exported, so notification intents carry an HMAC-SHA256 token
  (per-install key in private preferences) over id, action, route and data; forged notification extras are ignored.
* Deep links are untrusted input: only allow-listed schemes/hosts become routes (Core `DeepLinkOptions`).
* Logs never contain clipboard contents, secure-storage values or file contents.

## Error codes

In addition to `NativeErrorCodes`: `android.play-services-unavailable`, `android.module-unavailable`,
`android.no-handler` (no app handles a system intent, for example no document provider). See `AndroidErrorCodes`.
