using Devolutions.Terminal.Core;

namespace Devolutions.Terminal.Render;

public static class TerminalFrameDiffer
{
    public static IReadOnlyList<int> GetDirtyRows(
        TerminalRenderFrame? previous,
        TerminalRenderFrame current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (previous is null ||
            previous.Columns != current.Columns ||
            previous.Rows != current.Rows ||
            previous.Background != current.Background ||
            !ImagesEqual(previous.Images, current.Images))
        {
            var allRows = new int[current.Rows];
            for (var i = 0; i < allRows.Length; i++)
            {
                allRows[i] = i;
            }

            return allRows;
        }

        var dirty = new List<int>();
        var rowCount = Math.Min(previous.RowsData.Count, current.RowsData.Count);
        for (var row = 0; row < rowCount; row++)
        {
            if (!RowsEqual(previous.RowsData[row], current.RowsData[row]))
            {
                dirty.Add(row);
            }
        }

        AddCursorRow(previous, current, previous.CursorY, dirty);
        AddCursorRow(previous, current, current.CursorY, dirty);
        dirty.Sort();
        return dirty;
    }

    private static bool ImagesEqual(
        IReadOnlyList<TerminalImageOverlay> left,
        IReadOnlyList<TerminalImageOverlay> right)
    {
        if (ReferenceEquals(left, right) || (left.Count == 0 && right.Count == 0))
        {
            return true;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            var a = left[index];
            var b = right[index];
            if (a.Id != b.Id ||
                a.Protocol != b.Protocol ||
                a.AlternateBuffer != b.AlternateBuffer ||
                a.AnchorColumn != b.AnchorColumn ||
                a.AnchorRow != b.AnchorRow ||
                a.CellGeometry != b.CellGeometry ||
                !ReferenceEquals(a.Sixel, b.Sixel) ||
                !ReferenceEquals(a.InlineImage, b.InlineImage) ||
                !KittyEqual(a.Kitty, b.Kitty))
            {
                return false;
            }
        }

        return true;
    }

    private static bool KittyEqual(KittyImage? left, KittyImage? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return left.ImageId == right.ImageId &&
               left.PlacementId == right.PlacementId &&
               left.Columns == right.Columns &&
               left.Rows == right.Rows &&
               left.PixelOffsetX == right.PixelOffsetX &&
               left.PixelOffsetY == right.PixelOffsetY &&
               left.CropX == right.CropX &&
               left.CropY == right.CropY &&
               left.CropWidth == right.CropWidth &&
               left.CropHeight == right.CropHeight &&
               left.ZIndex == right.ZIndex &&
               ReferenceEquals(left.Data, right.Data);
    }

    private static void AddCursorRow(
        TerminalRenderFrame previous,
        TerminalRenderFrame current,
        int row,
        List<int> dirty)
    {
        if (row < 0 || row >= current.Rows || dirty.Contains(row))
        {
            return;
        }

        if (previous.CursorX != current.CursorX ||
            previous.CursorY != current.CursorY ||
            previous.CursorVisible != current.CursorVisible ||
            previous.CursorStyle != current.CursorStyle ||
            previous.CursorHeightPercentage != current.CursorHeightPercentage ||
            previous.CursorColor != current.CursorColor)
        {
            dirty.Add(row);
        }
    }

    private static bool RowsEqual(TerminalRenderRow left, TerminalRenderRow right)
    {
        // DECDWL/DECDHL change how identical runs are drawn.
        if (left.Rendition != right.Rendition ||
            left.Runs.Count != right.Runs.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Runs.Count; index++)
        {
            var a = left.Runs[index];
            var b = right.Runs[index];
            if (a.StartColumn != b.StartColumn ||
                a.CellCount != b.CellCount ||
                a.Attributes != b.Attributes ||
                !string.Equals(a.Text, b.Text, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
