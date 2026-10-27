using System.Text;
using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Core.Tests;

/// <summary>
/// <see cref="CellRow"/> packs cells, keeps rare strings in side arrays (combining
/// text as interned IDs) and leaves a logically blank tail unwritten. Random operation sequences must
/// leave it indistinguishable from a plain <see cref="Cell"/> array.
/// </summary>
public sealed class CellRowModelTests
{
    private const int Columns = 12;

    [Fact]
    public void RandomOperationsMatchPlainCellArrays()
    {
        var random = new Random(4242);
        for (var iteration = 0; iteration < 300; iteration++)
        {
            var attributes = RandomAttributes(random);
            // Rows of one buffer share a combining table and copy IDs; rows with
            // their own tables copy the text.
            var table = iteration % 2 == 0 ? new CombiningTable() : null;
            var rows = new[] { new CellRow(Columns, attributes, table), new CellRow(Columns, CellAttributes.Default, table) };
            var models = new[] { Blank(attributes), Blank(CellAttributes.Default) };
            for (var step = 0; step < 40; step++)
            {
                var target = random.Next(2);
                var row = rows[target];
                var model = models[target];
                var start = random.Next(Columns);
                var count = random.Next(0, Columns - start + 1);
                string operation;
                switch (random.Next(10))
                {
                    case 0:
                        var cell = RandomCell(random);
                        row[start] = cell;
                        model[start] = cell;
                        operation = $"set {start}";
                        break;
                    case 1:
                        var fill = RandomCell(random);
                        row.Fill(start, count, fill);
                        model.AsSpan(start, count).Fill(fill);
                        operation = $"fill {start}+{count}";
                        break;
                    case 2:
                    case 3:
                        // Half of the blank fills run to the end of the line.
                        var blankAttributes = RandomAttributes(random);
                        var blankCount = random.Next(2) == 0 ? Columns - start : count;
                        row.FillBlank(start, blankCount, blankAttributes);
                        model.AsSpan(start, blankCount).Fill(BlankCell(blankAttributes));
                        operation = $"blank {start}+{blankCount}";
                        break;
                    case 4:
                        var text = Encoding.ASCII.GetBytes(new string('a', count).Select(_ => (char)random.Next(0x20, 0x7F)).ToArray());
                        var template = RandomCell(random);
                        template.CombiningCharacters = null;
                        template.IsWideContinuation = false;
                        template.StoredWidth = 1;
                        row.WriteAscii(start, text, template);
                        for (var index = 0; index < text.Length; index++)
                        {
                            model[start + index] = template with { Rune = new Rune(text[index]) };
                        }

                        operation = $"ascii {start}+{count}";
                        break;
                    case 5:
                        var source = random.Next(2);
                        var sourceStart = random.Next(Columns - count + 1);
                        rows[source].CopyTo(sourceStart, row, start, count);
                        Array.Copy(models[source], sourceStart, model, start, count);
                        operation = $"copy {source}:{sourceStart} -> {start}+{count}";
                        break;
                    case 6:
                        var reset = RandomAttributes(random);
                        row.Reset(reset);
                        Array.Fill(model, BlankCell(reset));
                        operation = "reset";
                        break;
                    case 7 when row.CanAppendAt(start):
                        // The single-pass path: narrow and wide cells, no combining text.
                        var appendTemplate = RandomCell(random);
                        appendTemplate.CombiningCharacters = null;
                        appendTemplate.IsWideContinuation = false;
                        var narrowCell = appendTemplate with { StoredWidth = 1 };
                        var wideCell = appendTemplate with { StoredWidth = 2 };
                        var tailCell = appendTemplate with { Rune = new Rune(' '), StoredWidth = 0, IsWideContinuation = true };
                        var cells = row.BeginAppend(start);
                        var written = 0;
                        var wroteWide = false;
                        while (written < count)
                        {
                            var rune = new Rune(random.Next(0x4E00, 0x4F00));
                            if (random.Next(2) == 0 && written + 2 <= count)
                            {
                                cells[written] = PackedCell.From(wideCell with { Rune = rune });
                                cells[written + 1] = PackedCell.From(tailCell);
                                model[start + written] = wideCell with { Rune = rune };
                                model[start + written + 1] = tailCell;
                                written += 2;
                                wroteWide = true;
                            }
                            else
                            {
                                cells[written] = PackedCell.From(narrowCell with { Rune = rune });
                                model[start + written] = narrowCell with { Rune = rune };
                                written++;
                            }
                        }

                        row.EndAppend(start, start + written, appendTemplate.HyperlinkUri, wroteWide);
                        for (var x = start; x < start + written; x++)
                        {
                            model[x].HyperlinkUri = appendTemplate.HyperlinkUri;
                        }

                        operation = $"append {start}+{written}";
                        break;
                    case 8:
                        var mark = new Rune(random.Next(2) == 0 ? 0x0301 : 0x200D);
                        row.AppendCombining(start, mark);
                        model[start].CombiningCharacters = (model[start].CombiningCharacters ?? string.Empty) + mark;
                        operation = $"combine {start}";
                        break;
                    default:
                        rows[target] = row.Clone();
                        operation = "clone";
                        break;
                }

                AssertSame($"iteration {iteration} step {step} ({operation}) row {target}", models[target], rows[target]);
                AssertSame($"iteration {iteration} step {step} ({operation}) row {1 - target}", models[1 - target], rows[1 - target]);
            }
        }
    }

    private static void AssertSame(string context, Cell[] expected, CellRow actual)
    {
        Assert.True(expected.SequenceEqual(actual.ToArray()), context);
        Assert.True(
            actual.MayHoldWide || !expected.Any(static cell => cell.IsWideContinuation || cell.DisplayWidth == 2),
            $"{context}: wide cell stored while MayHoldWide is false");
        for (var x = 0; x < Columns; x++)
        {
            var cell = expected[x];
            Assert.True(cell.IsWideContinuation == actual.IsWideContinuation(x), $"{context}: wide at {x}");
            Assert.True(cell.DisplayWidth == actual.DisplayWidth(x), $"{context}: width at {x}");
            Assert.True(cell.Rune == actual.RuneAt(x), $"{context}: rune at {x}");
            Assert.True(cell.CombiningCharacters == actual.CombiningAt(x), $"{context}: combining at {x}");
            Assert.True(cell.IsBlank == actual.IsBlank(x), $"{context}: blank at {x}");
        }
    }

    private static Cell[] Blank(CellAttributes attributes) =>
        Enumerable.Repeat(BlankCell(attributes), Columns).ToArray();

    private static Cell BlankCell(CellAttributes attributes) => new()
    {
        Rune = new Rune(' '),
        Attributes = attributes,
        StoredWidth = 1,
    };

    private static CellAttributes RandomAttributes(Random random) => random.Next(3) == 0
        ? CellAttributes.Default
        : new CellAttributes
        {
            Foreground = TermColor.FromIndex(random.Next(256)),
            Background = random.Next(2) == 0 ? TermColor.Default : TermColor.FromRgb(1, 2, (byte)random.Next(256)),
            Flags = (CellFlags)random.Next(0, 0x800),
        };

    private static Cell RandomCell(Random random) => new()
    {
        Rune = random.Next(3) == 0 ? new Rune(' ') : new Rune(random.Next(0x21, 0x3000)),
        Attributes = RandomAttributes(random),
        IsProtected = random.Next(4) == 0,
        IsWideContinuation = random.Next(5) == 0,
        StoredWidth = (byte)random.Next(0, 3),
        // A cell can carry empty combining text, which is not the same as none.
        CombiningCharacters = random.Next(8) switch { 0 or 1 => "\u0301", 2 => string.Empty, _ => null },
        HyperlinkUri = random.Next(4) == 0 ? "https://example.test" : null,
        ShellIntegration = (ShellIntegrationKind)random.Next(4),
    };
}
