using System.Text;
using Devolutions.Terminal.Core;

namespace Devolutions.Terminal.Render;

public static class TerminalRenderPlanner
{
    public static TerminalRenderFrame Create(
        TerminalSnapshot snapshot,
        ColorScheme scheme,
        TerminalRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(scheme);
        options ??= new TerminalRenderOptions();

        var rows = new TerminalRenderRow[snapshot.Buffer.Lines.Count];
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            rows[rowIndex] = PlanRow(
                rowIndex,
                snapshot.Buffer.Lines[rowIndex],
                scheme,
                snapshot.ReverseVideo);
        }

        var cursorY = snapshot.Buffer.CursorY + snapshot.Buffer.ScrollOffset;
        return new TerminalRenderFrame(
            snapshot.Buffer.Columns,
            snapshot.Buffer.Rows,
            snapshot.Buffer.CursorX,
            cursorY,
            snapshot.CursorVisible && cursorY < snapshot.Buffer.Rows,
            snapshot.ReverseVideo ? scheme.Foreground : scheme.Background,
            scheme.Cursor,
            scheme.SelectionBackground,
            rows)
        {
            CursorStyle = options.CursorStyle,
            CursorHeightPercentage = Math.Clamp(options.CursorHeightPercentage, 1, 100),
            Images = snapshot.Images
                .Where(image => image.AlternateBuffer == snapshot.AlternateBufferActive)
                .ToArray(),
            DrcsGlyphs = snapshot.DrcsGlyphs,
        };
    }

    public static ResolvedCellAttributes Resolve(
        CellAttributes attributes,
        string? hyperlinkUri,
        ColorScheme scheme,
        bool reverseScreen)
    {
        var foreground = attributes.Foreground.ToArgb(scheme, foreground: true);
        var background = attributes.Background.ToArgb(scheme, foreground: false);
        if ((attributes.Flags & CellFlags.Faint) != 0)
        {
            foreground = Fade(foreground);
        }

        if ((attributes.Flags & CellFlags.Inverse) != 0)
        {
            (foreground, background) = (background, foreground);
        }

        if (reverseScreen)
        {
            (foreground, background) = (background, foreground);
        }

        return new ResolvedCellAttributes(
            foreground,
            background,
            attributes.Flags,
            hyperlinkUri);
    }

    private static TerminalRenderRow PlanRow(
        int rowIndex,
        TextBufferLineSnapshot line,
        ColorScheme scheme,
        bool reverseScreen)
    {
        var cells = line.Cells;
        var runs = new List<TerminalRenderRun>();
        Span<char> textBuffer = cells.Count <= 256
            ? stackalloc char[cells.Count]
            : new char[cells.Count];
        Span<TerminalTextCluster> clusterBuffer = cells.Count <= 256
            ? stackalloc TerminalTextCluster[cells.Count]
            : new TerminalTextCluster[cells.Count];
        var column = 0;
        while (column < cells.Count)
        {
            var start = column;
            var first = cells[column];
            var resolved = Resolve(first.Attributes, first.HyperlinkUri, scheme, reverseScreen);
            var end = start + 1;
            while (end < cells.Count &&
                   Resolve(cells[end].Attributes, cells[end].HyperlinkUri, scheme, reverseScreen) == resolved)
            {
                end++;
            }

            var textLength = 0;
            var clusterCount = 0;
            var asciiOnly = true;
            Rune? previousRune = null;
            for (; column < end; column++)
            {
                var cell = cells[column];
                if (cell.IsWideContinuation ||
                    cell.Rune.Value is < 0x20 or > 0x7e ||
                    cell.DisplayWidth != 1 ||
                    !string.IsNullOrEmpty(cell.CombiningCharacters))
                {
                    asciiOnly = false;
                }

                if (!cell.IsWideContinuation)
                {
                    var offset = textLength;
                    var required = textLength + cell.Rune.Utf16SequenceLength +
                                   (cell.CombiningCharacters?.Length ?? 0);
                    if (required > textBuffer.Length)
                    {
                        var grown = new char[Math.Max(required, textBuffer.Length * 2)];
                        textBuffer[..textLength].CopyTo(grown);
                        textBuffer = grown;
                    }

                    textLength += cell.Rune.EncodeToUtf16(textBuffer[textLength..]);
                    if (cell.CombiningCharacters is { } combining)
                    {
                        combining.AsSpan().CopyTo(textBuffer[textLength..]);
                        textLength += combining.Length;
                    }

                    var cellWidth = cell.DisplayWidth;
                    if (clusterCount > 0 &&
                        (offset > 0 && textBuffer[offset - 1] == '\u200D' ||
                         (previousRune is { } prior &&
                          IsContextualScript(prior) &&
                          IsContextualScript(cell.Rune))))
                    {
                        var previous = clusterBuffer[clusterCount - 1];
                        clusterBuffer[clusterCount - 1] = previous with
                        {
                            TextLength = textLength - previous.TextOffset,
                            CellCount = (column + cellWidth) - previous.StartColumn,
                        };
                    }
                    else
                    {
                        clusterBuffer[clusterCount++] = new TerminalTextCluster(
                            offset,
                            textLength - offset,
                            column,
                            cellWidth);
                    }

                    previousRune = cell.Rune;
                }
            }

            runs.Add(new TerminalRenderRun(
                start,
                end - start,
                new string(textBuffer[..textLength]),
                resolved,
                asciiOnly
                    ? new AsciiClusters(start, clusterCount)
                    : clusterBuffer[..clusterCount].ToArray()));
        }

        return new TerminalRenderRow(rowIndex, runs)
        {
            Rendition = line.Rendition,
        };
    }

    private static bool IsContextualScript(Rune rune) =>
        rune.Value is >= 0x0590 and <= 0x109F or
            >= 0x1780 and <= 0x17FF or
            >= 0xA840 and <= 0xA8FF;

    private static uint Fade(uint argb)
    {
        var alpha = (byte)(argb >> 24);
        var red = (byte)((argb >> 16) & 0xFF);
        var green = (byte)((argb >> 8) & 0xFF);
        var blue = (byte)(argb & 0xFF);
        return ((uint)alpha << 24) |
               ((uint)(red / 2) << 16) |
               ((uint)(green / 2) << 8) |
               (byte)(blue / 2);
    }

    private sealed class AsciiClusters(int startColumn, int count) : IReadOnlyList<TerminalTextCluster>
    {
        public int Count => count;

        public TerminalTextCluster this[int index] =>
            (uint)index < (uint)count
                ? new TerminalTextCluster(index, 1, startColumn + index, 1)
                : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<TerminalTextCluster> GetEnumerator()
        {
            for (var index = 0; index < count; index++)
            {
                yield return this[index];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
