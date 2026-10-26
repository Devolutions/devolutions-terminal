using System.Text;
using Devolutions.Terminal.Core;
using Devolutions.Terminal.Ghostty;
using Xunit;

namespace Devolutions.Terminal.Ghostty.Tests;

/// <summary>
/// Feeding only marks the projection stale, and a later read re-projects just
/// the rows Ghostty's render state reports dirty. Reading after every chunk
/// exercises that incremental path; scrolling the viewport to the bottom then
/// forces a full re-projection, and both must describe the same screen.
/// </summary>
public sealed class GhosttyProjectionTests
{
    private const string Esc = "\u001b";

    public static TheoryData<string, string> Streams => new()
    {
        { "partial rows", $"one\r\ntwo\r\nthree{Esc}[2;1Hchanged{Esc}[5;3Hx" },
        { "scrolling", string.Concat(Enumerable.Range(0, 30).Select(i => $"line {i:D2} {new string('#', i % 15)}\r\n")) },
        { "region scroll", $"{Esc}[2;5r{Esc}[5;1H" + string.Concat(Enumerable.Range(0, 12).Select(i => $"r{i}\r\n")) + $"{Esc}[r" },
        { "styled wide", $"{Esc}[1;31mred{Esc}[0m \u4E00\u4E00 {Esc}[44mblue{Esc}[0m e\u0301" },
        { "hyperlink and prompt", $"{Esc}]133;A\u0007PS> {Esc}]133;B\u0007{Esc}]8;;https://example.test\u0007link{Esc}]8;;\u0007\r\n" },
        { "alternate screen", $"primary\r\n{Esc}[?1049h{Esc}[Halt{Esc}[3;3Hz{Esc}[?1049lback" },
        { "erase", $"abcdef\r\nghijkl{Esc}[1;3H{Esc}[K{Esc}[2;2H{Esc}[1P" },
    };

    [Theory]
    [MemberData(nameof(Streams))]
    public void IncrementalProjectionMatchesFullProjection(string name, string stream)
    {
        var bytes = Encoding.UTF8.GetBytes(stream);
        using var engine = new GhosttyTerminalEngine(16, 6, historySize: 20);
        var random = new Random(name.Length);
        for (var offset = 0; offset < bytes.Length;)
        {
            var length = Math.Min(bytes.Length - offset, random.Next(1, 9));
            engine.Feed(bytes.AsSpan(offset, length));
            offset += length;
            _ = engine.CreateSnapshot();
        }

        var incremental = engine.CreateSnapshot();
        engine.SetScrollOffset(0);
        var full = engine.CreateSnapshot();

        AssertSameScreen(name, full, incremental);
    }

    [Fact]
    public void FeedDefersProjectionUntilRead()
    {
        using var reader = new GhosttyTerminalEngine(20, 4);
        using var writer = new GhosttyTerminalEngine(20, 4);
        foreach (var line in new[] { "first\r\n", "second\r\n", "third" })
        {
            reader.Feed(line);
            _ = reader.CreateSnapshot();
            writer.Feed(line);
        }

        // The cursor and grid accessors project on demand.
        Assert.Equal((reader.CursorX, reader.CursorY), (writer.CursorX, writer.CursorY));
        AssertSameScreen("deferred", reader.CreateSnapshot(), writer.CreateSnapshot());
        Assert.Equal("third", writer.Buffer.GetText(0, 2, 19, 2).TrimEnd());
    }

    private static void AssertSameScreen(string name, TerminalSnapshot expected, TerminalSnapshot actual)
    {
        Assert.True(
            (expected.Buffer.CursorX, expected.Buffer.CursorY) == (actual.Buffer.CursorX, actual.Buffer.CursorY),
            $"{name}: cursor");
        Assert.Equal(expected.Buffer.Lines.Count, actual.Buffer.Lines.Count);
        for (var row = 0; row < expected.Buffer.Lines.Count; row++)
        {
            var left = expected.Buffer.Lines[row];
            var right = actual.Buffer.Lines[row];
            Assert.True(left.Wrapped == right.Wrapped, $"{name}: row {row} wrapped");
            Assert.True(
                left.Marks.Select(static mark => mark.StartColumn).SequenceEqual(right.Marks.Select(static mark => mark.StartColumn)),
                $"{name}: row {row} marks");
            for (var column = 0; column < left.Cells.Count; column++)
            {
                Assert.True(
                    left.Cells[column].Equals(right.Cells[column]),
                    $"{name}: row {row} column {column}: '{left.Cells[column].Text}' vs '{right.Cells[column].Text}'");
            }
        }
    }
}
