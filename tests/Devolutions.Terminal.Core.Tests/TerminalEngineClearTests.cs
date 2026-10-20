using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Core.Tests;

public sealed class TerminalEngineClearTests
{
    [Fact]
    public void ClearBufferKeepsCursorRowAtTopAndErasesEverythingElse()
    {
        var engine = new TerminalEngine(20, 5);
        for (var i = 0; i < 8; i++)
        {
            engine.Feed($"line{i}\r\n");
        }

        engine.Feed("PS> ab\r\nbelow\u001b[A\u001b[7G");
        Assert.True(engine.HistoryCount > 0);
        Assert.Equal((6, 3), (engine.CursorX, engine.CursorY));

        engine.ClearBufferKeepingCursorRow();

        Assert.Equal(0, engine.HistoryCount);
        Assert.Equal((6, 0), (engine.CursorX, engine.CursorY));
        Assert.Equal("PS> ab", RowText(engine, 0));
        for (var row = 1; row < engine.Rows; row++)
        {
            Assert.Equal(string.Empty, RowText(engine, row));
        }
    }

    [Fact]
    public void ClearBufferOnTopRowKeepsThatRow()
    {
        var engine = new TerminalEngine(20, 5);
        engine.Feed("PS> \r\nstale\r\nstale\u001b[H\u001b[5G");

        engine.ClearBufferKeepingCursorRow();

        Assert.Equal((4, 0), (engine.CursorX, engine.CursorY));
        Assert.Equal("PS>", RowText(engine, 0));
        Assert.Equal(string.Empty, RowText(engine, 1));
        Assert.Equal(string.Empty, RowText(engine, 2));
    }

    [Fact]
    public void ClearBufferOnBottomRowKeepsThatRow()
    {
        var engine = new TerminalEngine(20, 3);
        engine.Feed("one\r\ntwo\r\nPS> x");

        engine.ClearBufferKeepingCursorRow();

        Assert.Equal((5, 0), (engine.CursorX, engine.CursorY));
        Assert.Equal("PS> x", RowText(engine, 0));
        Assert.Equal(string.Empty, RowText(engine, 1));
        Assert.Equal(string.Empty, RowText(engine, 2));
    }

    [Fact]
    public void AbsoluteCursorMoveAfterClearLandsOnKeptPromptRow()
    {
        // ConPTY clears its own buffer the same way, so the shell's next CUP to
        // row 1 targets the prompt that now sits on the top row.
        var engine = new TerminalEngine(20, 5);
        engine.Feed("banner\r\nPS> ");

        engine.ClearBufferKeepingCursorRow();
        engine.Feed("\u001b[1;5Hdir");

        Assert.Equal("PS> dir", RowText(engine, 0));
    }

    private static string RowText(TerminalEngine engine, int row) =>
        string.Concat(engine.Buffer.GetRow(row)
            .Where(static cell => !cell.IsWideContinuation)
            .Select(static cell => cell.Text)).TrimEnd();
}
