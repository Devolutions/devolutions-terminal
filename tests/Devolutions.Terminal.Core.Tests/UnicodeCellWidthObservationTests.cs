using System.Text;
using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Core.Tests;

// WcWidth.AmbiguousAsWide is process-wide; other Core tests temporarily change it.
[CollectionDefinition("Unicode cell width observations", DisableParallelization = true)]
public sealed class UnicodeCellWidthObservationCollection;

[Collection("Unicode cell width observations")]
public sealed class UnicodeCellWidthObservationTests
{
    [Fact]
    public void SinglePrintableRuneReplacesInitialBlankWithoutAddingAnotherCell()
    {
        var buffer = NewBuffer(6);
        Assert.Equal(" ", buffer.GetCell(0, 0).Text);
        Assert.Equal(0, buffer.CursorX);

        buffer.Print(new Rune('Q'));

        Assert.Equal("Q", buffer.GetCell(0, 0).Text);
        Assert.Equal(1, buffer.GetCell(0, 0).DisplayWidth);
        Assert.False(buffer.GetCell(0, 0).IsWideContinuation);
        Assert.Equal(" ", buffer.GetCell(1, 0).Text);
        Assert.Equal(1, buffer.CursorX);
        Assert.False(buffer.WrapPending);
    }

    [Fact]
    public void ExtendKeepsOriginalScalarOrderInBaseCellWithoutAdvancing()
    {
        var buffer = NewBuffer(6);
        Print(buffer, "e\u0301X");

        Assert.Equal("e\u0301", buffer.GetCell(0, 0).Text);
        Assert.Equal(1, buffer.GetCell(0, 0).DisplayWidth);
        Assert.False(buffer.GetCell(1, 0).IsWideContinuation);
        Assert.Equal("X", buffer.GetCell(1, 0).Text);
        Assert.Equal(" ", buffer.GetCell(2, 0).Text);
        Assert.Equal(2, buffer.CursorX);
    }

    [Fact]
    public void WideRuneReservesOneEmptyTextContinuationBeforeNextGlyph()
    {
        var buffer = NewBuffer(6);
        Print(buffer, "🚀X");

        Assert.Equal("🚀", buffer.GetCell(0, 0).Text);
        Assert.Equal(2, buffer.GetCell(0, 0).DisplayWidth);
        Assert.Equal("", buffer.GetCell(1, 0).Text);
        Assert.True(buffer.GetCell(1, 0).IsWideContinuation);
        Assert.Equal(0, buffer.GetCell(1, 0).DisplayWidth);
        Assert.Equal("X", buffer.GetCell(2, 0).Text);
        Assert.False(buffer.GetCell(2, 0).IsWideContinuation);
        Assert.Equal(3, buffer.CursorX);
    }

    [Fact]
    public void LeadingAndTrailingSpacesDoNotHideAdjacentEqualClusters()
    {
        var buffer = NewBuffer(8);
        Print(buffer, " A\u0301A\u0301 ");

        Assert.Equal(" ", buffer.GetCell(0, 0).Text);
        Assert.Equal("A\u0301", buffer.GetCell(1, 0).Text);
        Assert.Equal("A\u0301", buffer.GetCell(2, 0).Text);
        Assert.False(buffer.GetCell(2, 0).IsWideContinuation);
        Assert.Equal(" ", buffer.GetCell(3, 0).Text);
        Assert.Equal(" ", buffer.GetCell(4, 0).Text);
        Assert.Equal(4, buffer.CursorX);
        Assert.False(buffer.WrapPending);
    }

    [Theory]
    [InlineData(0x41, 1, "A")]
    [InlineData(0x0301, 0, "A\u0301")]
    [InlineData(0x200D, 0, "A\u200D")]
    [InlineData(0x1F1E6, 2, "🇦")]
    [InlineData(0x1F680, 2, "🚀")]
    [InlineData(0x2301, 1, "⌁")]
    public void WidthTableMatchesActualCellAdvance(int scalar, int width, string expectedCell)
    {
        var rune = new Rune(scalar);
        Assert.Equal(width, WcWidth.Width(rune));

        var buffer = NewBuffer(8);
        if (width == 0)
        {
            buffer.Print(new Rune('A'));
        }

        buffer.Print(rune);

        Assert.Equal(expectedCell, buffer.GetCell(0, 0).Text);
        Assert.Equal(Math.Max(1, width), buffer.GetCell(0, 0).DisplayWidth);
        Assert.Equal(Math.Max(1, width), buffer.CursorX);
        Assert.Equal(width == 2, buffer.GetCell(1, 0).IsWideContinuation);
        Assert.Equal(width == 2 ? "" : " ", buffer.GetCell(1, 0).Text);
    }

    [Theory]
    [InlineData("\u2764\uFE0F", 2)]
    [InlineData("1\uFE0F\u20E3", 1)]
    public void VariationSelectorAndKeycapKeepTheirTextButUseTerminalWidthPolicy(string cluster, int width)
    {
        var buffer = NewBuffer(6);
        Print(buffer, cluster + "X");

        Assert.Equal(cluster, buffer.GetCell(0, 0).Text);
        Assert.Equal(width, buffer.GetCell(0, 0).DisplayWidth);
        Assert.Equal(width == 2, buffer.GetCell(1, 0).IsWideContinuation);
        Assert.Equal(width == 2 ? "" : "X", buffer.GetCell(1, 0).Text);
        Assert.Equal("X", buffer.GetCell(width, 0).Text);
        Assert.Equal(width + 1, buffer.CursorX);
    }

    [Theory]
    [InlineData("AB", 0, 2, 3)]
    [InlineData("ABC", 1, 0, 2)]
    public void WideRuneExactFitOrOneColumnShortWrapsWithoutSplitting(
        string prefix, int glyphRow, int glyphColumn, int cursorColumn)
    {
        var buffer = NewBuffer(4);
        Print(buffer, prefix);
        buffer.Print(new Rune(0x1F680));

        Assert.Equal(prefix[0].ToString(), buffer.GetCell(0, 0).Text);
        Assert.Equal(prefix[1].ToString(), buffer.GetCell(1, 0).Text);
        Assert.Equal("🚀", buffer.GetCell(glyphColumn, glyphRow).Text);
        Assert.Equal(2, buffer.GetCell(glyphColumn, glyphRow).DisplayWidth);
        Assert.Equal("", buffer.GetCell(glyphColumn + 1, glyphRow).Text);
        Assert.True(buffer.GetCell(glyphColumn + 1, glyphRow).IsWideContinuation);
        Assert.Equal(cursorColumn, buffer.CursorX);
        Assert.Equal(glyphRow, buffer.CursorY);
        Assert.Equal(glyphRow == 0, buffer.WrapPending);
        Assert.Equal(glyphRow == 0 ? "🚀" : "C", buffer.GetCell(2, 0).Text);
    }

    private static TextBuffer NewBuffer(int columns) =>
        new(columns, rows: 2, historySize: 0, hasHistory: false);

    private static void Print(TextBuffer buffer, string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            buffer.Print(rune);
        }
    }
}
