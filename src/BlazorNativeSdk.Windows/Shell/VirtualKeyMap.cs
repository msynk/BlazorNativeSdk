using System.Text;
using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.WindowsPlatform.Interop;

namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>Maps <see cref="NativeKey"/> and <see cref="KeyModifiers"/> to Win32 virtual-key codes and hot-key modifiers.</summary>
internal static class VirtualKeyMap
{
    /// <summary>Gets the virtual-key code for a key. OEM keys use their US-layout positions (as Electron does).</summary>
    public static bool TryGetVirtualKey(NativeKey key, out uint virtualKey)
    {
        virtualKey = key switch
        {
            >= NativeKey.A and <= NativeKey.Z => 0x41u + (uint)(key - NativeKey.A),
            >= NativeKey.D0 and <= NativeKey.D9 => 0x30u + (uint)(key - NativeKey.D0),
            >= NativeKey.F1 and <= NativeKey.F24 => 0x70u + (uint)(key - NativeKey.F1),
            >= NativeKey.NumPad0 and <= NativeKey.NumPad9 => 0x60u + (uint)(key - NativeKey.NumPad0),
            NativeKey.Space => 0x20,
            NativeKey.Enter => 0x0D,
            NativeKey.Escape => 0x1B,
            NativeKey.Tab => 0x09,
            NativeKey.Backspace => 0x08,
            NativeKey.Delete => 0x2E,
            NativeKey.Insert => 0x2D,
            NativeKey.Home => 0x24,
            NativeKey.End => 0x23,
            NativeKey.PageUp => 0x21,
            NativeKey.PageDown => 0x22,
            NativeKey.Left => 0x25,
            NativeKey.Up => 0x26,
            NativeKey.Right => 0x27,
            NativeKey.Down => 0x28,
            NativeKey.Plus => 0xBB,          // VK_OEM_PLUS ('=' / '+')
            NativeKey.Equal => 0xBB,         // VK_OEM_PLUS
            NativeKey.Minus => 0xBD,         // VK_OEM_MINUS
            NativeKey.Comma => 0xBC,         // VK_OEM_COMMA
            NativeKey.Period => 0xBE,        // VK_OEM_PERIOD
            NativeKey.Slash => 0xBF,         // VK_OEM_2
            NativeKey.Backslash => 0xDC,     // VK_OEM_5
            NativeKey.Semicolon => 0xBA,     // VK_OEM_1
            NativeKey.Quote => 0xDE,         // VK_OEM_7
            NativeKey.Backquote => 0xC0,     // VK_OEM_3
            NativeKey.BracketLeft => 0xDB,   // VK_OEM_4
            NativeKey.BracketRight => 0xDD,  // VK_OEM_6
            NativeKey.PrintScreen => 0x2C,   // VK_SNAPSHOT
            NativeKey.Pause => 0x13,
            NativeKey.MediaPlayPause => 0xB3,
            NativeKey.MediaNextTrack => 0xB0,
            NativeKey.MediaPreviousTrack => 0xB1,
            NativeKey.MediaStop => 0xB2,
            NativeKey.VolumeUp => 0xAF,
            NativeKey.VolumeDown => 0xAE,
            NativeKey.VolumeMute => 0xAD,
            _ => 0,
        };

        return virtualKey != 0;
    }

    /// <summary>Converts modifiers to <c>RegisterHotKey</c> flags. <see cref="KeyModifiers.CommandOrControl"/> maps to Control.</summary>
    public static uint ToHotKeyModifiers(KeyModifiers modifiers)
    {
        uint result = 0;
        if ((modifiers & (KeyModifiers.Control | KeyModifiers.CommandOrControl)) != 0)
        {
            result |= Win32.MOD_CONTROL;
        }

        if ((modifiers & KeyModifiers.Alt) != 0)
        {
            result |= Win32.MOD_ALT;
        }

        if ((modifiers & KeyModifiers.Shift) != 0)
        {
            result |= Win32.MOD_SHIFT;
        }

        if ((modifiers & KeyModifiers.Meta) != 0)
        {
            result |= Win32.MOD_WIN;
        }

        return result;
    }

    /// <summary>Formats a gesture the way Windows menus display accelerators, for example <c>Ctrl+Shift+K</c>.</summary>
    public static string ToDisplayString(KeyGesture gesture)
    {
        var resolved = gesture.Resolve(NativePlatform.Windows);
        var builder = new StringBuilder();
        if ((resolved.Modifiers & KeyModifiers.Control) != 0)
        {
            builder.Append("Ctrl+");
        }

        if ((resolved.Modifiers & KeyModifiers.Alt) != 0)
        {
            builder.Append("Alt+");
        }

        if ((resolved.Modifiers & KeyModifiers.Shift) != 0)
        {
            builder.Append("Shift+");
        }

        if ((resolved.Modifiers & KeyModifiers.Meta) != 0)
        {
            builder.Append("Win+");
        }

        builder.Append(KeyName(resolved.Key));
        return builder.ToString();
    }

    private static string KeyName(NativeKey key) => key switch
    {
        >= NativeKey.D0 and <= NativeKey.D9 => ((char)('0' + (key - NativeKey.D0))).ToString(),
        >= NativeKey.NumPad0 and <= NativeKey.NumPad9 => "Num " + (char)('0' + (key - NativeKey.NumPad0)),
        NativeKey.Plus => "+",
        NativeKey.Minus => "-",
        NativeKey.Equal => "=",
        NativeKey.Comma => ",",
        NativeKey.Period => ".",
        NativeKey.Slash => "/",
        NativeKey.Backslash => "\\",
        NativeKey.Semicolon => ";",
        NativeKey.Quote => "'",
        NativeKey.Backquote => "`",
        NativeKey.BracketLeft => "[",
        NativeKey.BracketRight => "]",
        NativeKey.Escape => "Esc",
        NativeKey.Delete => "Del",
        NativeKey.Insert => "Ins",
        NativeKey.PageUp => "PgUp",
        NativeKey.PageDown => "PgDn",
        NativeKey.Backspace => "Backspace",
        NativeKey.PrintScreen => "PrtSc",
        _ => key.ToString(),
    };
}
