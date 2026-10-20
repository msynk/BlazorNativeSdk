namespace BlazorNativeSdk.Desktop.Ipc;

/// <summary>An internal IPC failure that maps to a <see cref="NativeError"/> at the public API boundary.</summary>
internal sealed class IpcException : Exception
{
    public IpcException(NativeResultStatus status, string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Status = status;
        Code = code;
    }

    public NativeResultStatus Status { get; }

    public string Code { get; }

    public static IpcException Protocol(string message, Exception? innerException = null) =>
        new(NativeResultStatus.Failed, DesktopErrorCodes.IpcProtocolError, message, innerException);

    public NativeError ToError() => new(Status, Code, Message)
    {
        Capability = NativeCapabilities.Ipc,
        Exception = InnerException,
    };
}
