using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace BlazorNativeSdk.Desktop.Interop;

/// <summary>Win32 entry points used by the desktop adapter.</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsInterop
{
    internal const int ErrorFileNotFound = 2;
    internal const int ErrorAccessDenied = 5;
    internal const int ErrorInsufficientBuffer = 122;
    internal const int ErrorPipeBusy = 231;
    internal const int ErrorNoAssociation = 1155;
    internal const int ErrorCancelled = 1223;
    internal const int AppModelErrorNoPackage = 15700;

    internal const uint PipeAccessDuplex = 0x00000003;
    internal const uint FileFlagFirstPipeInstance = 0x00080000;
    internal const uint FileFlagOverlapped = 0x40000000;
    internal const uint PipeTypeByte = 0x00000000;
    internal const uint PipeReadModeByte = 0x00000000;
    internal const uint PipeWait = 0x00000000;
    internal const uint PipeRejectRemoteClients = 0x00000008;
    internal const uint PipeUnlimitedInstances = 255;
    internal const uint SddlRevision1 = 1;
    internal const uint ProcessQueryLimitedInformation = 0x1000;

    internal const uint FoDelete = 0x0003;
    internal const ushort FofSilent = 0x0004;
    internal const ushort FofNoConfirmation = 0x0010;
    internal const ushort FofAllowUndo = 0x0040;
    internal const ushort FofNoErrorUi = 0x0400;
    internal const ushort FofWantNukeWarning = 0x4000;

    internal const int ShcneAssocChanged = 0x08000000;
    internal const uint ShcnfIdList = 0x0000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityAttributes
    {
        public int Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    /// <summary><c>SHFILEOPSTRUCTW</c> with natural (8-byte) alignment, as declared by shellapi.h for 64-bit Windows (x64 and arm64).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ShFileOpStruct64
    {
        public nint Hwnd;
        public uint Func;
        public nint From;
        public nint To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public nint NameMappings;
        public nint ProgressTitle;
    }

    /// <summary><c>SHFILEOPSTRUCTW</c> as declared by shellapi.h for 32-bit Windows (<c>pshpack1.h</c>).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct ShFileOpStruct32
    {
        public nint Hwnd;
        public uint Func;
        public nint From;
        public nint To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public nint NameMappings;
        public nint ProgressTitle;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateNamedPipe(
        string name,
        uint openMode,
        uint pipeMode,
        uint maxInstances,
        uint outBufferSize,
        uint inBufferSize,
        uint defaultTimeout,
        ref SecurityAttributes securityAttributes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessTimes(SafeProcessHandle process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* exeName, ref uint size);

    [LibraryImport("kernel32.dll")]
    internal static partial int GetCurrentPackageFullName(ref uint packageFullNameLength, nint packageFullName);

    [LibraryImport("kernel32.dll")]
    internal static partial nint LocalFree(nint memory);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out nint securityDescriptor, out uint size);

    [LibraryImport("shell32.dll", EntryPoint = "ILCreateFromPathW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint ILCreateFromPath(string path);

    [LibraryImport("shell32.dll")]
    internal static partial void ILFree(nint pidl);

    [LibraryImport("shell32.dll")]
    internal static partial int SHOpenFolderAndSelectItems(nint folder, uint count, nint items, uint flags);

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    internal static partial int SHFileOperation64(ref ShFileOpStruct64 operation);

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    internal static partial int SHFileOperation32(ref ShFileOpStruct32 operation);

    [LibraryImport("shell32.dll")]
    internal static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);

    /// <summary>Returns the full image path of a process, or <see langword="null"/> when it cannot be queried.</summary>
    /// <param name="processId">The process id, typically reported for the peer of a pipe connection.</param>
    /// <param name="connectedAtUtc">
    /// When the connection that reported <paramref name="processId"/> was observed. A process created after that moment cannot
    /// be the peer: the peer exited and Windows reused its id for another process. The path is then <see langword="null"/>,
    /// so checks such as <see cref="IpcSecurityOptions.AllowedClientExecutables"/> fail closed. The creation time is read
    /// from the same handle as the path, so the two cannot describe different processes.
    /// </param>
    internal static unsafe string? TryGetProcessImagePath(uint processId, DateTime? connectedAtUtc = null)
    {
        var raw = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, processId);
        if (raw == 0)
        {
            return null;
        }

        using var process = new SafeProcessHandle(raw, ownsHandle: true);
        if (connectedAtUtc is { } connected &&
            (!GetProcessTimes(process, out var creationTime, out _, out _, out _) || !StartedBy(creationTime, connected)))
        {
            return null;
        }

        const int Capacity = 32768;
        var buffer = new char[Capacity];
        var size = (uint)Capacity;
        fixed (char* p = buffer)
        {
            return QueryFullProcessImageName(process, 0, p, ref size) ? new string(buffer, 0, (int)size) : null;
        }
    }

    /// <summary>
    /// Returns whether a process created at <paramref name="creationFileTime"/> (a UTC <c>FILETIME</c>) existed when a
    /// connection was observed at <paramref name="connectedAtUtc"/>.
    /// </summary>
    internal static bool StartedBy(long creationFileTime, DateTime connectedAtUtc) =>
        creationFileTime > 0 && creationFileTime <= connectedAtUtc.ToUniversalTime().ToFileTimeUtc();

    /// <summary>Returns whether the current process runs with MSIX package identity.</summary>
    internal static bool IsPackagedProcess()
    {
        try
        {
            uint length = 0;
            var result = GetCurrentPackageFullName(ref length, 0);
            return result != AppModelErrorNoPackage;
        }
        catch (EntryPointNotFoundException)
        {
            // Windows 7 has no package identity.
            return false;
        }
    }
}
