using System.Text;
using Devolutions.Terminal.Core;
using Devolutions.Terminal.Render;
using Xunit;

namespace Devolutions.Terminal.Render.Tests;

public sealed class TerminalRenderPlannerTests
{
    [Fact]
    public void GroupsAdjacentCellsWithSamePaint()
    {
        var engine = new TerminalEngine(8, 2);
        engine.Feed("ab\u001b[31mcd");

        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);

        Assert.Equal(3, frame.RowsData[0].Runs.Count);
        Assert.Equal("ab", frame.RowsData[0].Runs[0].Text);
        Assert.Equal("cd", frame.RowsData[0].Runs[1].Text);
        Assert.Equal(2, frame.RowsData[0].Runs[1].CellCount);
    }

    [Fact]
    public void PresizesRunsWithoutChangingClusterOffsetsAcrossStylesAndWideCells()
    {
        var engine = new TerminalEngine(12, 2);
        engine.Feed("a\u0301\u754c\u001b[31mb\U0001F600\u001b[0mc");

        var runs = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme).RowsData[0].Runs;

        Assert.Equal("a\u0301\u754c", runs[0].Text[..3]);
        Assert.Equal(new TerminalTextCluster(0, 2, 0, 1), runs[0].Clusters[0]);
        Assert.Equal(new TerminalTextCluster(2, 1, 1, 2), runs[0].Clusters[1]);
        Assert.Equal("b\U0001F600", runs[1].Text);
        Assert.Equal(new TerminalTextCluster(1, 2, 4, 2), runs[1].Clusters[1]);
        Assert.Equal(6, runs[2].StartColumn);
    }

    [Fact]
    public void GrowsRowTextBufferForCombiningCharactersWithoutChangingClusters()
    {
        const int columns = 80;
        var engine = new TerminalEngine(columns, 2);
        engine.Feed(string.Concat(Enumerable.Repeat("e\u0301", columns)));

        var run = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme).RowsData[0].Runs[0];

        Assert.Equal(string.Concat(Enumerable.Repeat("e\u0301", columns)), run.Text);
        Assert.Equal(columns, run.Clusters.Count);
        Assert.Equal(new TerminalTextCluster(158, 2, 79, 1), run.Clusters[^1]);
    }

    [Fact]
    public void PlainAsciiClustersKeepOffsetsAcrossStyleRuns()
    {
        var engine = new TerminalEngine(8, 1);
        engine.Feed("ab\u001b[31mcd");

        var runs = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme).RowsData[0].Runs;

        Assert.Equal(
            [new TerminalTextCluster(0, 1, 0, 1), new TerminalTextCluster(1, 1, 1, 1)],
            runs[0].Clusters.ToArray());
        Assert.Equal(
            [new TerminalTextCluster(0, 1, 2, 1), new TerminalTextCluster(1, 1, 3, 1)],
            runs[1].Clusters.ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = runs[0].Clusters[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = runs[0].Clusters[2]);
    }

    [Fact]
    public void PlansRowsWiderThanStackBuffers()
    {
        const int columns = 300;
        var engine = new TerminalEngine(columns, 2);
        engine.Feed(new string('x', columns - 1));

        var run = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme).RowsData[0].Runs[0];

        Assert.Equal(new string('x', columns - 1) + " ", run.Text);
        Assert.Equal(columns, run.Clusters.Count);
        Assert.Equal(new TerminalTextCluster(columns - 1, 1, columns - 1, 1), run.Clusters[^1]);
    }

    [Fact]
    public void ResolvesInverseAndFaintAttributes()
    {
        var attributes = CellAttributes.Default;
        attributes.Foreground = TermColor.FromIndex(1);
        attributes.Background = TermColor.FromIndex(2);
        attributes.Flags = CellFlags.Inverse | CellFlags.Faint;

        var resolved = TerminalRenderPlanner.Resolve(
            attributes,
            null,
            ColorScheme.Campbell,
            reverseScreen: false);

        Assert.Equal(ColorScheme.Campbell.Resolve(2), resolved.Foreground);
        Assert.Equal(0xFF62070Fu, resolved.Background);
    }

    [Fact]
    public void PreservesCombiningTextAndWideCellCount()
    {
        var engine = new TerminalEngine(8, 2);
        engine.Feed("e\u0301界");

        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);
        var run = frame.RowsData[0].Runs[0];

        Assert.StartsWith("e\u0301界", run.Text, StringComparison.Ordinal);
        Assert.Equal(8, run.CellCount);
        Assert.True(run.Clusters.Count >= 2);
        Assert.Equal(0, run.Clusters[0].StartColumn);
        Assert.Equal(1, run.Clusters[0].CellCount);
        Assert.Equal(1, run.Clusters[1].StartColumn);
        Assert.Equal(2, run.Clusters[1].CellCount);
    }

    [Fact]
    public void KeepsEmojiJoinerSequenceInOneShapingCluster()
    {
        var engine = new TerminalEngine(8, 2);
        engine.Feed("\U0001F469\u200D\U0001F4BB");

        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);
        var clusters = frame.RowsData[0].Runs[0].Clusters;

        Assert.Equal("\U0001F469\u200D\U0001F4BB", frame.RowsData[0].Runs[0].Text[..5]);
        Assert.Equal(1, clusters.Count(cluster => cluster.TextLength > 1));
        Assert.Equal(2, clusters[0].CellCount);
    }

    [Fact]
    public void KeepsContextualScriptRunInOneShapingCluster()
    {
        var engine = new TerminalEngine(8, 2);
        engine.Feed("سلام");

        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);
        var clusters = frame.RowsData[0].Runs[0].Clusters;

        var contextual = Assert.Single(clusters, cluster => cluster.TextLength > 1);
        Assert.Equal("سلام".Length, contextual.TextLength);
        Assert.Equal(4, contextual.CellCount);
    }

    [Fact]
    public void TracksHyperlinkBoundaries()
    {
        var engine = new TerminalEngine(12, 2);
        engine.Feed("\u001b]8;;https://example.com\u0007link\u001b]8;;\u0007 plain");

        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);

        Assert.Equal("https://example.com", frame.RowsData[0].Runs[0].Attributes.HyperlinkUri);
        Assert.Null(frame.RowsData[0].Runs[1].Attributes.HyperlinkUri);
    }

    [Fact]
    public void AppliesScreenReverseVideo()
    {
        var engine = new TerminalEngine(4, 1);
        engine.Feed("\u001b[?5hA");

        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);

        Assert.Equal(engine.Scheme.Foreground, frame.Background);
        Assert.Equal(engine.Scheme.Background, frame.RowsData[0].Runs[0].Attributes.Foreground);
        Assert.Equal(engine.Scheme.Foreground, frame.RowsData[0].Runs[0].Attributes.Background);
    }

    [Fact]
    public void CursorTracksScrolledViewportAndHidesOutsideFrame()
    {
        var engine = new TerminalEngine(4, 2);
        engine.Feed("one\r\ntwo\r\nthree");
        engine.Buffer.ScrollOffset = 1;

        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);

        Assert.Equal(engine.CursorY + 1, frame.CursorY);
        Assert.False(frame.CursorVisible);
    }

    [Fact]
    public void PlansDoubleWidthAndDoubleHeightRowGeometry()
    {
        var engine = new TerminalEngine(10, 3);
        engine.Feed("\u001b#6wide");
        engine.Feed("\u001b[2;1H\u001b#3top");
        engine.Feed("\u001b[3;1H\u001b#4botto");

        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);

        Assert.Equal(LineRendition.DoubleWidth, frame.RowsData[0].Rendition);
        Assert.Equal(LineRendition.DoubleHeightTop, frame.RowsData[1].Rendition);
        Assert.Equal(LineRendition.DoubleHeightBottom, frame.RowsData[2].Rendition);
        Assert.Equal(4, frame.CursorX);
    }

    [Fact]
    public void CarriesDownloadedGlyphMasksIntoRenderFrame()
    {
        var engine = new TerminalEngine();
        engine.Feed("\u001bP0;1;0;2;1;2;6;0{ B~\u001b\\\u001b( B!");

        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);

        Assert.Single(frame.DrcsGlyphs);
        Assert.Equal(new Rune(0xEF21), frame.DrcsGlyphs.Single().Value.PrivateUseRune);
    }
}
