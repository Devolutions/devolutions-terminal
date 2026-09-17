using System.Globalization;
using System.Text;
using Avalonia.Input;
using Devolutions.Terminal.Core;

namespace Devolutions.Terminal;

public enum TerminalKeyEventType : byte
{
    Press = 1,
    Repeat = 2,
    Release = 3,
}

public static class KeyMapper
{
    public static string? ToVt(
        Key key,
        KeyModifiers modifiers,
        PhysicalKey physicalKey,
        string? keySymbol,
        bool applicationCursorKeys) =>
        ToVt(
            key,
            modifiers,
            physicalKey,
            keySymbol,
            new TerminalInputMode(true, applicationCursorKeys, false, KittyKeyboardFlags.None, 0, false));

    public static string? ToVt(
        Key key,
        KeyModifiers modifiers,
        PhysicalKey physicalKey,
        string? keySymbol,
        TerminalInputMode mode,
        TerminalKeyEventType eventType = TerminalKeyEventType.Press,
        ushort repeatCount = 1)
    {
        if (physicalKey is PhysicalKey.Enter or PhysicalKey.NumPadEnter)
        {
            key = Key.Return;
        }
        else
        {
            key = physicalKey switch
            {
                PhysicalKey.ArrowUp => Key.Up,
                PhysicalKey.ArrowDown => Key.Down,
                PhysicalKey.ArrowLeft => Key.Left,
                PhysicalKey.ArrowRight => Key.Right,
                _ => key,
            };
        }

        if (mode.KittyFlags != KittyKeyboardFlags.None)
        {
            var kitty = EncodeKitty(
                key,
                modifiers,
                physicalKey,
                keySymbol,
                mode.KittyFlags,
                eventType);
            if (kitty is not null)
            {
                return kitty;
            }
        }

        if (mode.Win32InputMode && mode.KittyFlags == KittyKeyboardFlags.None)
        {
            return EncodeWin32(key, modifiers, keySymbol, eventType, repeatCount);
        }

        if (!mode.AnsiMode)
        {
            return eventType == TerminalKeyEventType.Release
                ? null
                : EncodeVt52(key, modifiers, mode.ApplicationKeypad);
        }

        if (eventType == TerminalKeyEventType.Release)
        {
            return null;
        }

        var ctrl = modifiers.HasFlag(KeyModifiers.Control);
        var alt = modifiers.HasFlag(KeyModifiers.Alt);
        var shift = modifiers.HasFlag(KeyModifiers.Shift);

        if (TrySingleRune(keySymbol, out var textRune) &&
            mode.ModifyOtherKeys > 0 &&
            (mode.ModifyOtherKeys == 2 || ctrl || alt))
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"\u001b[27;{KittyModifiers(modifiers)};{textRune.Value}~");
        }

        if (ctrl && !alt && !shift && key is >= Key.A and <= Key.Z)
        {
            return ((char)(key - Key.A + 1)).ToString();
        }

        if (ctrl && key == Key.Space)
        {
            return "\0";
        }

        var sequence = key switch
        {
            Key.Return or Key.LineFeed => "\r",
            Key.Tab => shift ? "\u001b[Z" : "\t",
            Key.Back => "\u007f",
            Key.Escape => "\u001b",
            Key.Up => mode.ApplicationCursorKeys ? "\u001bOA" : "\u001b[A",
            Key.Down => mode.ApplicationCursorKeys ? "\u001bOB" : "\u001b[B",
            Key.Right => mode.ApplicationCursorKeys ? "\u001bOC" : "\u001b[C",
            Key.Left => mode.ApplicationCursorKeys ? "\u001bOD" : "\u001b[D",
            Key.Home => mode.ApplicationCursorKeys ? "\u001bOH" : "\u001b[H",
            Key.End => mode.ApplicationCursorKeys ? "\u001bOF" : "\u001b[F",
            Key.Insert => "\u001b[2~",
            Key.Delete => "\u001b[3~",
            Key.PageUp => "\u001b[5~",
            Key.PageDown => "\u001b[6~",
            Key.F1 => "\u001bOP",
            Key.F2 => "\u001bOQ",
            Key.F3 => "\u001bOR",
            Key.F4 => "\u001bOS",
            Key.F5 => "\u001b[15~",
            Key.F6 => "\u001b[17~",
            Key.F7 => "\u001b[18~",
            Key.F8 => "\u001b[19~",
            Key.F9 => "\u001b[20~",
            Key.F10 => "\u001b[21~",
            Key.F11 => "\u001b[23~",
            Key.F12 => "\u001b[24~",
            >= Key.NumPad0 and <= Key.NumPad9 when mode.ApplicationKeypad =>
                $"\u001bO{(char)('p' + key - Key.NumPad0)}",
            Key.Decimal when mode.ApplicationKeypad => "\u001bOn",
            Key.Subtract when mode.ApplicationKeypad => "\u001bOm",
            Key.Add when mode.ApplicationKeypad => "\u001bOk",
            Key.Multiply when mode.ApplicationKeypad => "\u001bOj",
            Key.Divide when mode.ApplicationKeypad => "\u001bOo",
            _ => null,
        };

        if (sequence is not null)
        {
            return alt ? "\u001b" + sequence : sequence;
        }

        return !string.IsNullOrEmpty(keySymbol) && !ctrl && alt ? "\u001b" + keySymbol : null;
    }

    private static string? EncodeVt52(Key key, KeyModifiers modifiers, bool applicationKeypad)
    {
        var sequence = key switch
        {
            Key.Up => "\u001bA",
            Key.Down => "\u001bB",
            Key.Right => "\u001bC",
            Key.Left => "\u001bD",
            Key.F1 => "\u001bP",
            Key.F2 => "\u001bQ",
            Key.F3 => "\u001bR",
            Key.F4 => "\u001bS",
            >= Key.NumPad0 and <= Key.NumPad9 when applicationKeypad =>
                $"\u001b?{(char)('p' + key - Key.NumPad0)}",
            Key.Decimal when applicationKeypad => "\u001b?n",
            Key.Subtract when applicationKeypad => "\u001b?m",
            Key.Add when applicationKeypad => "\u001b?k",
            Key.Multiply when applicationKeypad => "\u001b?j",
            Key.Divide when applicationKeypad => "\u001b?o",
            Key.Return or Key.LineFeed => "\r",
            Key.Tab => "\t",
            Key.Back => "\b",
            Key.Escape => "\u001b",
            _ => null,
        };
        return sequence is not null && modifiers.HasFlag(KeyModifiers.Alt)
            ? "\u001b" + sequence
            : sequence;
    }

    private static string? EncodeKitty(
        Key key,
        KeyModifiers modifiers,
        PhysicalKey physicalKey,
        string? keySymbol,
        KittyKeyboardFlags flags,
        TerminalKeyEventType eventType)
    {
        if (eventType != TerminalKeyEventType.Press &&
            !flags.HasFlag(KittyKeyboardFlags.ReportEventTypes))
        {
            return null;
        }

        // Cursor/navigation/function keys already have a well-known legacy escape
        // sequence (e.g. "\u001b[A" for Up, "\u001b[3~" for Delete) recognized by
        // virtually every terminal application. Per the kitty keyboard protocol
        // spec, these keys keep using that legacy-compatible form -- with an
        // added modifier/event-type subfield when needed -- even when the
        // "disambiguate escape codes" flag is set; they only ever switch to the
        // numeric "CSI codepoint u" form for keys with no legacy representation.
        // Sending the raw private-use codepoint here (as opposed to the legacy
        // form) is not recognized by applications such as Claude Code's Ink-based
        // prompts, which silently drop the key event -- breaking arrow-key
        // navigation entirely.
        if (TryLegacyFunctionalKey(key, out var legacyForm))
        {
            var reportEvents = flags.HasFlag(KittyKeyboardFlags.ReportEventTypes);
            if (!flags.HasFlag(KittyKeyboardFlags.ReportAllKeysAsEscapeCodes) &&
                !flags.HasFlag(KittyKeyboardFlags.DisambiguateEscapeCodes) &&
                !reportEvents)
            {
                return null;
            }

            return EncodeLegacyFunctionalKey(legacyForm, modifiers, eventType, reportEvents);
        }

        var codepoint = KittyCodepoint(key);
        var textKey = codepoint == 0;
        if (textKey)
        {
            codepoint = KittyTextBaseCodepoint(key, physicalKey, keySymbol);
        }

        if (codepoint == 0)
        {
            return null;
        }

        var allKeys = flags.HasFlag(KittyKeyboardFlags.ReportAllKeysAsEscapeCodes);
        var disambiguate = flags.HasFlag(KittyKeyboardFlags.DisambiguateEscapeCodes);
        var altOrControl = modifiers.HasFlag(KeyModifiers.Alt) ||
                           modifiers.HasFlag(KeyModifiers.Control) ||
                           modifiers.HasFlag(KeyModifiers.Meta);
        var disambiguated = disambiguate &&
            (key == Key.Escape ||
             (key is Key.Return or Key.LineFeed or Key.Tab or Key.Back &&
              modifiers != KeyModifiers.None) ||
             (textKey && altOrControl) ||
             codepoint >= 57344);
        var release = eventType == TerminalKeyEventType.Release &&
                      flags.HasFlag(KittyKeyboardFlags.ReportEventTypes);
        if (!allKeys && !disambiguated && !release)
        {
            return null;
        }

        var modifier = KittyModifiers(modifiers);
        var eventSuffix = flags.HasFlag(KittyKeyboardFlags.ReportEventTypes) &&
                          eventType != TerminalKeyEventType.Press
            ? $":{(int)eventType}"
            : string.Empty;
        var associatedText = flags.HasFlag(KittyKeyboardFlags.ReportAssociatedText) &&
                             eventType != TerminalKeyEventType.Release &&
                             TrySingleRune(keySymbol, out var associatedRune) &&
                             IsKittyText(associatedRune) &&
                             !modifiers.HasFlag(KeyModifiers.Control)
            ? associatedRune.Value
            : 0;
        var modifierField = modifier != 1 || eventSuffix.Length > 0
            ? modifier.ToString(CultureInfo.InvariantCulture) + eventSuffix
            : string.Empty;
        if (associatedText != 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"\u001b[{codepoint};{modifierField};{associatedText}u");
        }

        if (modifierField.Length == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"\u001b[{codepoint}u");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"\u001b[{codepoint};{modifierField}u");
    }

    private readonly record struct LegacyFunctionalKeyForm(char? Letter, int? TildeNumber);

    private static bool TryLegacyFunctionalKey(Key key, out LegacyFunctionalKeyForm form)
    {
        form = key switch
        {
            Key.Up => new LegacyFunctionalKeyForm('A', null),
            Key.Down => new LegacyFunctionalKeyForm('B', null),
            Key.Right => new LegacyFunctionalKeyForm('C', null),
            Key.Left => new LegacyFunctionalKeyForm('D', null),
            Key.Home => new LegacyFunctionalKeyForm('H', null),
            Key.End => new LegacyFunctionalKeyForm('F', null),
            Key.Insert => new LegacyFunctionalKeyForm(null, 2),
            Key.Delete => new LegacyFunctionalKeyForm(null, 3),
            Key.PageUp => new LegacyFunctionalKeyForm(null, 5),
            Key.PageDown => new LegacyFunctionalKeyForm(null, 6),
            Key.F1 => new LegacyFunctionalKeyForm('P', null),
            Key.F2 => new LegacyFunctionalKeyForm('Q', null),
            // F3 has no letter form: "CSI R" conflicts with Cursor Position Report.
            Key.F3 => new LegacyFunctionalKeyForm(null, 13),
            Key.F4 => new LegacyFunctionalKeyForm('S', null),
            Key.F5 => new LegacyFunctionalKeyForm(null, 15),
            Key.F6 => new LegacyFunctionalKeyForm(null, 17),
            Key.F7 => new LegacyFunctionalKeyForm(null, 18),
            Key.F8 => new LegacyFunctionalKeyForm(null, 19),
            Key.F9 => new LegacyFunctionalKeyForm(null, 20),
            Key.F10 => new LegacyFunctionalKeyForm(null, 21),
            Key.F11 => new LegacyFunctionalKeyForm(null, 23),
            Key.F12 => new LegacyFunctionalKeyForm(null, 24),
            _ => default,
        };
        return form.Letter is not null || form.TildeNumber is not null;
    }

    private static string EncodeLegacyFunctionalKey(
        LegacyFunctionalKeyForm form,
        KeyModifiers modifiers,
        TerminalKeyEventType eventType,
        bool reportEvents)
    {
        var modifier = KittyModifiers(modifiers);
        var eventSuffix = reportEvents && eventType != TerminalKeyEventType.Press
            ? $":{(int)eventType}"
            : string.Empty;
        var modifierField = modifier != 1 || eventSuffix.Length > 0
            ? modifier.ToString(CultureInfo.InvariantCulture) + eventSuffix
            : string.Empty;

        if (form.Letter is char letter)
        {
            return modifierField.Length == 0
                ? $"\u001b[{letter}"
                : $"\u001b[1;{modifierField}{letter}";
        }

        return modifierField.Length == 0
            ? $"\u001b[{form.TildeNumber}~"
            : $"\u001b[{form.TildeNumber};{modifierField}~";
    }

    internal static string? EncodeKittyTextInput(string text, KittyKeyboardFlags flags)
    {
        if (!flags.HasFlag(KittyKeyboardFlags.ReportAssociatedText) ||
            string.IsNullOrEmpty(text))
        {
            return null;
        }

        var codepoints = new List<int>();
        foreach (var rune in text.EnumerateRunes())
        {
            if (!IsKittyText(rune))
            {
                return null;
            }
            codepoints.Add(rune.Value);
        }
        return codepoints.Count == 0
            ? null
            : $"\u001b[0;;{string.Join(':', codepoints)}u";
    }

    private static int KittyTextBaseCodepoint(
        Key key,
        PhysicalKey physicalKey,
        string? keySymbol)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            return 'a' + key - Key.A;
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            return '0' + key - Key.D0;
        }

        var physicalCodepoint = physicalKey switch
        {
            >= PhysicalKey.A and <= PhysicalKey.Z => 'a' + physicalKey - PhysicalKey.A,
            >= PhysicalKey.Digit0 and <= PhysicalKey.Digit9 =>
                '0' + physicalKey - PhysicalKey.Digit0,
            PhysicalKey.Space => ' ',
            PhysicalKey.Backquote => '`',
            PhysicalKey.Backslash => '\\',
            PhysicalKey.BracketLeft => '[',
            PhysicalKey.BracketRight => ']',
            PhysicalKey.Comma => ',',
            PhysicalKey.Equal => '=',
            PhysicalKey.Minus => '-',
            PhysicalKey.Period => '.',
            PhysicalKey.Quote => '\'',
            PhysicalKey.Semicolon => ';',
            PhysicalKey.Slash => '/',
            _ => 0,
        };
        if (physicalCodepoint != 0)
        {
            return physicalCodepoint;
        }

        return TrySingleRune(keySymbol, out var rune) ? rune.Value : 0;
    }

    private static bool IsKittyText(Rune rune) =>
        rune.Value is > 0x1F and < 0x7F or > 0x9F;

    // Keys with a legacy escape sequence (arrows, Home/End, Insert/Delete,
    // PageUp/PageDown, F1-F12) are handled earlier by TryLegacyFunctionalKey /
    // EncodeLegacyFunctionalKey and never reach this codepoint table.
    private static int KittyCodepoint(Key key) => key switch
    {
        Key.Escape => 27,
        Key.Return or Key.LineFeed => 13,
        Key.Tab => 9,
        Key.Back => 127,
        _ => 0,
    };

    private static string? EncodeWin32(
        Key key,
        KeyModifiers modifiers,
        string? keySymbol,
        TerminalKeyEventType eventType,
        ushort repeatCount)
    {
        var virtualKey = VirtualKey(key);
        var unicode = TrySingleRune(keySymbol, out var rune) ? rune.Value : 0;
        if (virtualKey == 0 && unicode == 0)
        {
            return null;
        }

        var keyDown = eventType == TerminalKeyEventType.Release ? 0 : 1;
        var controlState =
            (modifiers.HasFlag(KeyModifiers.Shift) ? 0x10 : 0) |
            (modifiers.HasFlag(KeyModifiers.Alt) ? 0x02 : 0) |
            (modifiers.HasFlag(KeyModifiers.Control) ? 0x08 : 0);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"\u001b[{virtualKey};0;{unicode};{keyDown};{controlState};{Math.Max(1, (int)repeatCount)}_");
    }

    private static int VirtualKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => 0x41 + key - Key.A,
        >= Key.D0 and <= Key.D9 => 0x30 + key - Key.D0,
        >= Key.NumPad0 and <= Key.NumPad9 => 0x60 + key - Key.NumPad0,
        Key.Return or Key.LineFeed => 0x0D,
        Key.Tab => 0x09,
        Key.Back => 0x08,
        Key.Escape => 0x1B,
        Key.Left => 0x25,
        Key.Up => 0x26,
        Key.Right => 0x27,
        Key.Down => 0x28,
        Key.Delete => 0x2E,
        Key.Insert => 0x2D,
        Key.Home => 0x24,
        Key.End => 0x23,
        Key.PageUp => 0x21,
        Key.PageDown => 0x22,
        >= Key.F1 and <= Key.F12 => 0x70 + key - Key.F1,
        Key.LeftShift or Key.RightShift => 0x10,
        Key.LeftCtrl or Key.RightCtrl => 0x11,
        Key.LeftAlt or Key.RightAlt => 0x12,
        Key.LWin or Key.RWin => 0x5B,
        Key.Space => 0x20,
        Key.OemSemicolon => 0xBA,
        Key.OemPlus => 0xBB,
        Key.OemComma => 0xBC,
        Key.OemMinus => 0xBD,
        Key.OemPeriod => 0xBE,
        Key.OemQuestion => 0xBF,
        Key.OemTilde => 0xC0,
        Key.OemOpenBrackets => 0xDB,
        Key.OemPipe => 0xDC,
        Key.OemCloseBrackets => 0xDD,
        Key.OemQuotes => 0xDE,
        Key.OemBackslash => 0xE2,
        _ => 0,
    };

    public static string? NormalizeOptionAsMetaSymbol(
        Key key,
        KeyModifiers modifiers,
        string? keySymbol,
        bool optionAsMeta)
    {
        if (!optionAsMeta ||
            !modifiers.HasFlag(KeyModifiers.Alt) ||
            modifiers.HasFlag(KeyModifiers.Control) ||
            modifiers.HasFlag(KeyModifiers.Meta))
        {
            return keySymbol;
        }

        if (key is >= Key.A and <= Key.Z)
        {
            var letter = (char)((modifiers.HasFlag(KeyModifiers.Shift) ? 'A' : 'a') + (key - Key.A));
            return letter.ToString();
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((char)('0' + (key - Key.D0))).ToString();
        }

        return keySymbol;
    }

    private static int KittyModifiers(KeyModifiers modifiers) =>
        1 +
        (modifiers.HasFlag(KeyModifiers.Shift) ? 1 : 0) +
        (modifiers.HasFlag(KeyModifiers.Alt) ? 2 : 0) +
        (modifiers.HasFlag(KeyModifiers.Control) ? 4 : 0) +
        (modifiers.HasFlag(KeyModifiers.Meta) ? 8 : 0);

    private static bool TrySingleRune(string? text, out Rune rune)
    {
        rune = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var status = Rune.DecodeFromUtf16(text, out rune, out var consumed);
        return status == System.Buffers.OperationStatus.Done && consumed == text.Length;
    }
}
