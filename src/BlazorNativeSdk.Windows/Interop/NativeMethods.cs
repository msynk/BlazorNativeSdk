using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace BlazorNativeSdk.WindowsPlatform.Interop;

/// <summary>Hand-written P/Invoke declarations (source-generated marshalling, no CsWin32).</summary>
internal static unsafe partial class NativeMethods
{
    private const string User32 = "user32.dll";
    private const string Kernel32 = "kernel32.dll";
    private const string Shell32 = "shell32.dll";
    private const string Shcore = "shcore.dll";
    private const string Comctl32 = "comctl32.dll";
    private const string Combase = "combase.dll";

    // ---- Window classes, windows and messages -------------------------------------------------------------------------

    [LibraryImport(User32, SetLastError = true)]
    public static partial ushort RegisterClassExW(WNDCLASSEXW* lpwcx);

    [LibraryImport(User32, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowExW(
        uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint hWnd);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hWnd);

    [LibraryImport(User32)]
    public static partial nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport(User32, SetLastError = true)]
    public static partial int GetMessageW(MSG* lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(MSG* lpMsg);

    [LibraryImport(User32)]
    public static partial nint DispatchMessageW(MSG* lpMsg);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport(User32, SetLastError = true)]
    public static partial nint SendMessageTimeoutW(nint hWnd, uint msg, nint wParam, nint lParam, uint fuFlags, uint uTimeout, nint* lpdwResult);

    [LibraryImport(User32)]
    public static partial void PostQuitMessage(int nExitCode);

    [LibraryImport(User32, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessageW(string lpString);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ChangeWindowMessageFilterEx(nint hwnd, uint message, uint action, nint pChangeFilterStruct);

    [LibraryImport(User32)]
    public static partial uint GetDoubleClickTime();

    [LibraryImport(User32, SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr64(nint hWnd, int nIndex);

    [LibraryImport(User32, SetLastError = true, EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong32(nint hWnd, int nIndex);

    [LibraryImport(User32, SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport(User32, SetLastError = true, EntryPoint = "SetWindowLongW")]
    private static partial int SetWindowLong32(nint hWnd, int nIndex, int dwNewLong);

    public static nint GetWindowLongPtr(nint hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);

    public static nint SetWindowLongPtr(nint hWnd, int nIndex, nint value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, value) : SetWindowLong32(hWnd, nIndex, (int)value);

    [LibraryImport(User32)]
    public static partial uint GetWindowThreadProcessId(nint hWnd, uint* lpdwProcessId);

    public static uint GetWindowThreadId(nint hWnd) => GetWindowThreadProcessId(hWnd, null);

    [LibraryImport(User32)]
    public static partial nint GetAncestor(nint hwnd, uint gaFlags);

    // ---- Window state -------------------------------------------------------------------------------------------------

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindowAsync(nint hWnd, int nCmdShow);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hWnd);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsZoomed(nint hWnd);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hWnd, RECT* lpRect);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowPlacement(nint hWnd, WINDOWPLACEMENT* lpwndpl);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPlacement(nint hWnd, WINDOWPLACEMENT* lpwndpl);

    [LibraryImport(User32, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowTextW(nint hWnd, string lpString);

    [LibraryImport(User32)]
    public static partial int InternalGetWindowText(nint hWnd, char* pString, int cchMaxCount);

    [LibraryImport(User32)]
    public static partial nint GetForegroundWindow();

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BringWindowToTop(nint hWnd);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FlashWindowEx(FLASHWINFO* pfwi);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetGUIThreadInfo(uint idThread, GUITHREADINFO* pgui);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(POINT* lpPoint);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PhysicalToLogicalPointForPerMonitorDPI(nint hWnd, POINT* lpPoint);

    // ---- DPI and monitors ---------------------------------------------------------------------------------------------

    [LibraryImport(User32)]
    public static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport(User32)]
    public static partial uint GetDpiForSystem();

    [LibraryImport(User32)]
    public static partial int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [LibraryImport(User32)]
    public static partial nint SetThreadDpiAwarenessContext(nint dpiContext);

    [LibraryImport(User32)]
    public static partial nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [LibraryImport(User32)]
    public static partial nint MonitorFromRect(RECT* lprc, uint dwFlags);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfoW(nint hMonitor, MONITORINFOEXW* lpmi);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayMonitors(nint hdc, RECT* lprcClip, delegate* unmanaged<nint, nint, RECT*, nint, int> lpfnEnum, nint dwData);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayDevicesW(char* lpDevice, uint iDevNum, DISPLAY_DEVICEW* lpDisplayDevice, uint dwFlags);

    [LibraryImport(Shcore)]
    public static partial int GetDpiForMonitor(nint hmonitor, int dpiType, uint* dpiX, uint* dpiY);

    // ---- Hot keys ------------------------------------------------------------------------------------------------------

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hWnd, int id);

    // ---- Menus ---------------------------------------------------------------------------------------------------------

    [LibraryImport(User32, SetLastError = true)]
    public static partial nint CreateMenu();

    [LibraryImport(User32, SetLastError = true)]
    public static partial nint CreatePopupMenu();

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyMenu(nint hMenu);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsMenu(nint hMenu);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InsertMenuItemW(nint hmenu, uint item, [MarshalAs(UnmanagedType.Bool)] bool fByPosition, MENUITEMINFOW* lpmi);

    [LibraryImport(User32, SetLastError = true)]
    public static partial int GetMenuItemCount(nint hMenu);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMenuItemInfoW(nint hmenu, uint item, [MarshalAs(UnmanagedType.Bool)] bool fByPosition, MENUITEMINFOW* lpmii);

    [LibraryImport(User32, SetLastError = true)]
    public static partial int TrackPopupMenuEx(nint hMenu, uint uFlags, int x, int y, nint hwnd, nint lptpm);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetMenu(nint hWnd, nint hMenu);

    [LibraryImport(User32)]
    public static partial nint GetMenu(nint hWnd);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DrawMenuBar(nint hWnd);

    [LibraryImport(Comctl32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowSubclass(nint hWnd, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [LibraryImport(Comctl32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveWindowSubclass(nint hWnd, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> pfnSubclass, nuint uIdSubclass);

    [LibraryImport(Comctl32)]
    public static partial nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    // ---- Icons ---------------------------------------------------------------------------------------------------------

    [LibraryImport(User32, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint LoadImageW(nint hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [LibraryImport(User32, SetLastError = true)]
    public static partial nint CreateIconFromResourceEx(byte* presbits, uint dwResSize, [MarshalAs(UnmanagedType.Bool)] bool fIcon, uint dwVer, int cxDesired, int cyDesired, uint flags);

    [LibraryImport(User32, SetLastError = true)]
    public static partial nint LoadIconW(nint hInstance, nint lpIconName);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint hIcon);

    [LibraryImport(Shell32, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHDefExtractIconW(string pszIconFile, int iIndex, uint uFlags, nint* phiconLarge, nint* phiconSmall, uint nIconSize);

    [LibraryImport(Shell32)]
    public static partial nint ExtractAssociatedIconW(nint hInst, char* pszIconPath, ushort* piIcon);

    // ---- Notification area ---------------------------------------------------------------------------------------------

    [LibraryImport(Shell32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Shell_NotifyIconW(uint dwMessage, NOTIFYICONDATAW* lpData);

    // ---- Clipboard -----------------------------------------------------------------------------------------------------

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(nint hWndNewOwner);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();

    [LibraryImport(User32, SetLastError = true)]
    public static partial nint GetClipboardData(uint uFormat);

    [LibraryImport(User32, SetLastError = true)]
    public static partial nint SetClipboardData(uint uFormat, nint hMem);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AddClipboardFormatListener(nint hwnd);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveClipboardFormatListener(nint hwnd);

    // ---- Kernel --------------------------------------------------------------------------------------------------------

    [LibraryImport(Kernel32)]
    public static partial uint GetCurrentThreadId();

    [LibraryImport(Kernel32, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandleW(string? lpModuleName);

    [LibraryImport(Kernel32, SetLastError = true)]
    public static partial nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport(Kernel32, SetLastError = true)]
    public static partial nint GlobalFree(nint hMem);

    [LibraryImport(Kernel32, SetLastError = true)]
    public static partial void* GlobalLock(nint hMem);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(nint hMem);

    [LibraryImport(Kernel32, SetLastError = true)]
    public static partial nuint GlobalSize(nint hMem);

    [LibraryImport(Kernel32)]
    public static partial int GetCurrentPackageFullName(uint* packageFullNameLength, char* packageFullName);

    // ---- Shell ---------------------------------------------------------------------------------------------------------

    [LibraryImport(Shell32, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    [LibraryImport(Shell32)]
    public static partial int SHGetKnownFolderPath(Guid* rfid, uint dwFlags, nint hToken, char** ppszPath);

    // ---- WinRT strings -------------------------------------------------------------------------------------------------

    [LibraryImport(Combase)]
    public static partial int WindowsCreateString(char* sourceString, uint length, nint* hstring);

    [LibraryImport(Combase)]
    public static partial int WindowsDeleteString(nint hstring);
}
