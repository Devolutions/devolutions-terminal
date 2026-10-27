using System.Text;
using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Core.Tests;

/// <summary>
/// The parser hands printable-ASCII runs to <see cref="IVtDispatch.PrintAscii"/>
/// and the engine stores them in bulk; with bulk storage disabled every byte
/// takes the per-character path, and both must leave the terminal in exactly the
/// same state. DEC Special Graphics maps to
/// ambiguous-width box drawing, and one test here sets
/// <see cref="WcWidth.AmbiguousAsWide"/>, so this class stays clear of tests
/// toggling it.
/// </summary>
[Collection("Unicode cell width observations")]
public sealed class AsciiFastPathTests
{
    private const string Esc = "\u001b";

    public static TheoryData<string, string> Streams => new()
    {
        { "wrap", new string('a', 50) + "\r\n" + new string('b', 21) + "xyz" },
        { "prepend joins first byte", "ab\u0600cd" + "\u0600" + "efgh" },
        { "wrap over wide cells", "\u4E00\u4E00\u4E00\u4E00\r" + "abcdefghij" },
        { "run ends on wide head", "\u4E00\u4E00\u4E00\r" + "abc" },
        { "run starts on wide tail", $"\u4E00\u4E00\u4E00{Esc}[4Gxyz" },
        { "runes start on wide tail", $"\u4E00\u4E00\u4E00{Esc}[2G\u0436\u0438" },
        { "runes end on wide head", "\u4E00\u4E00\u4E00\r\u0436\u0438\u0437" },
        { "wide runes start on wide tail", $"\u4E00\u4E00\u4E00{Esc}[2G\u4E2D\u6587" },
        { "wide runes end on wide head", "\u4E00\u4E00\u4E00\r\u0436\u4E2D" },
        { "wide runes wrap", new string('a', 19) + "\u4E2D\u6587\u0436\U0001F600\U0001F680" },
        { "zwj family after runes", "\u0436\U0001F468\u200D\U0001F469\u200D\U0001F467\u0438" },
        { "dec graphics", $"{Esc}(0lqqk{Esc}(B plain" },
        { "single shift", $"{Esc}*0{Esc}Nqq plain" },
        { "insert mode", $"0123456789\r{Esc}[4hab{Esc}[4lcd" },
        { "autowrap off", $"{Esc}[?7l{new string('x', 30)}yz{Esc}[?7h" },
        { "left right margins", $"{Esc}[?69h{Esc}[3;8s{Esc}[1;3H{new string('m', 25)}{Esc}[?69l" },
        { "double width line", $"{Esc}#6{new string('d', 15)}" },
        { "repeat last", $"abc{Esc}[3b" },
        { "styled hyperlink", $"{Esc}[1;31mred{Esc}]8;;https://example.test\u0007link{Esc}]8;;\u0007{Esc}[0m" },
        { "region scroll", $"{Esc}[2;4r{Esc}[4;1H" + string.Concat(Enumerable.Range(0, 12).Select(i => $"line {i}\r\n")) },
        { "history overflow", string.Concat(Enumerable.Range(0, 40).Select(i => $"{Esc}[{31 + (i % 7)}mrow {i:D3} {new string('#', i % 23)}\r\n")) },
    };

    [Theory]
    [MemberData(nameof(Streams))]
    public void BulkAsciiMatchesPerCharacterPrinting(string name, string stream)
    {
        AssertBulkMatchesReference(name, Encoding.UTF8.GetBytes(stream), 20, 5, 8);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BulkRunesMatchPerCharacterPrintingWithAmbiguousWidths(bool ambiguousAsWide)
    {
        // Bulk printing reads one width table per AmbiguousAsWide setting. Latin-1
        // symbols, punctuation and box drawing are ambiguous; the long runs wrap.
        var stream =
            "±§ —…“” ┌─┐\r\n" +
            new string('─', 15) + "\r\n" +
            new string('a', 13) + "±─中│°═║";
        WcWidth.AmbiguousAsWide = ambiguousAsWide;
        try
        {
            AssertBulkMatchesReference($"ambiguous as wide: {ambiguousAsWide}", Encoding.UTF8.GetBytes(stream), 20, 5, 8);
        }
        finally
        {
            WcWidth.AmbiguousAsWide = false;
        }
    }

    [Fact]
    public void BulkAsciiMatchesPerCharacterPrintingOnRandomStreams()
    {
        string[] pieces =
        [
            "\r\n", "\n", "\r", "\t", "\b", $"{Esc}[K", $"{Esc}[2J", $"{Esc}[H", $"{Esc}[3;5H",
            $"{Esc}[1m", $"{Esc}[0m", $"{Esc}[38;5;200m", $"{Esc}[4h", $"{Esc}[4l", $"{Esc}[?7l", $"{Esc}[?7h",
            $"{Esc}(0", $"{Esc}(B", $"{Esc}#6", $"{Esc}#5", $"{Esc}[2;4r", $"{Esc}[r", $"{Esc}[2b", $"{Esc}M",
            "\u0600", "\u0301", "\u4E00", "\U0001F600", "\u200D", "e\u0301",
            "\u0436\u0438\u0437\u043D\u044C", "\u4E2D\u6587\u5B57", "\U0001F600\U0001F680", "\U0001F468\u200D\U0001F469",
            "\u2764\uFE0F", "\U0001F1EB\U0001F1F7", "\u1100\u1161\u11A8", "\u0915\u094D\u0937", "\u00E9\u00FC\u00DF",
            "\u0928\u093F", "\u0600\u0628",
        ];
        var random = new Random(20261026);
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var builder = new StringBuilder();
            for (var piece = 0; piece < 60; piece++)
            {
                if (random.Next(3) == 0)
                {
                    builder.Append(pieces[random.Next(pieces.Length)]);
                }
                else
                {
                    var length = random.Next(1, 30);
                    for (var index = 0; index < length; index++)
                    {
                        builder.Append((char)random.Next(0x20, 0x7F));
                    }
                }
            }

            AssertBulkMatchesReference($"random #{iteration}", Encoding.UTF8.GetBytes(builder.ToString()), 17, 4, 6);
        }
    }

    [Fact]
    public void AsciiAfterCompletedPrependCellJoinsLikePrint()
    {
        // Live feeds always complete a Prepend with the next scalar, but a
        // projected screen can hold one alone; ASCII must then join it (GB9b).
        static TextBuffer Create()
        {
            var buffer = new TextBuffer(8, 2, 0, hasHistory: false);
            buffer.ReplaceViewport(
                [new TextBufferProjectionRow([new Cell { Rune = new Rune(0x0600), StoredWidth = 1 }], false, [])],
                1,
                0);
            return buffer;
        }

        var lone = Create();
        Assert.False(lone.PrintAscii("x"u8));

        var bulk = Create();
        var reference = Create();
        Assert.True(bulk.PrintAscii("xy"u8));
        reference.Print(new Rune('x'));
        reference.Print(new Rune('y'));

        Assert.Equal("\u0600x", bulk.GetCell(0, 0).Text);
        Assert.Equal(reference.GetRow(0), bulk.GetRow(0));
        Assert.Equal(reference.GetRow(0), lone.GetRow(0).Select((cell, x) => x == 1 ? reference.GetCell(1, 0) : cell).ToArray());
        Assert.Equal((reference.CursorX, reference.CursorY), (bulk.CursorX, bulk.CursorY));
    }

    // The reference prints every byte through the per-character path. The bulk
    // engine gets the stream whole (long runs) and one byte at a time (runs of
    // one, split anywhere); both must end in the reference's state.
    private static void AssertBulkMatchesReference(string name, byte[] bytes, int columns, int rows, int history)
    {
        using var reference = new TerminalEngine(columns, rows, history) { BulkPrintEnabled = false };
        using var whole = new TerminalEngine(columns, rows, history);
        using var single = new TerminalEngine(columns, rows, history);
        reference.Feed(bytes);
        whole.Feed(bytes);
        foreach (var value in bytes)
        {
            single.Feed([value]);
        }

        AssertSameState($"{name} (whole)", reference, whole);
        AssertSameState($"{name} (byte by byte)", reference, single);
    }

    [Fact]
    public void RecycledHistoryLineDoesNotChangeEarlierSnapshots()
    {
        using var engine = new TerminalEngine(10, 2, historySize: 2);
        engine.Feed("one\r\ntwo\r\nthree\r\nfour");
        var before = engine.CreateSnapshot(includeHistory: true);
        var beforeText = Text(before.Buffer.Lines[0]);

        engine.Feed($"\r\n{Esc}[44mfive\r\nsix");

        Assert.Equal("one", beforeText);
        Assert.Equal("one", Text(before.Buffer.Lines[0]));
        var after = engine.CreateSnapshot(includeHistory: true);
        Assert.Equal(["three", "four", "five", "six"], after.Buffer.Lines.Select(Text));
        // The recycled line was blanked with the current (BCE) background.
        Assert.Equal(ColorKind.Indexed, after.Buffer.Lines[3].Cells[9].Attributes.Background.Kind);
        Assert.Equal(4, after.Buffer.Lines[3].Cells[9].Attributes.Background.Index);
    }

    private static void AssertSameState(string name, TerminalEngine expected, TerminalEngine actual)
    {
        Assert.True(
            (expected.CursorX, expected.CursorY, expected.HistoryCount) ==
            (actual.CursorX, actual.CursorY, actual.HistoryCount),
            $"{name}: cursor/history ({expected.CursorX},{expected.CursorY},{expected.HistoryCount}) " +
            $"vs ({actual.CursorX},{actual.CursorY},{actual.HistoryCount})");
        Assert.True(expected.Buffer.WrapPending == actual.Buffer.WrapPending, $"{name}: wrap pending");
        var left = expected.CreateSnapshot(includeHistory: true).Buffer.Lines;
        var right = actual.CreateSnapshot(includeHistory: true).Buffer.Lines;
        Assert.True(left.Count == right.Count, $"{name}: line count");
        for (var line = 0; line < left.Count; line++)
        {
            Assert.True(left[line].Wrapped == right[line].Wrapped, $"{name}: line {line} wrapped");
            Assert.True(left[line].Rendition == right[line].Rendition, $"{name}: line {line} rendition");
            Assert.True(left[line].LogicalOffset == right[line].LogicalOffset, $"{name}: line {line} logical offset");
            for (var column = 0; column < left[line].Cells.Count; column++)
            {
                Assert.True(
                    left[line].Cells[column].Equals(right[line].Cells[column]),
                    $"{name}: line {line} column {column}: '{left[line].Cells[column].Text}' vs '{right[line].Cells[column].Text}'");
            }
        }
    }

    private static string Text(TextBufferLineSnapshot line) =>
        string.Concat(line.Cells.Select(static cell => cell.Text)).TrimEnd();
}
