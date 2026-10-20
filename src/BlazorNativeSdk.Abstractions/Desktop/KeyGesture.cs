using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace BlazorNativeSdk.Desktop;

/// <summary>Keyboard modifiers.</summary>
[Flags]
public enum KeyModifiers
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Control.</summary>
    Control = 1,

    /// <summary>Alt (Option on macOS).</summary>
    Alt = 2,

    /// <summary>Shift.</summary>
    Shift = 4,

    /// <summary>The Windows key, Command on macOS, Super on Linux.</summary>
    Meta = 8,

    /// <summary>Command on macOS and Control elsewhere. Resolved by adapters with <see cref="KeyGesture.Resolve"/>.</summary>
    CommandOrControl = 16,
}

/// <summary>Keys that can be used in shortcuts.</summary>
public enum NativeKey
{
    /// <summary>No key.</summary>
    None = 0,
#pragma warning disable CS1591 // Key names are self-describing.
    A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, F13, F14, F15, F16, F17, F18, F19, F20, F21, F22, F23, F24,
    Space, Enter, Escape, Tab, Backspace, Delete, Insert, Home, End, PageUp, PageDown,
    Left, Up, Right, Down,
    Plus, Minus, Comma, Period, Slash, Backslash, Semicolon, Quote, Backquote, BracketLeft, BracketRight, Equal,
    NumPad0, NumPad1, NumPad2, NumPad3, NumPad4, NumPad5, NumPad6, NumPad7, NumPad8, NumPad9,
    PrintScreen, Pause, MediaPlayPause, MediaNextTrack, MediaPreviousTrack, MediaStop, VolumeUp, VolumeDown, VolumeMute,
#pragma warning restore CS1591
}

/// <summary>
/// A keyboard shortcut such as <c>CommandOrControl+Shift+K</c>. The text format follows Electron accelerators
/// and accepts the aliases <c>Ctrl</c>, <c>Control</c>, <c>Alt</c>, <c>Option</c>, <c>Shift</c>, <c>Meta</c>, <c>Super</c>,
/// <c>Win</c>, <c>Cmd</c>, <c>Command</c>, <c>CmdOrCtrl</c> and <c>CommandOrControl</c>.
/// </summary>
/// <param name="Modifiers">The modifiers.</param>
/// <param name="Key">The key.</param>
public readonly record struct KeyGesture(KeyModifiers Modifiers, NativeKey Key)
{
    /// <summary>Parses a gesture.</summary>
    /// <exception cref="FormatException">The text is not a valid gesture.</exception>
    public static KeyGesture Parse(string text) =>
        TryParse(text, out var gesture) ? gesture : throw new FormatException($"'{text}' is not a valid key gesture.");

    /// <summary>Tries to parse a gesture.</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out KeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = KeyModifiers.None;
        NativeKey? key = null;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);

        // Support "Ctrl++" (plus key): a trailing "++" produces two empty segments, the last of which is the Plus key.
        // A single trailing "+" ("Ctrl+") is incomplete and rejected.
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0)
            {
                if (i == parts.Length - 1 && key is null && (i == 0 || parts[i - 1].Length == 0))
                {
                    key = NativeKey.Plus;
                    continue;
                }

                if (i == parts.Length - 2 && parts[^1].Length == 0)
                {
                    continue;
                }

                return false;
            }

            var modifier = ParseModifier(part);
            if (modifier != KeyModifiers.None)
            {
                modifiers |= modifier;
                continue;
            }

            if (key is not null || !TryParseKey(part, out var parsed))
            {
                return false;
            }

            key = parsed;
        }

        if (key is null)
        {
            return false;
        }

        gesture = new KeyGesture(modifiers, key.Value);
        return true;
    }

    /// <summary>Resolves <see cref="KeyModifiers.CommandOrControl"/> for the given platform.</summary>
    public KeyGesture Resolve(NativePlatform platform)
    {
        if ((Modifiers & KeyModifiers.CommandOrControl) == 0)
        {
            return this;
        }

        var resolved = Modifiers & ~KeyModifiers.CommandOrControl;
        resolved |= platform is NativePlatform.MacOS or NativePlatform.MacCatalyst or NativePlatform.iOS ? KeyModifiers.Meta : KeyModifiers.Control;
        return this with { Modifiers = resolved };
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var builder = new StringBuilder();
        var modifiers = Modifiers;
        Append(KeyModifiers.CommandOrControl, "CommandOrControl");
        Append(KeyModifiers.Control, "Ctrl");
        Append(KeyModifiers.Alt, "Alt");
        Append(KeyModifiers.Shift, "Shift");
        Append(KeyModifiers.Meta, "Meta");
        builder.Append(KeyToString(Key));
        return builder.ToString();

        void Append(KeyModifiers flag, string name)
        {
            if ((modifiers & flag) != 0)
            {
                builder.Append(name).Append('+');
            }
        }
    }

    private static KeyModifiers ParseModifier(string part) => part.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" => KeyModifiers.Control,
        "ALT" or "OPTION" => KeyModifiers.Alt,
        "SHIFT" => KeyModifiers.Shift,
        "META" or "SUPER" or "WIN" or "WINDOWS" or "CMD" or "COMMAND" => KeyModifiers.Meta,
        "CMDORCTRL" or "COMMANDORCONTROL" or "MOD" => KeyModifiers.CommandOrControl,
        _ => KeyModifiers.None,
    };

    private static bool TryParseKey(string part, out NativeKey key)
    {
        key = NativeKey.None;
        if (part.Length == 1)
        {
            var c = char.ToUpperInvariant(part[0]);
            if (c is >= 'A' and <= 'Z')
            {
                key = NativeKey.A + (c - 'A');
                return true;
            }

            if (c is >= '0' and <= '9')
            {
                key = NativeKey.D0 + (c - '0');
                return true;
            }

            key = c switch
            {
                '-' => NativeKey.Minus,
                ',' => NativeKey.Comma,
                '.' => NativeKey.Period,
                '/' => NativeKey.Slash,
                '\\' => NativeKey.Backslash,
                ';' => NativeKey.Semicolon,
                '\'' => NativeKey.Quote,
                '`' => NativeKey.Backquote,
                '[' => NativeKey.BracketLeft,
                ']' => NativeKey.BracketRight,
                '=' => NativeKey.Equal,
                _ => NativeKey.None,
            };
            return key != NativeKey.None;
        }

        key = part.ToUpperInvariant() switch
        {
            "ESC" => NativeKey.Escape,
            "RETURN" => NativeKey.Enter,
            "DEL" => NativeKey.Delete,
            "INS" => NativeKey.Insert,
            "PGUP" => NativeKey.PageUp,
            "PGDN" or "PAGEDN" => NativeKey.PageDown,
            "ARROWLEFT" => NativeKey.Left,
            "ARROWRIGHT" => NativeKey.Right,
            "ARROWUP" => NativeKey.Up,
            "ARROWDOWN" => NativeKey.Down,
            "PRINTSCREEN" or "PRTSC" => NativeKey.PrintScreen,
            _ => NativeKey.None,
        };

        if (key != NativeKey.None)
        {
            return true;
        }

        if (part.StartsWith("num", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(part.AsSpan(part.StartsWith("numpad", StringComparison.OrdinalIgnoreCase) ? 6 : 3), out var digit) &&
            digit is >= 0 and <= 9)
        {
            key = NativeKey.NumPad0 + digit;
            return true;
        }

        return Enum.TryParse(part, ignoreCase: true, out key) && key != NativeKey.None && Enum.IsDefined(key);
    }

    private static string KeyToString(NativeKey key) => key switch
    {
        >= NativeKey.D0 and <= NativeKey.D9 => ((char)('0' + (key - NativeKey.D0))).ToString(),
        _ => key.ToString(),
    };
}
