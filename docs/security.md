# Security model

| Area | Design |
| --- | --- |
| Deep links | URLs become app routes only when their scheme or host is allow-listed in `DeepLinkOptions`. Explicit routes must be app-relative: a leading `/`, no `//`, no scheme, no control characters. Deep links whose URL matches no allowed scheme or host are dropped before any handler sees them (opt in with `DeliverUnrecognizedLinks`), because on Android and desktop any local app can send arbitrary URLs. Platform-specific encodings (signed toast and notification intents) are verified by translators, and forged ones are dropped. Validate route parameters as you would any user input. |
| Secure storage | Keychain (iOS), Android Keystore with EncryptedSharedPreferences, and DPAPI (Windows). Browsers have no API that can hide a secret from scripts running in the page, so browser secure storage is **off by default**. When you enable it, it uses a non-extractable WebCrypto key and reports `SecureStorageProtection.BestEffort`. |
| Biometrics | Local verification gates local functionality only. It proves nothing to a server. For server trust, use passkeys (WebAuthn) with server-side verification. |
| IPC | Local only: named pipes or Unix domain sockets restricted to the current user by the OS. An optional shared secret enables a mutual HMAC-SHA256 handshake, per-session keys and per-message MACs with sequence numbers (replay protection); machine-wide channels (`CurrentUserOnly = false`) require it. An optional executable allow-list is available on Windows. Size limits are enforced before allocation, and frame buffers grow only with the data actually received. On Unix, socket directories and lock files must be owned by the user (foreign directories are skipped, symbolic links refused). |
| Single instance | Arguments forwarded by a second instance are accepted from any process of the same OS user (the trust boundary); set `SingleInstanceSecurity.SharedSecret` to require authentication. Forwarded URLs still pass the `DeepLinkOptions` filter, and protocol launches use only the URL argument. |
| Opening URLs and files | `IDesktopFileOperations.OpenUriAsync` allows only `http`, `https` and `mailto` unless you opt in to other schemes. Processes are started with argument lists, never through a shell. On Windows, UNC and device paths are rejected before any file system access (probing them would send the user's credentials to a server), and executables are detected after Win32 name normalization. |
| Protocol registration | Reserved schemes (`http`, `file`, `javascript`, `ms-*`, ...) are refused. |
| Persisted state | Work queues, transfer state and checkpoints are stored unencrypted. Request headers in persisted transfers should hold short-lived tokens only. |
| Logging | Adapters never log secrets, clipboard contents or file contents. |

Report vulnerabilities privately to the maintainers. Don't open public issues for security problems.
