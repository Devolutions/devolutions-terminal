using System.Text;
using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Core.Tests;

/// <summary>
/// The buffer stores lines as packed, reference-free cells with combining text
/// and hyperlinks in per-line side arrays. Whatever goes in through the public
/// <see cref="Cell"/> must come back out unchanged, including colors that the
/// <see cref="TermColor"/> factories would never produce.
/// </summary>
public sealed class PackedCellStorageTests
{
    [Fact]
    public void ArbitraryCellsRoundTripThroughBufferStorage()
    {
        const int columns = 40;
        const int rows = 6;
        var random = new Random(5150);
        var buffer = new TextBuffer(columns, rows, 4, hasHistory: true);
        var expected = new Cell[rows][];
        var projection = new List<TextBufferProjectionRow>();
        for (var row = 0; row < rows; row++)
        {
            expected[row] = new Cell[columns];
            for (var column = 0; column < columns; column++)
            {
                expected[row][column] = RandomCell(random);
            }

            projection.Add(new TextBufferProjectionRow((Cell[])expected[row].Clone(), random.Next(2) == 0, []));
        }

        buffer.ReplaceViewport(projection, 3, 2);
        var snapshot = buffer.CreateSnapshot();

        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                Assert.Equal(expected[row][column], snapshot.Lines[row].Cells[column]);
                Assert.Equal(expected[row][column], buffer.GetCell(column, row));
            }
        }
    }

    [Fact]
    public void GetRowReturnsACopy()
    {
        var buffer = new TextBuffer(4, 2, 0, hasHistory: false);
        buffer.Print(new Rune('a'));

        var row = buffer.GetRow(0);
        row[0].Rune = new Rune('z');

        Assert.Equal(new Rune('a'), buffer.GetCell(0, 0).Rune);
    }

    [Fact]
    public void RecycledLinesDropCombiningTextAndHyperlinks()
    {
        using var engine = new TerminalEngine(8, 2, historySize: 1);
        engine.Feed("\u001b]8;;https://example.test\u0007e\u0301x\u001b]8;;\u0007\r\n");
        engine.Feed("second\r\nthird\r\nfourth\r\nfifth");

        // Every line has been recycled since the hyperlinked, combining first line.
        var snapshot = engine.CreateSnapshot(includeHistory: true);
        foreach (var line in snapshot.Buffer.Lines)
        {
            Assert.All(line.Cells, static cell =>
            {
                Assert.Null(cell.HyperlinkUri);
                Assert.Null(cell.CombiningCharacters);
            });
        }

        Assert.Equal("fifth", string.Concat(snapshot.Buffer.Lines[^1].Cells.Select(static cell => cell.Text)).TrimEnd());
    }

    private static Cell RandomCell(Random random)
    {
        static TermColor Color(Random random) => new(
            (ColorKind)random.Next(3),
            (byte)random.Next(256),
            (byte)random.Next(256),
            (byte)random.Next(256),
            (byte)random.Next(256));

        return new Cell
        {
            Rune = random.Next(4) switch
            {
                0 => new Rune(random.Next(0x20, 0x7F)),
                1 => new Rune(random.Next(0x4E00, 0x9FFF)),
                2 => new Rune(random.Next(0x1F300, 0x1FAFF)),
                _ => new Rune(random.Next(0x100000, 0x10FFFE)),
            },
            Attributes = new CellAttributes
            {
                Foreground = Color(random),
                Background = Color(random),
                Flags = (CellFlags)random.Next(0, 0x10000),
            },
            IsProtected = random.Next(2) == 0,
            IsWideContinuation = random.Next(4) == 0,
            StoredWidth = (byte)random.Next(0, 3),
            CombiningCharacters = random.Next(5) == 0 ? "\u0301\u0308" : null,
            HyperlinkUri = random.Next(5) == 0 ? $"https://example.test/{random.Next(100)}" : null,
            ShellIntegration = (ShellIntegrationKind)random.Next(4),
        };
    }
}
