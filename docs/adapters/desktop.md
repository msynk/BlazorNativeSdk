# Desktop adapter (`BlazorNativeSdk.Desktop`)

Host-neutral desktop services for Windows, macOS and Linux: local IPC, single-instance enforcement with argument and
deep-link forwarding, shell file operations and runtime URL-scheme registration. It works with any desktop host (WPF,
WinForms, MAUI on Windows/Mac Catalyst, Photino, custom hosts). Host packages declare the host; this package does not.

## Setup

```csharp
services.AddBlazorNativeSdk(sdk =>
{
    sdk.Configure(o => o.AppId = "com.contoso.notes");          // names pipes, sockets, locks, registry keys
    sdk.ConfigureDeepLinks(d => d.Schemes.Add("contoso-notes"));
    sdk.AddDesktop(o => o.AllowedUriSchemes.Add("vscode"));     // then a host package, e.g. sdk.AddWpf()
});
```

`AddDesktop()` also registers `PhysicalAppStorage`, the file state store, system connectivity, the in-process work
scheduler and HTTP transfers (both keep running while the app is in the background).

Single instance belongs at the very start of `Main`, before any window is shown:

```csharp
var single = provider.GetRequiredService<ISingleInstanceService>();
var lease = (await single.AcquireAsync(args)).GetValueOrThrow();
if (!lease.IsPrimary) { return; }          // arguments were forwarded (lease.ArgumentsForwarded)
// keep `lease` alive for the lifetime of the app; dispose it on exit
```

| `DesktopNativeOptions` | Default | Purpose |
| --- | --- | --- |
| `AllowedUriSchemes` | empty | Extra schemes `OpenUriAsync` may launch (http/https/mailto are always allowed) |
| `AllowOpeningExecutables` | `false` | Let `OpenPathAsync` open executables, scripts, shortcuts, `.app` bundles |
| `SingleInstanceChannel` | `single-instance` | IPC channel of the primary instance |
| `SingleInstanceConnectTimeout` | 5 s | How long a secondary keeps trying to reach a starting primary |
| `SingleInstanceLockDirectory` | `{AppData}/locks` | Lock-file directory (must be local and per-user) |
| `PublishLaunchActivation` | `true` | Primary publishes its own args as cold-start activations |
| `SingleInstanceSecurity` | 256 KB limit | IPC security of the single-instance channel (`CurrentUserOnly` is forced) |
| `MaxForwardedArguments` | 512 | Upper bound on forwarded arguments |
| `IpcConnectTimeout` / `IpcRequestTimeout` / `IpcWriteTimeout` | 2 s / 30 s / 10 s | IPC defaults |
| `IpcMaxConnections` / `IpcMaxConcurrentRequestsPerConnection` | 64 / 16 | Server resource limits |
| `OverwriteForeignProtocolRegistrations` | `false` | Allow replacing a scheme owned by another app |

## Capability matrix

| Capability | Windows | macOS | Linux |
| --- | --- | --- | --- |
| `IIpcService` | Named pipes | Unix domain sockets | Unix domain sockets |
| — `SharedSecret` auth + MAC | ✓ | ✓ | ✓ |
| — `AllowedClientExecutables` | ✓ (`GetNamedPipeClientProcessId`) | Unsupported¹ | Unsupported¹ |
| — peer process id / path | ✓ / ✓ | ✓ (`LOCAL_PEERPID`) / best effort | ✓ (`SO_PEERCRED`) / `/proc/{pid}/exe` |
| `ISingleInstanceService` | ✓ | ✓ | ✓ |
| `RevealInFileManagerAsync` | `SHOpenFolderAndSelectItems` (fallback `explorer /select,`) | `open -R` | `FileManager1.ShowItems` via `gdbus`, else `xdg-open` on the parent |
| `OpenPathAsync` / `OpenUriAsync` | `ShellExecuteEx` | `open` | `xdg-open` (Unavailable if xdg-utils missing) |
| `MoveToTrashAsync` | `SHFileOperationW` + `FOF_ALLOWUNDO` | `NSFileManager trashItemAtURL` (fallback `~/.Trash`) | `gio trash`, else FreeDesktop.org Trash spec |
| `IProtocolRegistrationService` | `HKCU\Software\Classes` (Unsupported when MSIX-packaged²) | Unsupported³ | `~/.local/share/applications` + `xdg-mime` (Unsupported in Flatpak/Snap²) |

¹ No OS facility reports the connecting process reliably enough to enforce it; `StartServerAsync` returns Unsupported
rather than silently ignoring the list. Use a `SharedSecret`. ² Declare the protocol in the package manifest.
³ Declare `CFBundleURLTypes` in `Info.plist`.

## IPC

**Naming.** Endpoints are `bns-{appId.channel (sanitized, truncated)}-{hash}` where the 64-bit hash covers the
untruncated app id, channel and — for `CurrentUserOnly` channels — the user identity (Windows SID / Unix euid), so
truncation never causes collisions and users never share an endpoint. Unix sockets live in a private `bns-{user}`
directory (0700) under `$XDG_RUNTIME_DIR`, `$TMPDIR` or `/tmp`: the first one that keeps the path within the 104-byte
`sun_path` limit and whose directory either does not exist yet or is a real directory owned by the user with mode 0700
(checked with `lstat`). A directory another user planted in a shared root, or a symbolic link, is skipped for the next
root, never reused. `CurrentUserOnly = false` channels are machine-wide and live in `/tmp` (socket mode 0666); they
require a `SharedSecret` (`InvalidArgument` otherwise), because any local user can connect to them or claim their name
first. Channel lock files must be regular files owned by the user: symbolic links are refused before opening and the
opened file's owner is verified (`fstat`), failing closed.

**Wire format.** 4-byte little-endian length + UTF-8 JSON envelope
`{kind, type, payload, correlationId, seq, mac}` (`kind`: `request|response|oneway|push|error`, plus
`hello|auth|welcome` during the handshake). The length is checked against `MaxMessageBytes` before any allocation,
and the announced length is never allocated up front: the body is read into a buffer that starts at 16 KB and grows only
as bytes arrive, so a peer must actually send what it claims. Frames before authentication are capped at 4 KB and the
handshake must finish within `HandshakeTimeout`.

**Handshake (with `SharedSecret`, ≥ 16 bytes).** Server sends nonce Ns; client replies with nonce Nc and
`HMAC-SHA256(K, "bns-ipc-v1|channel|Ns|Nc|client")`; server verifies and answers with the `…|server` proof; both
derive a session key `HKDF-SHA256(K, salt = Ns‖Nc, info = "bns-ipc-v1|session|channel")`. Every later frame carries
`HMAC(session key, direction ‖ seq ‖ kind ‖ type ‖ payload ‖ correlationId)` (length-prefixed fields) and must carry
exactly the next sequence number, so tampered, reflected, replayed, reordered or dropped frames close the connection.
All comparisons use `CryptographicOperations.FixedTimeEquals`. A side configured with a secret refuses a peer that does
not authenticate (no downgrade).

**Semantics.** Requests run concurrently per client (bounded, with back-pressure); one-way messages run in order. A
handler exception returns `Failed` (`ipc.handler-failed`) to the client without the exception text and keeps the
connection open. Pending requests fail with `ipc.disconnected` when the connection drops. A client is in
`IIpcServer.Peers` (and receives broadcasts) as soon as `ConnectAsync` returns. A peer that stops reading is
disconnected after `IpcWriteTimeout`. Starting a second server on a served channel returns `Conflict`.

### Threat model

| Threat | Mitigation |
| --- | --- |
| Other local users connecting | Windows: protected DACL granting only the user SID; Unix: 0700 directory + kernel peer-uid check on both ends |
| Remote (SMB) clients | `PIPE_REJECT_REMOTE_CLIENTS` |
| Pipe/socket squatting by another user | Windows: `FILE_FLAG_FIRST_PIPE_INSTANCE`, next instance always created before handing one out, clients verify the pipe owner SID; Unix: channel lock file (no symlinks, owner verified) + `lstat`-verified private directory (foreign directories skipped) + peer uid check; machine-wide channels require a `SharedSecret` |
| Malicious server impersonating the client | Clients connect with `SecurityIdentification` impersonation level |
| Same-user process without the secret | `SharedSecret` mutual authentication + per-message MAC |
| Memory exhaustion / slowloris | Size check before allocation, body buffers that grow with received data, 4 KB pre-auth frames, handshake timeout, connection and in-flight request limits, write timeout |
| Replay / reordering / reflection | Per-direction sequence numbers and direction byte inside the MAC |

Out of scope: code running as the same user that can read the secret from your process or config, and administrators/root.
`AllowedClientExecutables` is defense in depth (PID-based), not a boundary against same-user code. The client's path is
read through a process handle whose creation time must not be later than the moment the connection was accepted, so a
process that reused the id of an exited client is not mistaken for it.

## Single instance and deep links

1. `AcquireAsync(args)` tries to take an exclusive lock on `{lockDir}/bns-{appId.channel}-{hash}.lock` (Windows:
   `FileShare.None`; Unix: `FileStream` plus an explicit `flock(LOCK_EX|LOCK_NB)`, which keeps working when
   `DOTNET_SYSTEM_IO_DISABLEFILELOCKING` is set and fails closed on file systems without locks). The OS releases the
   lock when the process exits or crashes, so there are no abandoned locks; the file itself is never deleted (avoids the
   unlink race). A named mutex was not used because it is Windows-only and needs explicit abandoned-mutex handling.
2. **Primary**: starts a `CurrentUserOnly` IPC server on `SingleInstanceChannel`, returns `IsPrimary = true` and (by
   default) publishes its own arguments as cold-start activations.
3. **Secondary**: connects (retrying while the primary starts, up to `SingleInstanceConnectTimeout`), sends its
   arguments and working directory, and returns `IsPrimary = false`, `ArgumentsForwarded = true|false`.
4. The primary maps arguments with `ActivationArguments.Parse` and publishes them through `IActivationPublisher`:
   allow-listed custom-scheme or `https` host URLs → `DeepLink`; existing files (relative paths resolved against the
   secondary's working directory) → one `File` activation; otherwise one `SecondaryInstance` activation. The Core
   activation service then routes them like any other deep link. Non-allow-listed URLs never become deep links.
   When the first argument is a URL with an allow-listed custom scheme (a protocol launch, `"exe" "%1"`), it is the
   only argument used: anything after it can only come from quotes injected into a crafted URL, so it is ignored and a
   warning is logged. On Windows, UNC and device paths (`\\server\share\…`, `//server/…`, `\\?\UNC\…`, `\\.\…`) are
   never probed as files (probing would send the user's credentials to the server); they stay plain arguments.

**Trust model.** Single-instance forwarding accepts arguments from any process running as the same OS user: the pipe
DACL (Windows) or the private socket directory and peer-uid check (Unix) keep other users out, so the OS user is the
trust boundary, and a same-user process can forward arbitrary arguments to the running instance. To require
authentication, set `SingleInstanceSecurity.SharedSecret` (every instance must know it, for example from protected
per-user storage). Forwarded deep links are still filtered by `DeepLinkOptions`: only allow-listed schemes and hosts become
deep links, and Core drops absolute URLs that match neither unless `DeliverUnrecognizedLinks` is set. Forwarded file
paths are only paths: treat the files' contents as untrusted input.

Disposing the lease stops the server first, then releases the lock. Register custom schemes with
`IProtocolRegistrationService` (unpackaged Windows, Linux) so the OS launches your executable with the URL as an
argument, and add them to `DeepLinkOptions.Schemes`.

## Shell and protocol safety

- Helper tools run without a shell, with each argument in `ProcessStartInfo.ArgumentList`, resolved from absolute
  `PATH` entries only; `gdbus` receives a fully percent-encoded file URI.
- `OpenUriAsync` launches only http/https/mailto plus `AllowedUriSchemes`; `file`, `javascript`, `vbscript` and `data`
  are always refused. The escaped `AbsoluteUri` is what gets launched.
- `MoveToTrashAsync` on Windows sets `FOF_WANTNUKEWARNING`: if an item cannot be recycled (network share, too large),
  Windows asks before deleting it permanently; declining returns `Cancelled`.
- On Windows, paths passed to `OpenPathAsync`, `RevealInFileManagerAsync` and `MoveToTrashAsync` are rejected with
  `InvalidArgument` when they are UNC or device paths (`\\server\share`, `//server/share`, `\\?\UNC\…`, `\\.\…`,
  reserved names such as `NUL`) or use alternate data stream syntax (`file:stream`), before anything touches the file
  system. Trailing dots and spaces, which Win32 strips (`run.exe. .` opens `run.exe`), are removed before the executable
  check.
- Protocol registration validates schemes per RFC 3986, refuses browser/OS schemes (`http(s)`, `file`, `mailto`,
  `javascript`, `data`, `ms-*`, `microsoft*`, `search-ms`, `shell`, …), requires `%1` to be quoted (`"%1"`) to prevent
  argument injection, refuses the `dotnet` host as handler, and never replaces another app's registration unless
  `OverwriteForeignProtocolRegistrations` is set. Still validate incoming URLs in your app.

## Limitations

- `AllowedClientExecutables` is enforced on Windows only.
- Machine-wide IPC channels (`CurrentUserOnly = false`) can be squatted by any local user, so they require a
  `SharedSecret`: a squatter cannot authenticate, but it can still block the channel (denial of service). On Unix a
  crashed machine-wide server owned by another user blocks the channel until its files in `/tmp` are removed.
- Single-instance scope is per user across sessions (lock in the user's app data, pipe names are global on Windows).
  Lock directories on network file systems are not supported.
- Shell operations accept only local paths on Windows (UNC and device paths are rejected); mapped network drives
  (`Z:\`) are local paths to Windows and are allowed.
- Linux trash fallback supports the home trash and `$topdir/.Trash-$uid` (not `$topdir/.Trash/$uid`); it does not
  update `directorysizes`. An existing `$topdir/.Trash-$uid` that is not a real directory owned by the user with mode
  0700 (for example one created by another user) is skipped and the home trash is used instead; trashing a directory
  across file systems then fails rather than writing into a foreign directory. macOS `~/.Trash` fallback (used only if Foundation cannot be loaded) does not record
  "Put Back" information.
- Linux unregistration deletes the desktop entry; `xdg-mime` has no "unset", so `mimeapps.list` keeps a stale default
  that desktops ignore.
- macOS and Linux code paths are unit-tested where they are pure, but the automated suite currently runs on Windows.
