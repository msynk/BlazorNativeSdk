using System.Runtime.InteropServices;

namespace BlazorNativeSdk.WindowsPlatform.Interop;

// Layouts follow the Windows SDK headers. Sequential layout with natural packing produces the correct offsets on x86, x64
// and arm64 (pointer-sized fields are 8-byte aligned on 64-bit). StructLayoutTests verifies the 64-bit sizes.

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public RECT(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public readonly int Width => Right - Left;

    public readonly int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public nint hwnd;
    public uint message;
    public nint wParam;
    public nint lParam;
    public uint time;
    public POINT pt;
    public uint lPrivate;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WNDCLASSEXW
{
    public uint cbSize;
    public uint style;
    public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public nint hInstance;
    public nint hIcon;
    public nint hCursor;
    public nint hbrBackground;
    public char* lpszMenuName;
    public char* lpszClassName;
    public nint hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CREATESTRUCTW
{
    public nint lpCreateParams;
    public nint hInstance;
    public nint hMenu;
    public nint hwndParent;
    public int cy;
    public int cx;
    public int y;
    public int x;
    public int style;
    public nint lpszName;
    public nint lpszClass;
    public uint dwExStyle;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WINDOWPLACEMENT
{
    public uint length;
    public uint flags;
    public uint showCmd;
    public POINT ptMinPosition;
    public POINT ptMaxPosition;
    public RECT rcNormalPosition;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MONITORINFOEXW
{
    public uint cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
    public fixed char szDevice[32];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DISPLAY_DEVICEW
{
    public uint cb;
    public fixed char DeviceName[32];
    public fixed char DeviceString[128];
    public uint StateFlags;
    public fixed char DeviceID[128];
    public fixed char DeviceKey[128];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NOTIFYICONDATAW
{
    public uint cbSize;
    public nint hWnd;
    public uint uID;
    public uint uFlags;
    public uint uCallbackMessage;
    public nint hIcon;
    public fixed char szTip[128];
    public uint dwState;
    public uint dwStateMask;
    public fixed char szInfo[256];
    public uint uVersion; // union with uTimeout
    public fixed char szInfoTitle[64];
    public uint dwInfoFlags;
    public Guid guidItem;
    public nint hBalloonIcon;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MENUITEMINFOW
{
    public uint cbSize;
    public uint fMask;
    public uint fType;
    public uint fState;
    public uint wID;
    public nint hSubMenu;
    public nint hbmpChecked;
    public nint hbmpUnchecked;
    public nuint dwItemData;
    public char* dwTypeData;
    public uint cch;
    public nint hbmpItem;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GUITHREADINFO
{
    public uint cbSize;
    public uint flags;
    public nint hwndActive;
    public nint hwndFocus;
    public nint hwndCapture;
    public nint hwndMenuOwner;
    public nint hwndMoveSize;
    public nint hwndCaret;
    public RECT rcCaret;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FLASHWINFO
{
    public uint cbSize;
    public nint hwnd;
    public uint dwFlags;
    public uint uCount;
    public uint dwTimeout;
}
