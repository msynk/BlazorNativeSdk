namespace BlazorNativeSdk.WindowsPlatform.Interop;

/// <summary>Win32 constants used by the adapter (names match the Windows SDK headers).</summary>
internal static class Win32
{
    // Window messages.
    public const uint WM_NULL = 0x0000;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint WM_NCCREATE = 0x0081;
    public const uint WM_NCDESTROY = 0x0082;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_CUT = 0x0300;
    public const uint WM_COPY = 0x0301;
    public const uint WM_PASTE = 0x0302;
    public const uint WM_UNDO = 0x0304;
    public const uint WM_CLIPBOARDUPDATE = 0x031D;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_USER = 0x0400;
    public const uint WM_APP = 0x8000;
    public const uint EM_SETSEL = 0x00B1;
    public const uint EM_REDO = WM_USER + 84;

    // Private messages of the adapter's message windows.
    public const uint WM_BNS_INVOKE = WM_APP + 0x0B01;
    public const uint WM_BNS_TRAY = WM_APP + 0x0B02;

    // Window styles.
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_TOPMOST = 0x00000008;

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int GWLP_USERDATA = -21;

    public static readonly nint HWND_MESSAGE = -3;
    public static readonly nint HWND_TOP = 0;
    public static readonly nint HWND_TOPMOST = -1;
    public static readonly nint HWND_NOTOPMOST = -2;

    // ShowWindow commands.
    public const int SW_HIDE = 0;
    public const int SW_SHOWNORMAL = 1;
    public const int SW_SHOWMINIMIZED = 2;
    public const int SW_SHOWMAXIMIZED = 3;
    public const int SW_MAXIMIZE = 3;
    public const int SW_SHOW = 5;
    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;

    // SetWindowPos flags.
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOOWNERZORDER = 0x0200;

    // Monitors and DPI.
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    public const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    public const uint MONITORINFOF_PRIMARY = 0x00000001;
    public const int MDT_EFFECTIVE_DPI = 0;
    public static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
    public const int SM_CXSMICON = 49;
    public const int SM_CYSMICON = 50;
    public const uint USER_DEFAULT_SCREEN_DPI = 96;

    // Hot keys.
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;
    public const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    // Menus.
    public const uint MIIM_STATE = 0x00000001;
    public const uint MIIM_ID = 0x00000002;
    public const uint MIIM_SUBMENU = 0x00000004;
    public const uint MIIM_STRING = 0x00000040;
    public const uint MIIM_FTYPE = 0x00000100;
    public const uint MFT_STRING = 0x00000000;
    public const uint MFT_SEPARATOR = 0x00000800;
    public const uint MFT_RADIOCHECK = 0x00000200;
    public const uint MFS_GRAYED = 0x00000003;
    public const uint MFS_CHECKED = 0x00000008;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_BOTTOMALIGN = 0x0020;
    public const uint TPM_NONOTIFY = 0x0080;
    public const uint TPM_RETURNCMD = 0x0100;

    // Notification icons.
    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;
    public const uint NIM_SETVERSION = 0x00000004;
    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;
    public const uint NIF_SHOWTIP = 0x00000080;
    public const uint NOTIFYICON_VERSION_4 = 4;
    public const uint NIN_SELECT = WM_USER + 0;
    public const uint NIN_KEYSELECT = WM_USER + 1;

    // Icons.
    public const uint IMAGE_ICON = 1;
    public const uint LR_LOADFROMFILE = 0x00000010;
    public const uint LR_DEFAULTCOLOR = 0x00000000;
    public const int IDI_APPLICATION = 32512;

    // Clipboard and global memory.
    public const uint CF_UNICODETEXT = 13;
    public const uint GMEM_MOVEABLE = 0x0002;

    // Misc.
    public const uint GA_ROOT = 2;
    public const uint FLASHW_ALL = 0x00000003;
    public const uint FLASHW_TIMERNOFG = 0x0000000C;
    public const uint SMTO_ABORTIFHUNG = 0x0002;
    public const int APPMODEL_ERROR_NO_PACKAGE = 15700;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const uint KF_FLAG_CREATE = 0x00008000;

    public static ushort LoWord(nint value) => (ushort)((nuint)value & 0xFFFF);

    public static ushort HiWord(nint value) => (ushort)(((nuint)value >> 16) & 0xFFFF);

    public static int SignedLoWord(nint value) => (short)((nuint)value & 0xFFFF);

    public static int SignedHiWord(nint value) => (short)(((nuint)value >> 16) & 0xFFFF);
}
