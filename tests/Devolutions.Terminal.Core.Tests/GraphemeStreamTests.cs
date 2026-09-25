using System.Text;
using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Core.Tests;

[Collection("Unicode cell width observations")]
public sealed class GraphemeStreamTests
{
    private const int WideColumns = 80;
    private const int WideRows = 3;

    [Theory]
    [MemberData(nameof(GraphemeBreakFixtureTests.RepresentableCases),
        MemberType = typeof(GraphemeBreakFixtureTests))]
    public void RepresentableFixtureRowIsInvariantAcrossBytesAndScalarStringChunks(
        int line, string hex)
    {
        var clusters = GraphemeBreakFixtureTests.RepresentableClustersForStream(line, hex);
        var text = string.Concat(clusters);
        var bytes = Encoding.UTF8.GetBytes(text);
        var oneShot = NewEngine();
        oneShot.Feed(bytes);
        AssertOrderedClusters(oneShot, clusters, $"fixture line {line}: {hex}");
        var expected = Capture(oneShot);

        var singleBytes = NewEngine();
        foreach (var value in bytes)
        {
            singleBytes.Feed(new byte[] { value });
        }

        Assert.Equal(expected, Capture(singleBytes));

        var scalarChunks = NewEngine();
        foreach (var rune in text.EnumerateRunes())
        {
            scalarChunks.Feed(rune.ToString());
        }

        Assert.Equal(expected, Capture(scalarChunks));
        var direct = new TextBuffer(WideColumns, WideRows, 0, false);
        foreach (var rune in text.EnumerateRunes())
        {
            direct.Print(rune);
        }

        Assert.Equal(oneShot.CursorX, direct.CursorX);
        Assert.Equal(oneShot.CursorY, direct.CursorY);
        Assert.Equal(oneShot.Buffer.WrapPending, direct.WrapPending);
        for (var y = 0; y < WideRows; y++)
        {
            for (var x = 0; x < WideColumns; x++)
            {
                Assert.Equal(direct.GetCell(x, y).Text, oneShot.Buffer.GetCell(x, y).Text);
                Assert.Equal(direct.GetCell(x, y).IsWideContinuation,
                    oneShot.Buffer.GetCell(x, y).IsWideContinuation);
            }
        }
    }

    [Theory]
    [InlineData("👩\u200D💻X", 8, 2, "👩\u200D💻", "X")]
    [InlineData("\U0001F1E6\u0308\U0001F1E7X", 9, 2, "\U0001F1E6\u0308", "\U0001F1E7")]
    [InlineData("क\u094DषX", 8, 1, "क\u094Dष", "X")]
    [InlineData("A\u094DकX", 8, 1, "A\u094D", "क")]
    [InlineData("\u1100\u1161\u11A8X", 8, 2, "\u1100\u1161\u11A8", "X")]
    [InlineData("\u0600A\u0903", 8, -1, "\u0600A\u0903", "")]
    [InlineData("\u0600A\u0903X", 8, 1, "\u0600A\u0903", "X")]
    [InlineData("❤️\u200D🔥X", 8, 2, "❤️\u200D🔥", "X")]
    [InlineData("A👩\u200D💻B", 3, 1, "A", "👩\u200D💻")]
    [InlineData("AA👩\u200D💻B", 3, 1, "A", "A")]
    public void FocusedGraphemesAreInvariantAcrossEveryByteAndScalarSplit(
        string text, int columns, int firstColumn, string firstCluster, string secondCluster)
    {
        var original = new TerminalEngine(columns, 3);
        original.Feed(text);
        Assert.Equal(firstCluster, original.Buffer.GetCell(0, 0).Text);
        if (firstColumn < 0)
        {
            Assert.Equal(" ", original.Buffer.GetCell(1, 0).Text);
            Assert.Equal(1, original.CursorX);
        }
        else
        {
            Assert.Equal(secondCluster, original.Buffer.GetCell(firstColumn, 0).Text);
        }
        var expected = Capture(original);
        var bytes = Encoding.UTF8.GetBytes(text);

        // Includes empty first/last chunks, and every cut inside a UTF-8 scalar.
        for (var split = 0; split <= bytes.Length; split++)
        {
            var engine = new TerminalEngine(columns, 3);
            engine.Feed(bytes.AsSpan(0, split));
            engine.Feed(bytes.AsSpan(split));
            Assert.Equal(expected, Capture(engine));
        }

        var scalars = text.EnumerateRunes().Select(rune => rune.ToString()).ToArray();
        for (var split = 0; split <= scalars.Length; split++)
        {
            var engine = new TerminalEngine(columns, 3);
            engine.Feed(string.Concat(scalars.Take(split)));
            engine.Feed(string.Concat(scalars.Skip(split)));
            Assert.Equal(expected, Capture(engine));
        }
    }

    [Fact]
    public void IncompleteUtf8NeverPrintsBeforeFinalByteAndThenJoinsGrapheme()
    {
        var engine = new TerminalEngine(8, 2);
        engine.Feed("A");
        var emoji = Encoding.UTF8.GetBytes("👩");
        for (var i = 0; i < emoji.Length - 1; i++)
        {
            engine.Feed(emoji.AsSpan(i, 1));
            Assert.Equal("A", engine.Buffer.GetCell(0, 0).Text);
            Assert.Equal(" ", engine.Buffer.GetCell(1, 0).Text);
            Assert.Equal(1, engine.CursorX);
            Assert.False(engine.Buffer.WrapPending);
        }

        engine.Feed(emoji.AsSpan(emoji.Length - 1));
        Assert.Equal("👩", engine.Buffer.GetCell(1, 0).Text);
        Assert.True(engine.Buffer.GetCell(2, 0).IsWideContinuation);
        Assert.Equal(3, engine.CursorX);
        engine.Feed("\u200D💻B");
        Assert.Equal("👩\u200D💻", engine.Buffer.GetCell(1, 0).Text);
        Assert.Equal("B", engine.Buffer.GetCell(3, 0).Text);
        Assert.Equal(4, engine.CursorX);
    }

    [Fact]
    public void ExactFitAndAdjacentRolloverKeepClustersIntactAcrossRows()
    {
        var exactFit = new TerminalEngine(3, 3);
        exactFit.Feed("A👩\u200D💻B");
        Assert.Equal("A", exactFit.Buffer.GetCell(0, 0).Text);
        Assert.Equal("👩\u200D💻", exactFit.Buffer.GetCell(1, 0).Text);
        Assert.True(exactFit.Buffer.GetCell(2, 0).IsWideContinuation);
        Assert.Equal("B", exactFit.Buffer.GetCell(0, 1).Text);
        Assert.Equal(1, exactFit.CursorX);
        Assert.Equal(1, exactFit.CursorY);
        Assert.False(exactFit.Buffer.WrapPending);

        var rollover = new TerminalEngine(3, 3);
        rollover.Feed("AA👩\u200D💻B");
        Assert.Equal("A", rollover.Buffer.GetCell(1, 0).Text);
        Assert.Equal("👩\u200D💻", rollover.Buffer.GetCell(0, 1).Text);
        Assert.True(rollover.Buffer.GetCell(1, 1).IsWideContinuation);
        Assert.Equal("B", rollover.Buffer.GetCell(2, 1).Text);
        Assert.Equal(2, rollover.CursorX);
        Assert.Equal(1, rollover.CursorY);
        Assert.True(rollover.Buffer.WrapPending);
    }

    [Theory]
    [InlineData("\r", "B", 0, 0, 1, 0)]
    [InlineData("\n", "B", 1, 1, 2, 1)]
    [InlineData("\t", "B", 8, 0, 9, 0)]
    [InlineData("\b", "B", 0, 0, 1, 0)]
    [InlineData("\u001b[2C", "B", 3, 0, 4, 0)]
    [InlineData("\u007f", "B", 1, 0, 2, 0)]
    public void TerminalControlsPositionFollowingPrintableWithoutBecomingClusters(
        string control, string following, int x, int y, int cursorX, int cursorY)
    {
        var oneShot = new TerminalEngine(12, 3);
        oneShot.Feed("A\u0308" + control + following);
        Assert.Equal("B", oneShot.Buffer.GetCell(x, y).Text);
        Assert.False(oneShot.Buffer.GetCell(x, y).IsWideContinuation);
        Assert.Equal(cursorX, oneShot.CursorX);
        Assert.Equal(cursorY, oneShot.CursorY);
        if (x != 0 || y != 0)
        {
            Assert.Equal("A\u0308", oneShot.Buffer.GetCell(0, 0).Text);
        }

        var bytes = Encoding.UTF8.GetBytes("A\u0308" + control + following);
        var chunks = new TerminalEngine(12, 3);
        foreach (var value in bytes)
        {
            chunks.Feed(new byte[] { value });
        }

        Assert.Equal(Capture(oneShot), Capture(chunks));
    }

    [Fact]
    public void RawC1CsiMovesCursorAndDelIsIgnoredWithoutPrintingEither()
    {
        // The fixture's raw control class is dispatched by the terminal, not
        // rendered as the UAX grapheme-boundary markers in GraphemeBreakTest.
        byte[] bytes = [0x41, 0xCC, 0x88, 0x7F, 0x9B, (byte)'2', (byte)'C', (byte)'B'];
        var oneShot = new TerminalEngine(12, 2);
        oneShot.Feed(bytes);
        Assert.Equal("A\u0308", oneShot.Buffer.GetCell(0, 0).Text);
        Assert.Equal(" ", oneShot.Buffer.GetCell(1, 0).Text);
        Assert.Equal(" ", oneShot.Buffer.GetCell(2, 0).Text);
        Assert.Equal("B", oneShot.Buffer.GetCell(3, 0).Text);
        Assert.Equal(4, oneShot.CursorX);
        Assert.False(oneShot.Buffer.WrapPending);

        var chunks = new TerminalEngine(12, 2);
        foreach (var value in bytes)
        {
            chunks.Feed(new byte[] { value });
        }

        Assert.Equal(Capture(oneShot), Capture(chunks));
    }

    [Fact]
    public void CrLfExecutesTerminalControlsInsteadOfPrintingGb3Cluster()
    {
        // The fixture's GB3 cluster "\r\n" is for a pure segmenter; the
        // terminal executes CR and LF and never stores them in a cell.
        var oneShot = new TerminalEngine(8, 3);
        oneShot.Feed("A\u0308\r\nB");
        Assert.Equal("A\u0308", oneShot.Buffer.GetCell(0, 0).Text);
        Assert.Equal("B", oneShot.Buffer.GetCell(0, 1).Text);
        Assert.Equal(" ", oneShot.Buffer.GetCell(1, 0).Text);
        Assert.Equal(1, oneShot.CursorX);
        Assert.Equal(1, oneShot.CursorY);

        var chunks = new TerminalEngine(8, 3);
        foreach (var value in Encoding.UTF8.GetBytes("A\u0308\r\nB"))
        {
            chunks.Feed(new byte[] { value });
        }

        Assert.Equal(Capture(oneShot), Capture(chunks));
    }

    private static TerminalEngine NewEngine() => new(WideColumns, WideRows);

    private static void AssertOrderedClusters(
        TerminalEngine engine, IReadOnlyList<string> clusters, string context)
    {
        var x = 0;
        foreach (var cluster in clusters)
        {
            var cell = engine.Buffer.GetCell(x, 0);
            Assert.Equal(cluster, cell.Text);
            Assert.False(cell.IsWideContinuation);
            var width = cell.DisplayWidth;
            Assert.InRange(width, 1, 2);
            if (width == 2)
            {
                Assert.Equal("", engine.Buffer.GetCell(x + 1, 0).Text);
                Assert.True(engine.Buffer.GetCell(x + 1, 0).IsWideContinuation);
            }

            x += width;
            Assert.True(x < WideColumns, $"{context}: expected clusters exceeded row");
        }

        Assert.Equal(x, engine.CursorX);
        Assert.Equal(0, engine.CursorY);
        Assert.False(engine.Buffer.WrapPending);
        Assert.Equal(" ", engine.Buffer.GetCell(x, 0).Text);
    }

    private static string Capture(TerminalEngine engine)
    {
        var result = new StringBuilder();
        result.Append(engine.CursorX).Append(',').Append(engine.CursorY)
            .Append(',').Append(engine.Buffer.WrapPending);
        for (var y = 0; y < engine.Rows; y++)
        {
            for (var x = 0; x < engine.Columns; x++)
            {
                var cell = engine.Buffer.GetCell(x, y);
                result.Append('|').Append(cell.Text.Length).Append(':').Append(cell.Text)
                    .Append(':').Append(cell.DisplayWidth)
                    .Append(':').Append(cell.IsWideContinuation);
            }
        }

        return result.ToString();
    }
}
