using Devolutions.Terminal;
using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Control.Tests;

public sealed class TerminalSearchSessionTests
{
    [Fact]
    public void UpdateFindsMatchesAndSelectsFirst()
    {
        var engine = new TerminalEngine(20, 2);
        engine.Feed("one two one");
        var search = new TerminalSearchSession(engine);

        search.Update("one");

        Assert.Equal(2, search.Matches.Count);
        Assert.Equal(0, search.CurrentIndex);
        Assert.Equal(new BufferPosition(0, 0), search.Current?.Start);
    }

    [Fact]
    public void NavigationWrapsBothDirections()
    {
        var engine = new TerminalEngine(20, 2);
        engine.Feed("one two one");
        var search = new TerminalSearchSession(engine);
        search.Update("one");

        Assert.True(search.MoveNext(reverse: true));
        Assert.Equal(1, search.CurrentIndex);
        Assert.True(search.MoveNext());
        Assert.Equal(0, search.CurrentIndex);
    }

    [Fact]
    public void HistoricalMatchScrollsIntoView()
    {
        var engine = new TerminalEngine(10, 2, historySize: 10);
        engine.Feed("target\r\nsecond\r\nthird\r\nfourth");
        var search = new TerminalSearchSession(engine);

        search.Update("target");

        Assert.True(engine.Buffer.ScrollOffset > 0);
    }

    [Fact]
    public void EmptyQueryClearsResults()
    {
        var engine = new TerminalEngine(20, 2);
        engine.Feed("text");
        var search = new TerminalSearchSession(engine);
        search.Update("text");

        search.Update(" ");

        Assert.Empty(search.Matches);
        Assert.Null(search.Current);
    }

    [Fact]
    public void RaisesChangedForUpdatesNavigationAndClear()
    {
        var engine = new TerminalEngine(20, 2);
        engine.Feed("one one");
        var search = new TerminalSearchSession(engine);
        var changes = 0;
        search.Changed += (_, _) => changes++;

        search.Update("one");
        search.MoveNext();
        search.Clear();

        Assert.Equal(3, changes);
    }

    [Fact]
    public void RefreshRestoresSelectionAndRaisesOneFinalEvent()
    {
        var engine = new TerminalEngine(20, 2);
        engine.Feed("one two one");
        using var search = new TerminalSearchSession(engine);
        search.Update("one");
        search.MoveNext();
        var changes = 0;
        search.Changed += (_, _) => changes++;

        search.Refresh();

        Assert.Equal(1, search.CurrentIndex);
        Assert.Equal(new BufferPosition(0, 8), search.Current?.Start);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void NavigationRefreshesStaleMatches()
    {
        var engine = new TerminalEngine(20, 2);
        engine.Feed("one one");
        using var search = new TerminalSearchSession(engine);
        search.Update("one");
        engine.Reset();

        Assert.False(search.MoveNext());
        Assert.Empty(search.Matches);
        Assert.Null(search.Current);
    }

    [Fact]
    public void RefreshTracksSelectedLineAcrossHistoryEviction()
    {
        var engine = new TerminalEngine(10, 2, historySize: 3);
        engine.Feed("hit0\r\nhit1\r\nhit2\r\nhit3");
        using var search = new TerminalSearchSession(engine);
        search.Update("hit");
        search.MoveNext();
        search.MoveNext();
        Assert.Equal(2, search.CurrentIndex);

        engine.Feed("\r\nhit4");
        search.Refresh();

        var snapshot = engine.CreateSnapshot(includeHistory: true).Buffer;
        var selectedLine = snapshot.Lines[search.Current!.Value.Start.Line];
        var selectedText = string.Concat(selectedLine.Cells.Where(static cell => !cell.IsWideContinuation).Select(static cell => cell.Text));
        Assert.StartsWith("hit2", selectedText, StringComparison.Ordinal);
    }

    [Fact]
    public void RefreshPreservesSelectedMatchAcrossLogicalLineReflow()
    {
        var engine = new TerminalEngine(12, 3, historySize: 10);
        engine.Feed("first needle second needle");
        using var search = new TerminalSearchSession(engine);
        search.Update("needle");
        search.MoveNext();
        var originallySelected = search.Current;

        engine.Resize(6, 5);
        search.Refresh();

        var expected = TextBufferSearch.FindAll(
            engine.CreateSnapshot(includeHistory: true).Buffer,
            "needle");
        Assert.Equal(expected[1], search.Current);
        Assert.NotEqual(originallySelected, search.Current);
        Assert.Equal(1, search.CurrentIndex);
    }

    [Fact]
    public void RefreshSelectsSurvivingNeighborAfterSelectedLineIsEvicted()
    {
        var engine = new TerminalEngine(10, 2, historySize: 2);
        engine.Feed("before\r\nkeep needle\r\nselected needle");
        using var search = new TerminalSearchSession(engine);
        search.Update("needle");
        search.MoveNext();

        engine.Feed("\r\nafter");
        search.Refresh();

        var expected = Assert.Single(TextBufferSearch.FindAll(
            engine.CreateSnapshot(includeHistory: true).Buffer,
            "needle"));
        Assert.Equal(expected, search.Current);
        Assert.Equal(0, search.CurrentIndex);
    }

    [Fact]
    public void RefreshKeepsSelectedDuplicateAfterHistoryShifts()
    {
        var engine = new TerminalEngine(12, 2, historySize: 4);
        engine.Feed("needle\r\nneedle\r\nneedle");
        using var search = new TerminalSearchSession(engine);
        search.Update("needle");
        search.MoveNext();
        var selectedId = engine.CreateSnapshot(includeHistory: true)
            .Buffer.Lines[search.Current!.Value.Start.Line].LogicalLineId;

        engine.Feed("\r\nneedle");
        search.Refresh();

        var snapshot = engine.CreateSnapshot(includeHistory: true).Buffer;
        Assert.Equal(selectedId, snapshot.Lines[search.Current!.Value.Start.Line].LogicalLineId);
        Assert.Equal(1, search.CurrentIndex);
    }

    [Fact]
    public void RefreshKeepsSelectedDuplicateAfterEvictingEarlierHistory()
    {
        var engine = new TerminalEngine(12, 2, historySize: 2);
        engine.Feed("needle\r\nneedle\r\nneedle\r\nneedle");
        using var search = new TerminalSearchSession(engine);
        search.Update("needle");
        search.MoveNext();
        var selectedId = engine.CreateSnapshot(includeHistory: true)
            .Buffer.Lines[search.Current!.Value.Start.Line].LogicalLineId;

        engine.Feed("\r\nneedle");
        search.Refresh();

        var snapshot = engine.CreateSnapshot(includeHistory: true).Buffer;
        Assert.Equal(selectedId, snapshot.Lines[search.Current!.Value.Start.Line].LogicalLineId);
        Assert.Equal(0, search.CurrentIndex);
    }

    [Fact]
    public void RefreshKeepsSelectedOccurrenceAfterReflowOfIdenticalMatches()
    {
        var engine = new TerminalEngine(20, 2, historySize: 10);
        engine.Feed("needle needle needle");
        using var search = new TerminalSearchSession(engine);
        search.Update("needle");
        search.MoveNext();
        var before = engine.CreateSnapshot(includeHistory: true).Buffer;
        var selected = search.Current!.Value;
        var line = before.Lines[selected.Start.Line];
        var selectedId = line.LogicalLineId;
        var selectedOffset = line.LogicalOffset + selected.Start.Column;

        engine.Resize(8, 4);
        search.Refresh();

        var after = engine.CreateSnapshot(includeHistory: true).Buffer;
        var current = search.Current!.Value;
        Assert.Equal(selectedId, after.Lines[current.Start.Line].LogicalLineId);
        Assert.Equal(selectedOffset, after.Lines[current.Start.Line].LogicalOffset + current.Start.Column);
        Assert.Equal(1, search.CurrentIndex);
    }

    [Fact]
    public void RefreshKeepsSelectionAfterViewportScroll()
    {
        var engine = new TerminalEngine(12, 2, historySize: 5);
        engine.Feed("needle\r\nother\r\nneedle\r\nother");
        using var search = new TerminalSearchSession(engine);
        search.Update("needle");
        search.MoveNext();
        var selected = search.Current;

        engine.SetScrollOffset(engine.HistoryCount);
        search.Refresh();

        Assert.Equal(selected, search.Current);
        Assert.Equal(1, search.CurrentIndex);
    }

    [Fact]
    public void RefreshSelectsNextSurvivingMatchWhenSelectedLineIsEvicted()
    {
        var engine = new TerminalEngine(12, 2, historySize: 2);
        engine.Feed("needle\r\nneedle\r\nneedle\r\nneedle");
        using var search = new TerminalSearchSession(engine);
        search.Update("needle");
        var before = engine.CreateSnapshot(includeHistory: true).Buffer;
        var selectedId = before.Lines[search.Current!.Value.Start.Line].LogicalLineId;

        engine.Feed("\r\nneedle");
        search.Refresh();

        var after = engine.CreateSnapshot(includeHistory: true).Buffer;
        Assert.DoesNotContain(after.Lines, line => line.LogicalLineId == selectedId);
        Assert.Equal(0, search.CurrentIndex);
        Assert.Equal(0, search.Current!.Value.Start.Line);
    }

    [Fact]
    public void RefreshDoesNotReuseMainBufferAnchorInAlternateBuffer()
    {
        var engine = new TerminalEngine(20, 2);
        engine.Feed("needle needle");
        using var search = new TerminalSearchSession(engine);
        search.Update("needle");
        search.MoveNext();
        Assert.Equal(1, search.CurrentIndex);

        engine.Feed("\u001b[?1049hneedle needle");
        search.Refresh();

        Assert.True(engine.AlternateBufferActive);
        Assert.Equal(0, search.CurrentIndex);
        Assert.Equal(new BufferPosition(0, 0), search.Current?.Start);
    }
}
