using System.Text;
using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Core.Tests;

[Collection("Unicode cell width observations")]
public sealed class GraphemeBoundaryRegressionTests
{
    [Theory]
    [InlineData("\U0001F1E6\u0308\U0001F1E7", "\U0001F1E6\u0308", "\U0001F1E7", 4, 2, 2)]
    [InlineData("A\u094D\u0915", "A\u094D", "\u0915", 2, 1, 1)]
    [InlineData("\u2301\u200D\u2301", "\u2301\u200D", "\u2301", 2, 1, 1)]
    [InlineData("\u0905\u094D\u0915", "\u0905\u094D", "\u0915", 2, 1, 1)]
    [InlineData("\U0001F100\u200D\U0001F100", "\U0001F100\u200D", "\U0001F100", 2, 1, 1)]
    [InlineData("\u2764\u093E\u200D\u2764", "\u2764\u093E\u200D", "\u2764", 2, 1, 1)]
    [InlineData("\u0915\u094D\u093E\u0915", "\u0915\u094D\u093E", "\u0915", 2, 1, 1)]
    public void NonJoiningPropertiesKeepDistinctCells(
        string text, string first, string second, int cursor, int firstWidth, int lastAdvance)
    {
        var buffer = new TextBuffer(20, 2, 0, false);
        var runes = text.EnumerateRunes().ToArray();
        foreach (var rune in runes[..^1])
        {
            buffer.Print(rune);
        }

        var advance = buffer.GetPrintAdvance(runes[^1]);
        buffer.Print(runes[^1]);

        Assert.Equal(first, buffer.GetCell(0, 0).Text);
        Assert.Equal(firstWidth, buffer.GetCell(0, 0).DisplayWidth);
        Assert.Equal(second, buffer.GetCell(cursor == 4 ? 2 : 1, 0).Text);
        Assert.Equal(cursor, buffer.CursorX);
        Assert.Equal(lastAdvance, advance);
        Assert.False(buffer.WrapPending);
    }

    [Theory]
    [InlineData("\U0001F1E6\U0001F1E7", "\U0001F1E6\U0001F1E7", 2)]
    [InlineData("\u0915\u094D\u0915", "\u0915\u094D\u0915", 1)]
    [InlineData("\U0001F469\u0308\u200D\U0001F4BB", "\U0001F469\u0308\u200D\U0001F4BB", 2)]
    [InlineData("\u0995\u09CD\u09BE\u0995", "\u0995\u09CD\u09BE\u0995", 1)]
    public void PositiveJoinPropertiesKeepOneCell(string text, string expected, int width)
    {
        var buffer = new TextBuffer(20, 2, 0, false);
        foreach (var rune in text.EnumerateRunes())
        {
            buffer.Print(rune);
        }

        Assert.Equal(expected, buffer.GetCell(0, 0).Text);
        Assert.Equal(width, buffer.GetCell(0, 0).DisplayWidth);
        Assert.Equal(width == 2, buffer.GetCell(1, 0).IsWideContinuation);
        Assert.Equal(width, buffer.CursorX);
    }

    [Fact]
    public void EmptyBufferHasNoClustersAndFirstPrintHasOneCellOfAdvance()
    {
        var buffer = new TextBuffer(20, 2, 0, false);
        Assert.Equal(" ", buffer.GetCell(0, 0).Text);
        Assert.Equal(0, buffer.CursorX);
        Assert.Equal(1, buffer.GetPrintAdvance(new Rune('x')));

        buffer.Print(new Rune('x'));

        Assert.Equal("x", buffer.GetCell(0, 0).Text);
        Assert.Equal(" ", buffer.GetCell(1, 0).Text);
        Assert.Equal(1, buffer.CursorX);
        Assert.False(buffer.WrapPending);
    }

    [Fact]
    public void PrependAppliesToOneClusterButNotTheFollowingBase()
    {
        var buffer = new TextBuffer(8, 2, 0, false);
        foreach (var rune in "\u0600A\u0903".EnumerateRunes())
        {
            buffer.Print(rune);
        }

        Assert.Equal(1, buffer.GetPrintAdvance(new Rune('X')));
        buffer.Print(new Rune('X'));

        Assert.Equal("\u0600A\u0903", buffer.GetCell(0, 0).Text);
        Assert.Equal("X", buffer.GetCell(1, 0).Text);
        Assert.Equal(2, buffer.CursorX);
        Assert.False(buffer.WrapPending);
    }

    [Theory]
    [InlineData("e", 0x0308, 0, "e\u0308", 1)]
    [InlineData("\U0001F1E6", 0x1F1E7, 0, "\U0001F1E6\U0001F1E7", 2)]
    [InlineData("\u0915\u094D", 0x0915, 0, "\u0915\u094D\u0915", 1)]
    [InlineData("\u2764", 0xFE0F, 1, "\u2764\uFE0F", 2)]
    public void GetPrintAdvanceAgreesWithJoinedCellWidthAndCursor(
        string prefix, int scalar, int advance, string expected, int width)
    {
        var buffer = new TextBuffer(20, 2, 0, false);
        foreach (var rune in prefix.EnumerateRunes())
        {
            buffer.Print(rune);
        }

        Assert.Equal(advance, buffer.GetPrintAdvance(new Rune(scalar)));
        buffer.Print(new Rune(scalar));

        Assert.Equal(expected, buffer.GetCell(0, 0).Text);
        Assert.Equal(width, buffer.GetCell(0, 0).DisplayWidth);
        Assert.Equal(width == 2, buffer.GetCell(1, 0).IsWideContinuation);
        Assert.Equal(width, buffer.CursorX);
        Assert.False(buffer.WrapPending);
    }
}
