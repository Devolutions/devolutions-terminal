using System.Text;
using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Core.Tests;

/// <summary>
/// Rows store combining text as IDs from one table per buffer. The table must
/// stay bounded however many distinct clusters pass through, without changing
/// the text of any cell still in the buffer.
/// </summary>
public sealed class CombiningTableTests
{
    private const int Columns = 10;

    [Fact]
    public void CompactionKeepsTheTextOfEveryLiveCell()
    {
        // The bottom line is written once, before any compaction. The top line is
        // then overwritten 1,200 times with mark sequences never printed before,
        // so the table repeatedly drops the IDs no line holds any more and
        // renumbers the rest, the bottom line's included.
        var buffer = new TextBuffer(Columns, 3, 4, hasHistory: true);
        var serial = 0;
        buffer.SetCursor(2, 0);
        var kept = PrintLine(buffer, ref serial);
        string[] last = [];
        for (var pass = 0; pass < 1200; pass++)
        {
            buffer.SetCursor(0, 0);
            last = PrintLine(buffer, ref serial);
        }

        // 12,000 clusters and their prefixes passed through; only the 20 cells
        // with marks keep IDs, so one compaction threshold bounds the table.
        Assert.InRange(buffer.CombiningIdCount, 1, 4097);
        Assert.Equal(last, buffer.GetRow(0).Select(static cell => cell.Text).ToArray());
        Assert.Equal(kept, buffer.GetRow(2).Select(static cell => cell.Text).ToArray());
    }

    // Prints a letter per cell, each followed by four marks from U+0300-U+030F
    // that spell out the next serial number, and returns the cell texts.
    private static string[] PrintLine(TextBuffer buffer, ref int serial)
    {
        var texts = new string[Columns];
        for (var x = 0; x < Columns; x++)
        {
            var text = new StringBuilder().Append((char)('a' + x));
            buffer.Print(new Rune('a' + x));
            for (var digit = 0; digit < 4; digit++)
            {
                var mark = new Rune(0x0300 + ((serial >> (4 * digit)) & 0xF));
                buffer.Print(mark);
                text.Append(mark.ToString());
            }

            serial++;
            texts[x] = text.ToString();
        }

        return texts;
    }
}
