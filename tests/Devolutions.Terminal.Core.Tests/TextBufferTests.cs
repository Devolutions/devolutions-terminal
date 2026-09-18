using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Core.Tests;

public sealed class TextBufferTests
{
    [Fact]
    public void ResizePreservesWrapPendingAtDoubleWidthBoundary()
    {
        using var engine = new TerminalEngine(10, 3);
        engine.Feed("\u001b#6abcde");
        Assert.True(engine.Buffer.WrapPending);

        engine.Resize(10, 4);
        Assert.True(engine.Buffer.WrapPending);

        engine.Feed("f");
        Assert.Equal('f', (char)engine.Buffer.GetRow(1)[0].Rune.Value);
    }

    [Fact]
    public void ScrollbackIsBoundedAndKeepsNewestLines()
    {
        var engine = new TerminalEngine(8, 2, historySize: 2);

        engine.Feed("one\r\ntwo\r\nthree\r\nfour");

        Assert.Equal(2, engine.Buffer.HistoryCount);
        Assert.Equal(4, engine.Buffer.TotalLines);
        engine.Buffer.ScrollOffset = 2;
        Assert.Equal("one", RowText(engine.Buffer, 0));
        Assert.Equal("two", RowText(engine.Buffer, 1));
        engine.Buffer.ScrollOffset = 0;
        Assert.Equal("three", RowText(engine.Buffer, 0));
        Assert.Equal("four", RowText(engine.Buffer, 1));
    }

    [Fact]
    public void OutputWhileScrolledBackWritesToLiveViewport()
    {
        var engine = new TerminalEngine(8, 2, historySize: 4);
        engine.Feed("one\r\ntwo\r\nthree");
        engine.Buffer.ScrollOffset = 1;

        engine.Feed("\r\nfour");

        engine.Buffer.ScrollOffset = 0;
        Assert.Equal("three", RowText(engine.Buffer, 0));
        Assert.Equal("four", RowText(engine.Buffer, 1));
        engine.Buffer.ScrollOffset = 2;
        Assert.Equal("one", RowText(engine.Buffer, 0));
    }

    [Fact]
    public void ResizeReflowsWrappedContentAndKeepsWideGlyphsWhole()
    {
        var engine = new TerminalEngine(6, 3);
        engine.Feed("ab界cdEF");

        engine.Resize(4, 4);

        Assert.Equal("ab界cdEF", engine.Buffer.GetVisibleText().Replace("\n", string.Empty, StringComparison.Ordinal));
        for (var y = 0; y < engine.Rows; y++)
        {
            var row = engine.Buffer.GetRow(y);
            Assert.False(row[0].IsWideContinuation);
            if (WcWidth.Width(row[^1].Rune) == 2)
            {
                Assert.True(row[^1].IsBlank);
            }
        }
    }

    [Fact]
    public void CombiningCharactersStayWithTheirBaseCell()
    {
        var engine = new TerminalEngine(8, 2);

        engine.Feed("e\u0301");

        Assert.Equal("e\u0301", engine.Buffer.GetCell(0, 0).Text);
        Assert.Equal(1, engine.CursorX);
    }

    [Fact]
    public void OverwritingWideContinuationClearsWholeGlyph()
    {
        var engine = new TerminalEngine(8, 2);
        engine.Feed("界\u001b[1;2HX");

        Assert.True(engine.Buffer.GetCell(0, 0).IsBlank);
        Assert.Equal("X", engine.Buffer.GetCell(1, 0).Text);
        Assert.False(engine.Buffer.GetCell(1, 0).IsWideContinuation);
    }

    [Fact]
    public void TabStopsCanBeSetAndCleared()
    {
        var engine = new TerminalEngine(20, 2);
        engine.Feed("\u001b[4G\u001bH\r\t");
        Assert.Equal(3, engine.CursorX);

        engine.Feed("\u001b[g\r\t");
        Assert.Equal(8, engine.CursorX);

        engine.Feed("\u001b[3g\r\t");
        Assert.Equal(19, engine.CursorX);
    }

    [Fact]
    public void SnapshotDoesNotAliasMutableRows()
    {
        var engine = new TerminalEngine(8, 2);
        engine.Feed("before");
        var snapshot = engine.CreateSnapshot();

        engine.Feed("\rAFTER");

        Assert.Equal("b", snapshot.Buffer.Lines[0].Cells[0].Text);
        Assert.Equal("A", engine.Buffer.GetCell(0, 0).Text);
    }

    [Fact]
    public void AppliedColorsFollowCellsButDoNotSurviveResetOrEviction()
    {
        var engine = new TerminalEngine(4, 2, historySize: 1);
        engine.Feed("abcd");
        var red = TermColor.FromRgb(255, 0, 0);
        engine.Buffer.ApplyColors(
            new BufferPosition(0, 1),
            new BufferPosition(0, 2),
            red,
            null);

        engine.Resize(2, 3);

        Assert.Equal(red, engine.Buffer.GetCell(1, 0).Attributes.Foreground);
        Assert.Equal(red, engine.Buffer.GetCell(0, 1).Attributes.Foreground);

        engine.Reset();
        engine.Feed("x\r\ny\r\nz\r\nq");

        for (var row = 0; row < engine.Rows; row++)
        {
            Assert.NotEqual(red, engine.Buffer.GetCell(0, row).Attributes.Foreground);
        }
    }

    [Fact]
    public void AlternateBufferHasNoHistoryAndRestoresMainCursor()
    {
        var engine = new TerminalEngine(8, 2);
        engine.Feed("main");
        var cursor = (engine.CursorX, engine.CursorY);

        engine.Feed("\u001b[?1049halt\r\nscreen\r\nscroll\u001b[?1049l");

        Assert.False(engine.AlternateBufferActive);
        Assert.Equal(cursor, (engine.CursorX, engine.CursorY));
        Assert.Equal("main", RowText(engine.Buffer, 0));
        Assert.Equal(0, engine.Buffer.HistoryCount);
    }

    [Fact]
    public void AlternateBufferResizeClipsWithoutReflow()
    {
        var engine = new TerminalEngine(8, 2);
        engine.Feed("\u001b[?1049habcdefgh");

        engine.Resize(4, 3);

        Assert.True(engine.AlternateBufferActive);
        Assert.Equal("abcd", RowText(engine.Buffer, 0));
        Assert.Equal("", RowText(engine.Buffer, 1).TrimEnd());
        Assert.Equal(3, engine.CursorX);
        Assert.Equal(0, engine.CursorY);
    }

    [Fact]
    public void ResizeMapsPendingWrapAfterLastCharacter()
    {
        var engine = new TerminalEngine(6, 3);
        engine.Feed("abcdef");

        engine.Resize(4, 3);
        engine.Feed("X");

        var allText = TerminalBufferExport.ToPlainText(engine.CreateSnapshot(includeHistory: true).Buffer)
            .Replace(Environment.NewLine, string.Empty, StringComparison.Ordinal)
            .TrimEnd();
        Assert.Equal("abcdefX", allText);
    }

    [Fact]
    public void LeftRightMarginsWrapAndHomeInsideTheRegion()
    {
        var engine = new TerminalEngine(10, 3);
        engine.Feed("\u001b[?69h\u001b[3;8s");
        engine.Feed("\u001b[?6h");
        engine.Feed("abcdefg");

        Assert.Equal(2, engine.Buffer.MarginLeft);
        Assert.Equal(7, engine.Buffer.MarginRight);
        Assert.Equal("abcdef", string.Concat(
            Enumerable.Range(2, 6).Select(x => engine.Buffer.GetCell(x, 0).Text)));
        Assert.Equal("g", engine.Buffer.GetCell(2, 1).Text);
        Assert.Equal(3, engine.CursorX);
        Assert.Equal(1, engine.CursorY);
    }

    [Fact]
    public void ShiftLeftMovesLineContentsWithinTheScreen()
    {
        var engine = new TerminalEngine(6, 2);
        engine.Feed("abcdef\u001b[1 @");

        Assert.Equal("bcdef", RowText(engine.Buffer, 0));
        Assert.Equal(' ', (char)engine.Buffer.GetCell(5, 0).Rune.Value);
    }

    [Fact]
    public void ReverseWraparoundMovesBackspaceToPreviousLine()
    {
        var engine = new TerminalEngine(4, 3);
        engine.Feed("abcd\u001b[2;1H\u001b[?45h\b");

        Assert.Equal(3, engine.CursorX);
        Assert.Equal(0, engine.CursorY);
    }

    [Fact]
    public void DecalnFillsTheScreenWithE()
    {
        var engine = new TerminalEngine(3, 2);
        engine.Feed("hi\u001b#8");

        Assert.Equal('E', (char)engine.Buffer.GetCell(0, 0).Rune.Value);
        Assert.Equal('E', (char)engine.Buffer.GetCell(2, 1).Rune.Value);
        Assert.Equal(0, engine.CursorX);
        Assert.Equal(0, engine.CursorY);
    }

    [Fact]
    public void AmbiguousCharactersStayNarrowByDefault()
    {
        WcWidth.AmbiguousAsWide = false;
        Assert.Equal(1, WcWidth.Width(new System.Text.Rune('©')));
        WcWidth.AmbiguousAsWide = true;
        try
        {
            Assert.Equal(2, WcWidth.Width(new System.Text.Rune('©')));
        }
        finally
        {
            WcWidth.AmbiguousAsWide = false;
        }
    }

    private static string RowText(TextBuffer buffer, int row) =>
        string.Concat(buffer.GetRow(row).Where(cell => !cell.IsWideContinuation).Select(cell => cell.Text)).TrimEnd();
}
