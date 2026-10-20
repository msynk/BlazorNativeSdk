namespace BlazorNativeSdk.Desktop;

/// <summary>Options for the host-neutral desktop services registered by <c>AddDesktop()</c>.</summary>
public sealed class DesktopNativeOptions
{
    /// <summary>
    /// URI schemes that <see cref="IDesktopFileOperations.OpenUriAsync"/> may launch in addition to <c>http</c>,
    /// <c>https</c> and <c>mailto</c>, such as <c>tel</c> or <c>vscode</c>. Comparison is case-insensitive.
    /// </summary>
    /// <remarks>
    /// Every allowed scheme hands the URI to whatever application the user (or an installer) registered for it, so only
    /// add schemes whose handlers you trust with untrusted input. <c>file</c>, <c>javascript</c>, <c>vbscript</c> and
    /// <c>data</c> are always rejected; use <see cref="IDesktopFileOperations.OpenPathAsync"/> for local files.
    /// </remarks>
    public ISet<string> AllowedUriSchemes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Allow <see cref="IDesktopFileOperations.OpenPathAsync"/> to open executables, scripts, shortcuts and application
    /// bundles (which <em>runs</em> them). Default: <see langword="false"/>, so a path that came from untrusted input cannot
    /// start a program.
    /// </summary>
    public bool AllowOpeningExecutables { get; set; }

    /// <summary>The IPC channel used by <see cref="ISingleInstanceService"/>. Default: <c>single-instance</c>.</summary>
    public string SingleInstanceChannel { get; set; } = "single-instance";

    /// <summary>
    /// How long a secondary instance keeps trying to reach the primary instance (which may still be starting) before it
    /// gives up forwarding its arguments. Default: 5 seconds.
    /// </summary>
    public TimeSpan SingleInstanceConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The directory that holds single-instance lock files. Default: <see langword="null"/>, which uses
    /// <c>{AppData}/locks</c> from <see cref="Devices.IAppStorage"/>. The directory must be private to the user and on a local
    /// file system that supports advisory locks.
    /// </summary>
    public string? SingleInstanceLockDirectory { get; set; }

    /// <summary>
    /// When the primary instance acquires the lease, publish its own launch arguments as a cold-start activation
    /// (<see cref="Lifecycle.AppActivation.IsColdStart"/> = <see langword="true"/>). Disable when the host publishes the
    /// launch activation itself. Default: <see langword="true"/>.
    /// </summary>
    public bool PublishLaunchActivation { get; set; } = true;

    /// <summary>
    /// Security settings for the single-instance channel. <see cref="IpcSecurityOptions.CurrentUserOnly"/> is always
    /// enforced. Default: a 256 KB message limit.
    /// </summary>
    public IpcSecurityOptions SingleInstanceSecurity { get; set; } = new() { MaxMessageBytes = 256 * 1024 };

    /// <summary>The maximum number of arguments a secondary instance may forward. Default: 512.</summary>
    public int MaxForwardedArguments { get; set; } = 512;

    /// <summary>The default timeout of <see cref="IIpcService.ConnectAsync"/>. Default: 2 seconds.</summary>
    public TimeSpan IpcConnectTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The default timeout of <see cref="IIpcConnection.RequestAsync"/>. Default: 30 seconds.</summary>
    public TimeSpan IpcRequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a single frame write may take before the peer is considered stuck and disconnected (a peer that stops
    /// reading would otherwise block the writer forever). Default: 10 seconds.
    /// </summary>
    public TimeSpan IpcWriteTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The maximum number of simultaneous clients per IPC server; further connections are refused. Default: 64.</summary>
    public int IpcMaxConnections { get; set; } = 64;

    /// <summary>The maximum number of requests a single client may have in flight on a server. Default: 16.</summary>
    public int IpcMaxConcurrentRequestsPerConnection { get; set; } = 16;

    /// <summary>
    /// Allow <see cref="IProtocolRegistrationService.RegisterAsync"/> to replace a scheme registration that belongs to
    /// another application. Default: <see langword="false"/> (the call fails with <see cref="NativeErrorCodes.Conflict"/>).
    /// </summary>
    public bool OverwriteForeignProtocolRegistrations { get; set; }
}
