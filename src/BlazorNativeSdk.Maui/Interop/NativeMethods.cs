#if WINDOWS
using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace BlazorNativeSdk.Maui.Interop;

/// <summary>Win32 declarations used by the MAUI integration on Windows (source-generated marshalling).</summary>
internal static unsafe partial class NativeMethods
{
    private const string User32 = "user32.dll";
    private const string Kernel32 = "kernel32.dll";
    private const string Shell32 = "shell32.dll";
    private const string Ole32 = "ole32.dll";

    /// <summary><c>GWLP_HWNDPARENT</c>: the owner of a top-level window.</summary>
    public const int GWLP_HWNDPARENT = -8;

    /// <summary><c>ASFW_ANY</c>: every process may set the foreground window.</summary>
    public const uint ASFW_ANY = unchecked((uint)-1);

    /// <summary><c>APPMODEL_ERROR_NO_PACKAGE</c>.</summary>
    public const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    /// <summary><c>CWMO_DEFAULT</c>.</summary>
    public const uint CWMO_DEFAULT = 0;

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hWnd);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hWnd);

    [LibraryImport(User32, EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static partial nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(uint dwProcessId);

    [LibraryImport(Kernel32)]
    public static partial nint GetCurrentProcess();

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(nint hProcess, uint uExitCode);

    [LibraryImport(Kernel32)]
    public static partial int GetCurrentPackageFullName(uint* packageFullNameLength, char* packageFullName);

    [LibraryImport(Kernel32)]
    public static partial nint LocalFree(nint hMem);

    [LibraryImport(Shell32, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [LibraryImport(Ole32)]
    public static partial int CoWaitForMultipleObjects(uint dwFlags, uint dwTimeout, uint cHandles, nint* pHandles, out uint lpdwindex);
}

/// <summary>The <c>WINDOWPOS</c> structure (<c>WM_WINDOWPOSCHANGED</c>).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WINDOWPOS
{
    public nint hwnd;
    public nint hwndInsertAfter;
    public int x;
    public int y;
    public int cx;
    public int cy;
    public uint flags;
}

/// <summary>Process-level helpers built on <see cref="NativeMethods"/>.</summary>
internal static unsafe class Win32Process
{
    private static readonly Lazy<bool> s_isPackaged = new(static () =>
    {
        uint length = 0;
        return NativeMethods.GetCurrentPackageFullName(&length, null) != NativeMethods.APPMODEL_ERROR_NO_PACKAGE;
    });

    /// <summary><see langword="true"/> when the process has package identity (MSIX or a sparse package).</summary>
    public static bool IsPackaged => s_isPackaged.Value;

    /// <summary>Ends the current process immediately with exit code 0, without running shutdown code.</summary>
    public static void TerminateCurrentProcess()
    {
        if (!NativeMethods.TerminateProcess(NativeMethods.GetCurrentProcess(), 0))
        {
            Environment.Exit(0);
        }
    }

    /// <summary>
    /// Splits a Windows command line into arguments with the same rules as the C runtime (<c>CommandLineToArgvW</c>).
    /// The string must not include the executable.
    /// </summary>
    public static IReadOnlyList<string> SplitArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return [];
        }

        // CommandLineToArgvW parses the first token with program-name rules; prefix a placeholder and skip it.
        var argv = NativeMethods.CommandLineToArgvW("_ " + arguments, out var count);
        if (argv == 0)
        {
            return [];
        }

        try
        {
            var result = new string[Math.Max(0, count - 1)];
            for (var i = 1; i < count; i++)
            {
                result[i - 1] = Marshal.PtrToStringUni(((nint*)argv)[i]) ?? string.Empty;
            }

            return result;
        }
        finally
        {
            NativeMethods.LocalFree(argv);
        }
    }
}
#endif
