using Microsoft.Win32.SafeHandles;

namespace BlazorNativeSdk.WindowsPlatform.Interop;

/// <summary>An HICON destroyed with <c>DestroyIcon</c>. Shared icons (<c>LoadIcon</c>) are wrapped with <c>ownsHandle: false</c>.</summary>
internal sealed class SafeIconHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeIconHandle()
        : base(ownsHandle: true)
    {
    }

    public SafeIconHandle(nint handle, bool ownsHandle)
        : base(ownsHandle)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle() => NativeMethods.DestroyIcon(handle);
}

/// <summary>An HMENU destroyed with <c>DestroyMenu</c> (which also destroys its submenus).</summary>
internal sealed class SafeMenuHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeMenuHandle()
        : base(ownsHandle: true)
    {
    }

    public SafeMenuHandle(nint handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    /// <summary>Transfers ownership to the caller (for example to a window via <c>SetMenu</c>) and returns the raw handle.</summary>
    public nint Detach()
    {
        var value = handle;
        SetHandleAsInvalid();
        return value;
    }

    protected override bool ReleaseHandle() => !NativeMethods.IsMenu(handle) || NativeMethods.DestroyMenu(handle);
}

/// <summary>Global memory released with <c>GlobalFree</c> unless ownership was transferred (for example to the clipboard).</summary>
internal sealed class SafeGlobalMemoryHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeGlobalMemoryHandle()
        : base(ownsHandle: true)
    {
    }

    public SafeGlobalMemoryHandle(nint handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle() => NativeMethods.GlobalFree(handle) == 0;
}
