using Avalonia.Input;
using Devolutions.Terminal.Settings;

namespace Devolutions.Terminal.App.Actions;

public static class AvaloniaKeyChord
{
    public static bool TryCreate(KeyEventArgs args, out KeyChord chord)
    {
        var key = GetKeyName(args.Key);
        if (key is null)
        {
            chord = default;
            return false;
        }

        var parts = new List<string>(5);
        if (args.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            parts.Add("win");
        }

        if (args.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add("ctrl");
        }

        if (args.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("alt");
        }

        if (args.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("shift");
        }

        parts.Add(key);
        return KeyChord.TryParse(string.Join('+', parts), out chord);
    }

    public static string? GetKeyName(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            return key.ToString().ToLowerInvariant();
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((int)key - (int)Key.D0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            return $"numpad{(int)key - (int)Key.NumPad0}";
        }

        if (key is >= Key.F1 and <= Key.F24)
        {
            return key.ToString().ToLowerInvariant();
        }

        return key switch
        {
            Key.Return or Key.LineFeed => "enter",
            Key.Tab => "tab",
            Key.Escape => "esc",
            Key.Space => "space",
            Key.Back => "backspace",
            Key.Delete => "delete",
            Key.Insert => "insert",
            Key.Home => "home",
            Key.End => "end",
            Key.PageUp => "pageup",
            Key.PageDown => "pagedown",
            Key.Apps => "menu",
            Key.Left => "left",
            Key.Right => "right",
            Key.Up => "up",
            Key.Down => "down",
            Key.OemPlus => "plus",
            Key.Add => "numpad_plus",
            Key.OemMinus => "minus",
            Key.Subtract => "numpad_minus",
            Key.OemComma => "comma",
            Key.OemPeriod => "period",
            Key.Decimal => "numpad_period",
            Key.OemQuestion => "slash",
            Key.Divide => "numpad_divide",
            // The US "\|" key is OemPipe (VK_OEM_5, as the global hotkey backend
            // maps "backslash"); OemBackslash (VK_OEM_102) is the ISO 102nd key.
            Key.OemPipe or Key.OemBackslash => "backslash",
            Key.OemSemicolon => "semicolon",
            Key.OemQuotes => "quote",
            Key.OemOpenBrackets => "open_bracket",
            Key.OemCloseBrackets => "close_bracket",
            Key.OemTilde => "backtick",
            Key.Multiply => "numpad_multiply",
            Key.BrowserBack => "browser_back",
            Key.BrowserForward => "browser_forward",
            Key.BrowserRefresh => "browser_refresh",
            Key.BrowserStop => "browser_stop",
            Key.BrowserSearch => "browser_search",
            Key.BrowserFavorites => "browser_favorites",
            Key.BrowserHome => "browser_home",
            _ => null,
        };
    }

    public static KeyGesture? GetActionGesture(ActionMap actionMap, string actionId)
    {
        ArgumentNullException.ThrowIfNull(actionMap);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        return actionMap.GetKeyBindingForAction(actionId) is { } chord &&
               TryToKeyGesture(chord, out var gesture)
            ? gesture
            : null;
    }

    public static bool TryToKeyGesture(KeyChord chord, out KeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(chord.Value))
        {
            return false;
        }

        var modifiers = KeyModifiers.None;
        Key? parsedKey = null;
        foreach (var part in chord.Value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part)
            {
                case "ctrl":
                    modifiers |= KeyModifiers.Control;
                    break;
                case "alt":
                    modifiers |= KeyModifiers.Alt;
                    break;
                case "shift":
                    modifiers |= KeyModifiers.Shift;
                    break;
                case "win":
                case "cmd":
                    modifiers |= KeyModifiers.Meta;
                    break;
                default:
                    parsedKey = ParseKeyName(part);
                    if (parsedKey is null)
                    {
                        return false;
                    }
                    break;
            }
        }

        if (parsedKey is not { } key || key == Key.None)
        {
            return false;
        }

        gesture = new KeyGesture(key, modifiers);
        return true;
    }

    private static Key? ParseKeyName(string name)
    {
        if (name.Length == 1)
        {
            var ch = char.ToLowerInvariant(name[0]);
            if (ch is >= 'a' and <= 'z')
            {
                return (Key)((int)Key.A + (ch - 'a'));
            }

            if (ch is >= '0' and <= '9')
            {
                return (Key)((int)Key.D0 + (ch - '0'));
            }
        }

        if (name.StartsWith("numpad", StringComparison.Ordinal) &&
            name.Length == 7 &&
            name[6] is >= '0' and <= '9')
        {
            return (Key)((int)Key.NumPad0 + (name[6] - '0'));
        }

        if (name.Length is 2 or 3 &&
            name[0] == 'f' &&
            int.TryParse(name.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture, out var fn) &&
            fn is >= 1 and <= 24)
        {
            return (Key)((int)Key.F1 + (fn - 1));
        }

        return name switch
        {
            "enter" => Key.Return,
            "tab" => Key.Tab,
            "esc" => Key.Escape,
            "space" => Key.Space,
            "backspace" => Key.Back,
            "delete" => Key.Delete,
            "insert" => Key.Insert,
            "home" => Key.Home,
            "end" => Key.End,
            "pgup" or "pageup" => Key.PageUp,
            "pgdn" or "pagedown" => Key.PageDown,
            "menu" => Key.Apps,
            "left" => Key.Left,
            "right" => Key.Right,
            "up" => Key.Up,
            "down" => Key.Down,
            "plus" => Key.OemPlus,
            "numpad_plus" => Key.Add,
            "minus" => Key.OemMinus,
            "numpad_minus" => Key.Subtract,
            "comma" => Key.OemComma,
            "period" => Key.OemPeriod,
            "numpad_period" => Key.Decimal,
            "slash" => Key.OemQuestion,
            "numpad_divide" => Key.Divide,
            "backslash" => Key.OemPipe,
            "semicolon" => Key.OemSemicolon,
            "quote" => Key.OemQuotes,
            "open_bracket" => Key.OemOpenBrackets,
            "close_bracket" => Key.OemCloseBrackets,
            "backtick" => Key.OemTilde,
            "numpad_multiply" => Key.Multiply,
            _ => null,
        };
    }
}
