using Devolutions.Terminal;
using Devolutions.Terminal.Render;
using Xunit;

namespace Devolutions.Terminal.Control.Tests;

public sealed class RendererIntegrationTests
{
    [Theory]
    [InlineData("bar", TerminalCursorStyle.Bar)]
    [InlineData("underscore", TerminalCursorStyle.Underscore)]
    [InlineData("doubleUnderscore", TerminalCursorStyle.DoubleUnderscore)]
    [InlineData("vintage", TerminalCursorStyle.Vintage)]
    [InlineData("filledBox", TerminalCursorStyle.FilledBox)]
    [InlineData("emptyBox", TerminalCursorStyle.EmptyBox)]
    public void MapsEveryWindowsTerminalCursorShape(string value, TerminalCursorStyle expected)
    {
        Assert.Equal(expected, TermControl.ParseCursorStyle(value));
    }

    [Theory]
    [InlineData(0, "underscore", TerminalCursorStyle.Underscore)]
    [InlineData(1, "bar", TerminalCursorStyle.FilledBox)]
    [InlineData(4, "bar", TerminalCursorStyle.Underscore)]
    [InlineData(6, "filledBox", TerminalCursorStyle.Bar)]
    public void VtCursorStyleOverridesProfile(
        int decscusr,
        string profile,
        TerminalCursorStyle expected)
    {
        Assert.Equal(expected, TermControl.ResolveCursorStyle(decscusr, profile));
    }

    [Theory]
    [InlineData("8", 8, 8, 8, 8)]
    [InlineData("0", 0, 0, 0, 0)]
    [InlineData("8, 16", 8, 16, 8, 16)]
    [InlineData("1, 2, 3, 4", 1, 2, 3, 4)]
    public void ParsesProfilePadding(
        string value,
        double left,
        double top,
        double right,
        double bottom)
    {
        var padding = TermControl.ParsePadding(value);
        Assert.Equal(left, padding.Left);
        Assert.Equal(top, padding.Top);
        Assert.Equal(right, padding.Right);
        Assert.Equal(bottom, padding.Bottom);
    }
}
