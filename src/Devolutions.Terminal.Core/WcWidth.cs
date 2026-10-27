using System.Globalization;
using System.Text;

namespace Devolutions.Terminal.Core;

public static class WcWidth
{
    public static bool AmbiguousAsWide { get; set; }

    private const byte Zero = 0;
    private const byte Narrow = 1;
    private const byte Wide = 2;
    private const byte Ambiguous = 3;

    // The width class of every scalar in planes 0 and 1 (which hold the emoji),
    // from the same rules as the rest, so printing does not repeat category and
    // range lookups per character. Ambiguous stays a class of its own because
    // AmbiguousAsWide can change.
    private static readonly byte[] Classes = BuildClasses();

    public static int Width(Rune rune)
    {
        var value = rune.Value;
        var widthClass = value < Classes.Length ? Classes[value] : Classify(value);
        return widthClass == Ambiguous ? (AmbiguousAsWide ? 2 : 1) : widthClass;
    }

    /// <summary>0 zero width, 1 narrow, 2 wide, 3 ambiguous (2 when <see cref="AmbiguousAsWide"/>).</summary>
    internal static byte WidthClass(Rune rune) =>
        rune.Value < Classes.Length ? Classes[rune.Value] : Classify(rune.Value);

    internal static int ResolveWidth(byte widthClass) => ResolveWidth(widthClass, AmbiguousAsWide);

    internal static int ResolveWidth(byte widthClass, bool ambiguousAsWide) =>
        widthClass == Ambiguous ? (ambiguousAsWide ? 2 : 1) : widthClass;

    private static byte[] BuildClasses()
    {
        var classes = new byte[0x20000];
        for (var value = 0; value < classes.Length; value++)
        {
            classes[value] = Classify(value);
        }

        return classes;
    }

    private static byte Classify(int value)
    {
        if (value == 0)
        {
            return Zero;
        }

        if (value < 0x20 || (value >= 0x7F && value < 0xA0))
        {
            return Zero;
        }

        if (value is >= 0x1F3FB and <= 0x1F3FF)
        {
            return Zero;
        }

        var category = value is >= 0xD800 and <= 0xDFFF
            ? UnicodeCategory.Surrogate
            : Rune.GetUnicodeCategory(new Rune(value));
        if (category is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.EnclosingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.Surrogate)
        {
            return Zero;
        }

        if (IsWide(value))
        {
            return Wide;
        }

        return IsAmbiguous(value) ? Ambiguous : Narrow;
    }

    private static bool IsWide(int v) =>
        v is >= 0x1100 and <= 0x115F
        || v is 0x2329 or 0x232A
        || v is >= 0x2E80 and <= 0xA4CF && v is not 0x303F
        || v is >= 0xAC00 and <= 0xD7A3
        || v is >= 0xF900 and <= 0xFAFF
        || v is >= 0xFE10 and <= 0xFE19
        || v is >= 0xFE30 and <= 0xFE6F
        || v is >= 0xFF00 and <= 0xFF60
        || v is >= 0xFFE0 and <= 0xFFE6
        || v is >= 0x1F1E6 and <= 0x1F1FF
        || IsEmojiPresentationWide(v)
        || v is >= 0x20000 and <= 0x3FFFD;

    private static bool IsEmojiPresentationWide(int v) =>
        v is 0x231A or 0x231B or 0x23F0 or 0x23F3 or
            0x25FD or 0x25FE or 0x2614 or 0x2615 or 0x267F or 0x2693 or
            0x26A1 or 0x26AA or 0x26AB or 0x26BD or 0x26BE or 0x26C4 or
            0x26C5 or 0x26CE or 0x26D4 or 0x26EA or 0x26F2 or 0x26F3 or
            0x26F5 or 0x26FA or 0x26FD or 0x2705 or 0x270A or 0x270B or
            0x2728 or 0x274C or 0x274E or 0x2753 or 0x2754 or 0x2755 or
            0x2757 or 0x2795 or 0x2796 or 0x2797 or 0x27B0 or 0x27BF or
            >= 0x23E9 and <= 0x23EC or
            >= 0x2648 and <= 0x2653 or
            >= 0x1F1E6 and <= 0x1F1FF or
            >= 0x1F300 and <= 0x1FAFF;

    private static bool IsAmbiguous(int v) =>
        v is >= 0x00A1 and <= 0x00FF
        || v is >= 0x2010 and <= 0x2027
        || v is >= 0x2030 and <= 0x205E
        || v is >= 0x2100 and <= 0x245F
        || v is >= 0x2460 and <= 0x24FF
        || v is >= 0x2500 and <= 0x25FF
        || v is >= 0x2600 and <= 0x27BF
        || v is >= 0x2985 and <= 0x2986;
}
