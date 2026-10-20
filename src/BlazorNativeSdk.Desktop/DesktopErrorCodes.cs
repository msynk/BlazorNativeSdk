namespace BlazorNativeSdk.Desktop;

/// <summary>Error codes reported by the desktop adapter in addition to <see cref="NativeErrorCodes"/>.</summary>
public static class DesktopErrorCodes
{
    /// <summary>The IPC peer failed the shared-secret handshake, or required a secret that was not configured.</summary>
    public const string IpcAuthenticationFailed = "ipc.authentication-failed";

    /// <summary>The IPC server refused the connection (client executable not allowed, connection limit reached).</summary>
    public const string IpcRejected = "ipc.rejected";

    /// <summary>The peer violated the wire protocol (bad frame, bad MAC, replayed or reordered message).</summary>
    public const string IpcProtocolError = "ipc.protocol-error";

    /// <summary>The connection closed before the operation completed.</summary>
    public const string IpcDisconnected = "ipc.disconnected";

    /// <summary>The server's message handler failed while processing the request.</summary>
    public const string IpcHandlerFailed = "ipc.handler-failed";

    /// <summary>No application is associated with the file type or URI scheme.</summary>
    public const string NoAssociation = "desktop.no-association";

    /// <summary>A required desktop tool (for example <c>xdg-open</c> or <c>xdg-mime</c>) is not installed.</summary>
    public const string ToolMissing = "desktop.tool-missing";

    /// <summary>The item cannot be moved to the trash (for example a file system without trash support).</summary>
    public const string TrashUnavailable = "desktop.trash-unavailable";
}
