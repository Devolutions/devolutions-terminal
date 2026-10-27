using System.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Devolutions.Terminal.Core;

public enum LineRendition : byte
{
    SingleWidth,
    DoubleWidth,
    DoubleHeightTop,
    DoubleHeightBottom,
}

public sealed record TextBufferLineSnapshot(
    IReadOnlyList<Cell> Cells,
    bool Wrapped,
    IReadOnlyList<ShellMark> Marks)
{
    public ShellMark? Mark => Marks.Count > 0 ? Marks[0] : null;
    public LineRendition Rendition { get; init; }
    public long LogicalLineId { get; init; }
    public int LogicalOffset { get; init; }
}

public sealed record TextBufferSnapshot(
    int Columns,
    int Rows,
    int CursorX,
    int CursorY,
    int HistoryCount,
    int ScrollOffset,
    IReadOnlyList<TextBufferLineSnapshot> Lines);

public sealed record TextBufferProjectionRow(
    Cell[] Cells,
    bool Wrapped,
    IReadOnlyList<ShellMark> Marks)
{
    public LineRendition Rendition { get; init; }
}

public sealed class TextBuffer
{
    private sealed class BufferLine
    {
        public BufferLine(CellRow cells, long logicalLineId)
        {
            Cells = cells;
            LogicalLineId = logicalLineId;
        }

        public CellRow Cells { get; }
        public bool Wrapped { get; set; }
        public LineRendition Rendition { get; set; }
        private List<ShellMark>? _marks;

        // Most lines never carry a mark; allocate the list on first write only.
        public List<ShellMark> Marks => _marks ??= [];
        public IReadOnlyList<ShellMark> MarksOrEmpty => (IReadOnlyList<ShellMark>?)_marks ?? [];

        public void ClearMarks() => _marks?.Clear();
        public long LogicalLineId { get; set; }
        public int LogicalOffset { get; set; }
    }

    private CircularBuffer<BufferLine> _lines;
    private readonly int _historySize;
    private bool[] _tabStops;
    private int _scrollOffset;
    private CellAttributes _currentAttributes = CellAttributes.Default;
    private bool _currentProtection;
    private ShellIntegrationKind _shellIntegration;
    private ShellMark? _activeMark;
    private bool _activeMarkHasOutput;
    private string? _pendingPrepend;
    // Printing always completes a Prepend with the scalar after it, so a cell
    // holding a lone Prepend can only come from a projected viewport.
    private bool _mayHoldLonePrepend;
    private long _nextLogicalLineId;

    public TextBuffer(int columns, int rows, int historySize, bool hasHistory)
    {
        Columns = Math.Max(1, columns);
        Rows = Math.Max(1, rows);
        _historySize = hasHistory ? Math.Max(0, historySize) : 0;
        HasHistory = hasHistory;
        _lines = new CircularBuffer<BufferLine>(Rows + _historySize);
        _tabStops = CreateDefaultTabStops(Columns);
        ScrollBottom = Rows - 1;
        MarginRight = Columns - 1;
        EnsureViewportLines();
    }

    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public bool HasHistory { get; }
    public int CursorX { get; set; }
    public int CursorY { get; set; }
    public int ScrollTop { get; private set; }
    public int ScrollBottom { get; private set; }
    public int MarginLeft { get; private set; }
    public int MarginRight { get; private set; }
    public bool LeftRightMargins { get; set; }
    public int ViewportStart => Math.Max(0, _lines.Count - Rows);
    public int ScrollOffset
    {
        get => _scrollOffset;
        set => _scrollOffset = Math.Clamp(value, 0, HistoryCount);
    }

    public bool OriginMode { get; set; }
    public bool ReverseWraparound { get; set; }
    public bool WrapPending { get; set; }
    public int TotalLines => _lines.Count;
    public int HistoryCount => ViewportStart;
    public long CoordinateVersion { get; private set; }
    public CellAttributes CurrentAttributes
    {
        get => _currentAttributes;
        set
        {
            _currentAttributes = value;
            InvalidatePrintTemplates();
        }
    }

    public bool CurrentProtection
    {
        get => _currentProtection;
        set
        {
            _currentProtection = value;
            InvalidatePrintTemplates();
        }
    }

    public string? CurrentHyperlinkUri { get; set; }
    public int SavedCursorX { get; set; }
    public int SavedCursorY { get; set; }
    public CellAttributes SavedAttributes { get; set; } = CellAttributes.Default;
    public bool SavedProtection { get; set; }

    public void Resize(
        int columns,
        int rows,
        IReadOnlyList<TerminalImageAnchor>? retainedAnchors = null,
        bool reflow = true)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        if (columns == Columns && rows == Rows)
        {
            return;
        }

        var oldColumns = Columns;
        var oldViewport = ViewportStart;
        var cursorAbsoluteLine = oldViewport + CursorY;
        var wasWrapPending = WrapPending;
        var source = _lines.ToList();
        var activeMark = _activeMark;
        var previousOffset = ScrollOffset;

        Columns = columns;
        Rows = rows;
        CoordinateVersion++;
        _tabStops = ResizeTabStops(_tabStops, columns);
        ScrollTop = 0;
        ScrollBottom = rows - 1;
        MarginLeft = 0;
        MarginRight = columns - 1;
        WrapPending = false;

        if (!reflow)
        {
            var clipped = new List<BufferLine>(Math.Max(source.Count, rows));
            var keep = Math.Min(source.Count, rows);
            for (var i = 0; i < keep; i++)
            {
                clipped.Add(ResizeLineCells(source[i], columns));
            }

            while (clipped.Count < rows)
            {
                clipped.Add(NewBlankLine(CellAttributes.Default));
            }

            _lines.ResetCapacity(rows + _historySize, clipped);
            _activeMark = activeMark;
            EnsureViewportLines();
            CursorY = Math.Clamp(CursorY, 0, rows - 1);
            CursorX = Math.Clamp(
                CursorX,
                0,
                Math.Max(0, EffectiveColumns(GetLiveLine(CursorY)) - 1));
            WrapPending = wasWrapPending &&
                          CursorX == EffectiveColumns(GetLiveLine(CursorY)) - 1;
            ScrollOffset = Math.Clamp(previousOffset, 0, HistoryCount);
            return;
        }

        var reflowed = Reflow(
            source,
            oldColumns,
            columns,
            retainedAnchors,
            cursorAbsoluteLine,
            CursorX + (wasWrapPending ? 1 : 0),
            oldViewport,
            out var cursorLine,
            out var cursorColumn,
            out var oldViewportTopLine);

        // Place the viewport like conhost/ConPTY and Windows Terminal do: keep the
        // old viewport top anchored unless the text (or cursor) no longer fits, and
        // never pull scrollback into view or push blank rows below the cursor into
        // history. ConPTY addresses the cursor with absolute positions relative to
        // its own (scrollback-less) buffer, so any other placement desynchronizes
        // the shell's input line from what is rendered.
        var bottomLine = Math.Max(LastTextLine(reflowed, retainedAnchors), cursorLine);
        var viewportTop = Math.Max(oldViewportTopLine, bottomLine - rows + 1);
        viewportTop = Math.Clamp(viewportTop, 0, Math.Max(0, cursorLine));
        var viewportEnd = viewportTop + rows;
        if (reflowed.Count > viewportEnd)
        {
            reflowed.RemoveRange(viewportEnd, reflowed.Count - viewportEnd);
        }

        while (reflowed.Count < viewportEnd)
        {
            reflowed.Add(NewBlankLine(CellAttributes.Default));
        }

        _lines.ResetCapacity(rows + _historySize, reflowed);
        _activeMark = activeMark;
        var dropped = Math.Max(0, reflowed.Count - _lines.Count);
        cursorLine = Math.Max(0, cursorLine - dropped);

        EnsureViewportLines();
        var viewport = ViewportStart;
        if (cursorLine < viewport)
        {
            cursorLine = viewport;
        }
        else if (cursorLine >= viewport + rows)
        {
            cursorLine = viewport + rows - 1;
        }

        CursorY = Math.Clamp(cursorLine - viewport, 0, rows - 1);
        CursorX = Math.Clamp(cursorColumn, 0, columns - 1);
        WrapPending = wasWrapPending &&
                      CursorX == EffectiveColumns(GetLiveLine(CursorY)) - 1;
        ScrollOffset = Math.Clamp(previousOffset, 0, HistoryCount);
    }

    /// <summary>A copy of the visible row; writes to it do not reach the buffer.</summary>
    public Cell[] GetRow(int viewportY) => GetVisibleLine(viewportY).Cells.ToArray();

    public Cell GetCell(int x, int y)
    {
        if ((uint)x >= (uint)Columns || (uint)y >= (uint)Rows)
        {
            return Cell.Blank;
        }

        return GetVisibleLine(y).Cells[x];
    }

    public void ApplyColors(
        BufferPosition start,
        BufferPosition end,
        TermColor? foreground,
        TermColor? background)
    {
        if (start.Line > end.Line ||
            (start.Line == end.Line && start.Column > end.Column))
        {
            (start, end) = (end, start);
        }

        var firstLine = Math.Clamp(start.Line, 0, _lines.Count - 1);
        var lastLine = Math.Clamp(end.Line, 0, _lines.Count - 1);
        for (var lineIndex = firstLine; lineIndex <= lastLine; lineIndex++)
        {
            var line = _lines[lineIndex];
            var firstColumn = lineIndex == start.Line ? start.Column : 0;
            var lastColumn = lineIndex == end.Line ? end.Column : Columns - 1;
            for (var column = Math.Clamp(firstColumn, 0, Columns - 1);
                 column <= Math.Clamp(lastColumn, 0, Columns - 1);
                 column++)
            {
                var cell = line.Cells[column];
                if (foreground is { } foregroundColor)
                {
                    cell.Attributes.Foreground = foregroundColor;
                }

                if (background is { } backgroundColor)
                {
                    cell.Attributes.Background = backgroundColor;
                }

                line.Cells[column] = cell;
            }
        }
    }

    public TextBufferSnapshot CreateSnapshot(bool includeHistory = false)
    {
        var first = includeHistory ? 0 : Math.Max(0, ViewportStart - ScrollOffset);
        var count = includeHistory ? _lines.Count : Rows;
        var copies = new TextBufferLineSnapshot[count];
        for (var i = 0; i < count; i++)
        {
            var sourceIndex = Math.Min(first + i, _lines.Count - 1);
            var cells = Array.AsReadOnly(_lines[sourceIndex].Cells.ToArray());
            copies[i] = new TextBufferLineSnapshot(
                cells,
                _lines[sourceIndex].Wrapped,
                _lines[sourceIndex].MarksOrEmpty
                    .Select(static mark => new ShellMark(mark.StartColumn, mark.ExitCode))
                    .ToArray())
            {
                Rendition = _lines[sourceIndex].Rendition,
                LogicalLineId = _lines[sourceIndex].LogicalLineId,
                LogicalOffset = _lines[sourceIndex].LogicalOffset,
            };
        }

        return new TextBufferSnapshot(Columns, Rows, CursorX, CursorY, HistoryCount, ScrollOffset, copies);
    }

    public void ReplaceViewport(
        IReadOnlyList<TextBufferProjectionRow> rows,
        int cursorX,
        int cursorY)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var replacement = new List<BufferLine>(Rows);
        for (var y = 0; y < Rows; y++)
        {
            var projection = y < rows.Count
                ? rows[y]
                : new TextBufferProjectionRow([], false, []);
            var cells = CellRow.FromCells(projection.Cells, Columns);
            _mayHoldLonePrepend = true;
            var line = new BufferLine(cells, ++_nextLogicalLineId)
            {
                Wrapped = projection.Wrapped,
                Rendition = projection.Rendition,
            };
            if (projection.Marks.Count > 0)
            {
                line.Marks.AddRange(projection.Marks.Select(static mark =>
                    new ShellMark(mark.StartColumn, mark.ExitCode)));
            }

            replacement.Add(line);
        }

        _lines.ResetCapacity(Rows + _historySize, replacement);
        CursorX = Math.Clamp(cursorX, 0, Columns - 1);
        CursorY = Math.Clamp(cursorY, 0, Rows - 1);
        ScrollOffset = 0;
        WrapPending = false;
    }

    public void AdvanceCoordinateVersion() => CoordinateVersion++;

    public TerminalImageAnchor CreateImageAnchor(int column, int viewportRow)
    {
        var line = GetLiveLine(viewportRow);
        return new TerminalImageAnchor(
            line.LogicalLineId,
            line.LogicalOffset + Math.Clamp(column, 0, EffectiveColumns(line) - 1));
    }

    public bool TryResolveImageAnchor(
        TerminalImageAnchor anchor,
        out int absoluteRow,
        out int column)
    {
        for (var row = 0; row < _lines.Count; row++)
        {
            var line = _lines[row];
            if (line.LogicalLineId != anchor.LogicalLineId)
            {
                continue;
            }

            var width = EffectiveColumns(line);
            if (row + 1 < _lines.Count &&
                _lines[row + 1].LogicalLineId == line.LogicalLineId)
            {
                var nextOffset = _lines[row + 1].LogicalOffset;
                if (nextOffset > line.LogicalOffset)
                {
                    width = Math.Min(width, nextOffset - line.LogicalOffset);
                }
            }
            if (anchor.LogicalOffset < line.LogicalOffset ||
                anchor.LogicalOffset >= line.LogicalOffset + width)
            {
                continue;
            }

            absoluteRow = row;
            column = anchor.LogicalOffset - line.LogicalOffset;
            return true;
        }

        absoluteRow = -1;
        column = -1;
        return false;
    }

    public LineRendition CurrentLineRendition => GetLiveLine(CursorY).Rendition;

    public void SetCurrentLineRendition(LineRendition rendition)
    {
        var line = GetLiveLine(CursorY);
        line.Rendition = rendition;
        line.Wrapped = false;
        var width = EffectiveColumns(line);
        if (width < Columns)
        {
            EraseCells(line.Cells, width, Columns);
        }

        CursorX = Math.Clamp(CursorX, 0, width - 1);
        WrapPending = false;
    }

    public int GetPrintAdvance(Rune rune)
    {
        var target = GetJoinTarget(rune);
        if (target is null)
        {
            return WcWidth.Width(rune);
        }

        var (row, column, desiredWidth, lineColumns) = target.Value;
        return column + desiredWidth <= lineColumns
            ? Math.Max(0, desiredWidth - row[column].DisplayWidth)
            : 0;
    }

    /// <summary>
    /// Prints printable ASCII (0x20-0x7E) with auto-wrap, producing exactly what
    /// <see cref="Print"/> per byte would. Only the first byte can extend the
    /// previous grapheme (a pending or completed Prepend); every later byte
    /// follows an ASCII cell written here, and ASCII never extends a grapheme, so
    /// the rest skip the join checks and are stored a row segment at a time.
    /// Returns whether the last byte advanced the cursor (only a lone byte that
    /// joined a Prepend does not), which is what REP repeats.
    /// </summary>
    public bool PrintAscii(ReadOnlySpan<byte> text)
    {
        if (text.IsEmpty)
        {
            return false;
        }

        if (LeftRightMargins)
        {
            // DECLRMM can leave the cursor outside the margins after a wrap; keep
            // that rare geometry on the per-character path.
            var advanced = false;
            foreach (var value in text)
            {
                advanced = PrintAndGetAdvance(new Rune(value)) > 0;
            }

            return advanced;
        }

        var index = 0;
        if (_pendingPrepend is not null || PreviousIsLonePrepend())
        {
            var firstAdvanced = PrintAndGetAdvance(new Rune(text[0])) > 0;
            if (text.Length == 1)
            {
                return firstAdvanced;
            }

            index = 1;
        }

        var template = AsciiTemplate();
        var hyperlink = CurrentHyperlinkUri;
        if (index == text.Length - 1 && !WrapPending)
        {
            // A lone byte that fits (one between escape sequences, or the y of
            // `yes`): what the loop below does for it, as a single store.
            var line = GetLiveLine(CursorY);
            var effectiveColumns = RightBound(line) + 1;
            if (CursorX < effectiveColumns)
            {
                var row = line.Cells;
                ClearGlyphAt(row, CursorX);
                row.StoreAscii(CursorX, text[index], template, hyperlink);
                if (++CursorX >= effectiveColumns)
                {
                    CursorX = effectiveColumns - 1;
                    WrapPending = true;
                }

                return true;
            }
        }

        while (index < text.Length)
        {
            if (WrapPending)
            {
                var source = GetLiveLine(CursorY);
                source.Wrapped = true;
                CarriageReturn();
                LineFeed();
                ContinueLogicalLine(source);
            }

            var line = GetLiveLine(CursorY);
            var effectiveColumns = RightBound(line) + 1;
            if (CursorX + 1 > effectiveColumns)
            {
                line.Wrapped = true;
                CarriageReturn();
                LineFeed();
                ContinueLogicalLine(line);
                line = GetLiveLine(CursorY);
                effectiveColumns = RightBound(line) + 1;
            }

            var row = line.Cells;
            var count = Math.Min(text.Length - index, effectiveColumns - CursorX);
            // Wide glyphs inside a segment are overwritten cell by cell, so only
            // glyphs straddling its ends need their other half blanked. A later
            // segment starts after a cell written here or at column 0 of a new
            // line, so only the first can straddle its start.
            if (index == 0)
            {
                ClearGlyphAt(row, CursorX);
            }

            ClearGlyphAt(row, CursorX + count - 1);
            row.WriteAscii(CursorX, text.Slice(index, count), template, hyperlink);
            index += count;
            CursorX += count;
            if (CursorX >= effectiveColumns)
            {
                CursorX = effectiveColumns - 1;
                WrapPending = true;
            }
        }

        return true;
    }

    /// <summary>
    /// Prints runes with auto-wrap, producing exactly what <see cref="PrintAndGetAdvance"/>
    /// per rune would. A rune that cannot take part in a grapheme join (no
    /// <see cref="MayExtendPrevious"/>, not a Prepend, nothing pending and no lone
    /// Prepend before it) and has a width of 1 or 2 is stored directly from packed
    /// templates; every other rune takes the per-rune path. Returns whether any rune
    /// advanced the cursor and the last one that did, which is what REP repeats.
    /// </summary>
    public bool PrintRunes(ReadOnlySpan<Rune> runes, out Rune lastAdvanced)
    {
        lastAdvanced = default;
        var advanced = false;
        if (LeftRightMargins)
        {
            // DECLRMM can leave the cursor outside the margins after a wrap; keep
            // that rare geometry on the per-character path.
            foreach (var rune in runes)
            {
                if (PrintAndGetAdvance(rune) > 0)
                {
                    lastAdvanced = rune;
                    advanced = true;
                }
            }

            return advanced;
        }

        UpdateRuneTemplates();
        var simpleWidths = CurrentSimpleWidths();
        var hyperlink = CurrentHyperlinkUri;
        var widths = _segmentWidths;
        var line = GetLiveLine(CursorY);
        var effectiveColumns = RightBound(line) + 1;
        // Whether the cell the next rune would join has no combining text ending in
        // ZWJ, so an Extended_Pictographic rune cannot join it (GB11): true after
        // anything printed here advanced the cursor, since only a zero-width append
        // (no advance) can leave a ZWJ at the end of a cell.
        var previousEndsWithoutZwj = JoinTargetEndsWithoutZwj();
        var index = 0;
        while (index < runes.Length)
        {
            var rune = runes[index];
            // Only the per-rune path below can set a pending Prepend.
            var width = _pendingPrepend is null && !_mayHoldLonePrepend
                ? SimpleWidth(rune, simpleWidths, previousEndsWithoutZwj)
                : 0;
            if (width == 0)
            {
                previousEndsWithoutZwj = PrintAndGetAdvance(rune) > 0;
                if (previousEndsWithoutZwj)
                {
                    lastAdvanced = rune;
                    advanced = true;
                }

                line = GetLiveLine(CursorY);
                effectiveColumns = RightBound(line) + 1;
                index++;
                continue;
            }

            // PrintCore for runes that cannot join, a row segment at a time.
            if (WrapPending)
            {
                line.Wrapped = true;
                CarriageReturn();
                LineFeed();
                ContinueLogicalLine(line);
                line = GetLiveLine(CursorY);
                effectiveColumns = RightBound(line) + 1;
            }

            if (CursorX + width > effectiveColumns)
            {
                line.Wrapped = true;
                CarriageReturn();
                LineFeed();
                ContinueLogicalLine(line);
                line = GetLiveLine(CursorY);
                effectiveColumns = RightBound(line) + 1;
            }

            // Take every following rune that is also simple and still fits the row.
            // Only the first can wrap; the rest follow cells stored here.
            var row = line.Cells;
            var end = CursorX;
            var next = index;
            // A line narrower than a wide glyph (one effective column) keeps the
            // bounds-checked path below.
            if (!row.MayHoldWide && row.CanAppendAt(CursorX) && CursorX + width <= effectiveColumns)
            {
                // No wide glyph in the row can straddle the segment, so classify and
                // store in one pass.
                next = index + AppendSimpleRunes(
                    row.BeginAppend(CursorX),
                    runes[index..],
                    width,
                    effectiveColumns - CursorX,
                    simpleWidths,
                    _narrowBits,
                    _wideBits,
                    _continuationTemplate,
                    out var written,
                    out var wroteWide);
                end = CursorX + written;
                row.EndAppend(CursorX, end, hyperlink, wroteWide);
            }
            else
            {
                // Record widths for the write.
                do
                {
                    widths[next - index] = (byte)width;
                    end += width;
                    next++;
                    width = next < runes.Length && next - index < widths.Length
                        ? SimpleWidth(runes[next], simpleWidths, previousEndsWithoutZwj: true)
                        : 0;
                }
                while (width != 0 && end + width <= effectiveColumns);

                // As in PrintAscii, cells inside the segment are overwritten one by
                // one, so only glyphs straddling its first and last cell need clearing.
                ClearGlyphAt(row, CursorX);
                ClearGlyphAt(row, end - 1);
                row.WriteRunes(
                    CursorX,
                    end,
                    runes[index..next],
                    widths,
                    _narrowTemplate,
                    _wideTemplate,
                    _continuationTemplate,
                    hyperlink);
            }

            lastAdvanced = runes[next - 1];
            advanced = true;
            previousEndsWithoutZwj = true;
            CursorX = end;
            if (CursorX >= effectiveColumns)
            {
                CursorX = effectiveColumns - 1;
                WrapPending = true;
            }

            index = next;
        }

        return advanced;
    }

    // Packed cells stored by PrintAscii and PrintRunes: the ASCII (narrow) cell,
    // the wide and continuation cells, and the narrow and wide ones without their
    // rune for PackedCell.StoreWithRune. They depend only on the current
    // attributes, protection and shell-integration state, whose setters mark them
    // stale. The ASCII one is rebuilt apart from the rest, so a style change before
    // every ASCII cell does not also pay for the rune templates.
    private PackedCell _asciiTemplate;
    private bool _asciiTemplateStale = true;
    private PackedCell _narrowTemplate;
    private PackedCell _wideTemplate;
    private PackedCell _continuationTemplate;
    private Vector128<ulong> _narrowBits;
    private Vector128<ulong> _wideBits;
    private bool _runeTemplatesStale = true;
    private readonly byte[] _segmentWidths = new byte[256];

    private void InvalidatePrintTemplates()
    {
        _asciiTemplateStale = true;
        _runeTemplatesStale = true;
    }

    private void SetShellIntegration(ShellIntegrationKind kind)
    {
        _shellIntegration = kind;
        InvalidatePrintTemplates();
    }

    private PackedCell CurrentTemplate() =>
        PackedCell.From(new Rune(' '), _currentAttributes, _currentProtection, false, 1, _shellIntegration);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PackedCell AsciiTemplate()
    {
        if (_asciiTemplateStale)
        {
            _asciiTemplate = CurrentTemplate();
            _asciiTemplateStale = false;
        }

        return _asciiTemplate;
    }

    private void UpdateRuneTemplates()
    {
        if (!_runeTemplatesStale)
        {
            return;
        }

        var narrow = CurrentTemplate();
        _narrowTemplate = narrow;
        _wideTemplate = narrow.WithWidth(2, isWideContinuation: false);
        _continuationTemplate = narrow.WithWidth(0, isWideContinuation: true);
        _narrowBits = narrow.RuneFreeBits;
        _wideBits = _wideTemplate.RuneFreeBits;
        _runeTemplatesStale = false;
    }

    /// <summary>
    /// The append loop of <see cref="PrintRunes"/>, kept apart so its state stays in
    /// registers. Stores <paramref name="runes"/> from the first, whose simple
    /// <paramref name="width"/> fits in <paramref name="limit"/> cells, while each
    /// next rune is also simple and still fits. Every rune after the first follows
    /// a cell stored here, so none can join it. Returns the runes stored;
    /// <paramref name="written"/> is the cells they took.
    /// </summary>
    private static int AppendSimpleRunes(
        Span<PackedCell> cells,
        ReadOnlySpan<Rune> runes,
        int width,
        int limit,
        byte[] simpleWidths,
        Vector128<ulong> narrowBits,
        Vector128<ulong> wideBits,
        PackedCell continuation,
        out int written,
        out bool wroteWide)
    {
        // These bound every write below, which therefore skips per-cell checks.
        if ((uint)limit > (uint)cells.Length || width is not (1 or 2) || width > limit || runes.IsEmpty)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        ref var cell = ref MemoryMarshal.GetReference(cells);
        ref var rune = ref MemoryMarshal.GetReference(runes);
        // Vector arguments can arrive by reference; locals keep them in registers
        // instead of reloading them after every store.
        var narrow = narrowBits;
        var wide = wideBits;
        var continuationBits = Unsafe.BitCast<PackedCell, Vector128<ulong>>(continuation);
        var count = 0;
        var stored = 0;
        while (true)
        {
            var value = Unsafe.Add(ref rune, count).Value;
            if (width == 2)
            {
                PackedCell.StoreWithRune(ref Unsafe.Add(ref cell, stored), wide, value);
                continuationBits.StoreUnsafe(ref Unsafe.As<PackedCell, ulong>(ref Unsafe.Add(ref cell, stored + 1)));
            }
            else
            {
                PackedCell.StoreWithRune(ref Unsafe.Add(ref cell, stored), narrow, value);
            }

            stored += width;
            if (++count == runes.Length)
            {
                break;
            }

            width = SimpleWidth(Unsafe.Add(ref rune, count), simpleWidths, previousEndsWithoutZwj: true);
            if (width == 0 || stored + width > limit)
            {
                break;
            }
        }

        // Only a wide rune takes more than one cell.
        written = stored;
        wroteWide = stored != count;
        return count;
    }

    /// <summary>
    /// The width of a rune <see cref="PrintRunes"/> may store without the per-rune
    /// path, or 0. Such a rune is not a Prepend and cannot extend the previous
    /// grapheme: either it has no join property at all, or it is an
    /// Extended_Pictographic scalar whose only possible join (GB11) needs combining
    /// text ending in ZWJ, which the previous cell is known not to have. The caller
    /// has ruled out a pending or lone Prepend. <paramref name="simpleWidths"/> is
    /// <see cref="CurrentSimpleWidths"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SimpleWidth(Rune rune, byte[] simpleWidths, bool previousEndsWithoutZwj)
    {
        var simple = (uint)rune.Value < (uint)simpleWidths.Length
            ? simpleWidths[rune.Value]
            : SimpleWidthBeyondTables(rune);
        return (simple & SimpleAfterZwjFreeCell) != 0 && !previousEndsWithoutZwj ? 0 : simple & 3;
    }

    // SimpleWidth's inputs in one load per rune for planes 0 and 1, with ambiguous
    // widths already resolved: the width in the low two bits (0 when the rune needs
    // the per-rune path), plus whether it may skip that path only after a cell
    // without a trailing ZWJ. There is one table per AmbiguousAsWide setting; the
    // wide one is built on first use.
    private const byte SimpleAfterZwjFreeCell = 4;
    private const byte NeverSimple = 8;
    private static readonly byte[] SimpleWidthsAmbiguousNarrow = BuildSimpleWidths(ambiguousAsWide: false);
    private static byte[]? _simpleWidthsAmbiguousWide;

    private static byte[] CurrentSimpleWidths() =>
        WcWidth.AmbiguousAsWide
            ? (_simpleWidthsAmbiguousWide ??= BuildSimpleWidths(ambiguousAsWide: true))
            : SimpleWidthsAmbiguousNarrow;

    private static byte[] BuildSimpleWidths(bool ambiguousAsWide)
    {
        var widths = new byte[0x20000];
        for (var value = 0; value < widths.Length; value++)
        {
            if (value is < 0xD800 or > 0xDFFF)
            {
                widths[value] = ResolveSimple(ClassifySimple(new Rune(value)), ambiguousAsWide);
            }
        }

        return widths;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static byte SimpleWidthBeyondTables(Rune rune) =>
        ResolveSimple(ClassifySimple(rune), WcWidth.AmbiguousAsWide);

    private static byte ResolveSimple(byte simpleClass, bool ambiguousAsWide)
    {
        var width = WcWidth.ResolveWidth((byte)(simpleClass & 3), ambiguousAsWide);
        return (simpleClass & NeverSimple) != 0 || width == 0
            ? (byte)0
            : (byte)(width | (simpleClass & SimpleAfterZwjFreeCell));
    }

    // Built from ClassifyJoin, not the JoinClasses table, which static
    // initialization order may not have filled yet.
    private static byte ClassifySimple(Rune rune)
    {
        var joinClass = ClassifyJoin(rune);
        var widthClass = WcWidth.WidthClass(rune);
        if ((joinClass & PrependScalar) != 0 ||
            ((joinClass & MayExtendPrevious) != 0 && (joinClass & ExtendsOnlyAfterZwj) == 0))
        {
            return (byte)(NeverSimple | widthClass);
        }

        return (joinClass & ExtendsOnlyAfterZwj) != 0
            ? (byte)(SimpleAfterZwjFreeCell | widthClass)
            : widthClass;
    }

    /// <summary>
    /// Whether the cell a printed rune would join (as <see cref="GetJoinTarget"/>
    /// finds it) has no combining text ending in ZWJ, or there is none.
    /// </summary>
    private bool JoinTargetEndsWithoutZwj()
    {
        var y = CursorY;
        var x = WrapPending ? CursorX : CursorX - 1;
        if (!WrapPending && x < 0 && y > 0 && GetLiveLine(y - 1).Wrapped)
        {
            y--;
            x = Columns - 1;
        }

        if (x < 0)
        {
            return true;
        }

        var row = GetLiveLine(y).Cells;
        if (row.IsWideContinuation(x) && x > 0)
        {
            x--;
        }

        return row.CombiningAt(x)?.EndsWith('\u200D') != true;
    }

    /// <summary>
    /// Whether the cell a printed rune would join holds a lone Prepend scalar
    /// (GB9b). That is the only join open to runes without
    /// <see cref="MayExtendPrevious"/>, printable ASCII included. Conservative:
    /// true sends the rune through the full join check.
    /// </summary>
    private bool PreviousIsLonePrepend()
    {
        if (!_mayHoldLonePrepend)
        {
            return false;
        }

        var y = CursorY;
        var x = WrapPending ? CursorX : CursorX - 1;
        if (!WrapPending && x < 0 && y > 0 && GetLiveLine(y - 1).Wrapped)
        {
            y--;
            x = Columns - 1;
        }

        if (x < 0)
        {
            return false;
        }

        var row = GetLiveLine(y).Cells;
        if (row.IsWideContinuation(x) && x > 0)
        {
            x--;
        }

        var previous = row.RuneAt(x);
        return IsPrependScalar(previous) && row.CombiningAt(x) is null;
    }

    public void Print(Rune rune, bool autoWrap = true) => PrintCore(rune, autoWrap, out _);

    /// <summary>
    /// Prints like <see cref="Print"/> and returns what <see cref="GetPrintAdvance"/>
    /// would have returned just before, resolving the grapheme join target once
    /// instead of once per call.
    /// </summary>
    public int PrintAndGetAdvance(Rune rune, bool autoWrap = true)
    {
        if (_pendingPrepend is not null || IsPrependScalar(rune))
        {
            // GetPrintAdvance ignores a pending Prepend while Print consumes it;
            // keep both calls so the reported advance stays the same.
            var advance = GetPrintAdvance(rune);
            Print(rune, autoWrap);
            return advance;
        }

        PrintCore(rune, autoWrap, out var printed);
        return printed;
    }

    private void PrintCore(Rune rune, bool autoWrap, out int advance)
    {
        advance = 0;
        if (IsPrependScalar(rune))
        {
            _pendingPrepend = (_pendingPrepend ?? string.Empty) + rune;
            return;
        }

        var prepend = _pendingPrepend;
        _pendingPrepend = null;
        // Most runes cannot extend the previous grapheme unless it is a lone
        // Prepend; skip the full join evaluation for them.
        if (prepend is null &&
            ((JoinClass(rune) & MayExtendPrevious) != 0 || PreviousIsLonePrepend()) &&
            TryAppendJoinedRune(rune, out advance))
        {
            return;
        }

        var width = WcWidth.Width(rune);
        advance = width;
        if (width == 0)
        {
            AppendCombining(rune);
            return;
        }

        if (WrapPending)
        {
            var source = GetLiveLine(CursorY);
            source.Wrapped = true;
            CarriageReturn();
            LineFeed();
            ContinueLogicalLine(source);
        }

        var line = GetLiveLine(CursorY);
        var effectiveColumns = RightBound(line) + 1;
        if (CursorX + width > effectiveColumns && !autoWrap)
        {
            // DECAWM reset: never wrap or scroll; overwrite at the right edge.
            CursorX = Math.Max(0, effectiveColumns - width);
        }
        else if (CursorX + width > effectiveColumns)
        {
            line.Wrapped = true;
            CarriageReturn();
            LineFeed();
            ContinueLogicalLine(line);
            line = GetLiveLine(CursorY);
            effectiveColumns = RightBound(line) + 1;
        }

        var row = line.Cells;
        ClearGlyphAt(row, CursorX);
        if (width == 2 && CursorX + 1 < Columns)
        {
            ClearGlyphAt(row, CursorX + 1);
        }

        var storedRune = rune;
        string? combining = null;
        if (prepend is not null)
        {
            var runes = prepend.EnumerateRunes().ToArray();
            storedRune = runes[0];
            combining = string.Concat(runes.Skip(1).Select(static value => value.ToString())) + rune;
        }

        row.Store(
            CursorX,
            storedRune,
            CurrentAttributes,
            CurrentProtection,
            isWideContinuation: false,
            (byte)width,
            combining,
            CurrentHyperlinkUri,
            _shellIntegration);
        if (width == 2 && CursorX + 1 < Columns)
        {
            row.Store(
                CursorX + 1,
                new Rune(' '),
                CurrentAttributes,
                CurrentProtection,
                isWideContinuation: true,
                storedWidth: 0,
                combining: null,
                CurrentHyperlinkUri,
                _shellIntegration);
        }

        CursorX += width;
        if (CursorX >= effectiveColumns)
        {
            CursorX = effectiveColumns - 1;
            WrapPending = true;
        }
    }

    public void CarriageReturn()
    {
        CursorX = LeftBound;
        WrapPending = false;
    }

    public void LineFeed(bool alsoCarriageReturn = false)
    {
        WrapPending = false;
        if (CursorY == ScrollBottom)
        {
            ScrollUp(1);
        }
        else if (CursorY < Rows - 1)
        {
            CursorY++;
        }

        if (alsoCarriageReturn)
        {
            CursorX = LeftBound;
        }
        else
        {
            CursorX = Math.Min(CursorX, EffectiveColumns(GetLiveLine(CursorY)) - 1);
        }
    }

    public void ReverseIndex()
    {
        WrapPending = false;
        if (CursorY == ScrollTop)
        {
            ScrollDown(1);
        }
        else if (CursorY > 0)
        {
            CursorY--;
        }

        CursorX = Math.Min(CursorX, EffectiveColumns(GetLiveLine(CursorY)) - 1);
    }

    public void Backspace()
    {
        WrapPending = false;
        if (CursorX > LeftBound)
        {
            CursorX--;
            if (GetLiveLine(CursorY).Cells[CursorX].IsWideContinuation && CursorX > LeftBound)
            {
                CursorX--;
            }

            return;
        }

        if (ReverseWraparound && CursorY > ScrollTop)
        {
            CursorY--;
            CursorX = RightBound(GetLiveLine(CursorY));
            if (GetLiveLine(CursorY).Cells[CursorX].IsWideContinuation && CursorX > LeftBound)
            {
                CursorX--;
            }
        }
    }

    public void Tab(int count = 1)
    {
        WrapPending = false;
        var columns = EffectiveColumns(GetLiveLine(CursorY));
        for (var n = 0; n < Math.Max(1, count); n++)
        {
            var next = columns - 1;
            for (var x = CursorX + 1; x < columns; x++)
            {
                if (_tabStops[x])
                {
                    next = x;
                    break;
                }
            }

            CursorX = next;
        }
    }

    public void BackTab(int count = 1)
    {
        WrapPending = false;
        for (var n = 0; n < Math.Max(1, count); n++)
        {
            var previous = 0;
            for (var x = CursorX - 1; x > 0; x--)
            {
                if (_tabStops[x])
                {
                    previous = x;
                    break;
                }
            }

            CursorX = previous;
        }
    }

    public void SetTabStop() => _tabStops[CursorX] = true;

    public void ClearTabStop(bool all)
    {
        if (all)
        {
            Array.Clear(_tabStops);
        }
        else
        {
            _tabStops[CursorX] = false;
        }
    }

    public int[] GetTabStops() =>
        _tabStops
            .Select((isSet, index) => (isSet, index))
            .Where(static item => item.isSet)
            .Select(static item => item.index)
            .ToArray();

    public void ReplaceTabStops(ReadOnlySpan<int> columns)
    {
        Array.Clear(_tabStops);
        foreach (var column in columns)
        {
            if ((uint)column < (uint)_tabStops.Length)
            {
                _tabStops[column] = true;
            }
        }
    }

    public void SetCursor(int row, int col, bool relativeToOrigin = true)
    {
        WrapPending = false;
        var top = OriginMode && relativeToOrigin ? ScrollTop : 0;
        var bottom = OriginMode && relativeToOrigin ? ScrollBottom : Rows - 1;
        var left = OriginMode && relativeToOrigin ? LeftBound : 0;
        CursorY = Math.Clamp(top + row, top, bottom);
        var right = OriginMode && relativeToOrigin
            ? RightBound(GetLiveLine(CursorY))
            : EffectiveColumns(GetLiveLine(CursorY)) - 1;
        CursorX = Math.Clamp(left + col, left, right);
    }

    public void MoveCursor(int dx, int dy, bool respectMargins = false)
    {
        WrapPending = false;
        var withinMargins = CursorY >= ScrollTop && CursorY <= ScrollBottom;
        var top = OriginMode || (respectMargins && withinMargins) ? ScrollTop : 0;
        var bottom = OriginMode || (respectMargins && withinMargins) ? ScrollBottom : Rows - 1;
        var left = OriginMode ? LeftBound : 0;
        var right = OriginMode
            ? RightBound(GetLiveLine(CursorY))
            : EffectiveColumns(GetLiveLine(CursorY)) - 1;
        if (ReverseWraparound && dy == 0 && CursorX + dx < left && CursorY > top)
        {
            var steps = left - (CursorX + dx);
            CursorY = Math.Max(top, CursorY - 1);
            right = OriginMode
                ? RightBound(GetLiveLine(CursorY))
                : EffectiveColumns(GetLiveLine(CursorY)) - 1;
            CursorX = Math.Max(left, right - steps + 1);
            return;
        }

        CursorY = Math.Clamp(CursorY + dy, top, bottom);
        right = OriginMode
            ? RightBound(GetLiveLine(CursorY))
            : EffectiveColumns(GetLiveLine(CursorY)) - 1;
        CursorX = Math.Clamp(CursorX + dx, left, right);
    }

    public void SetLeftRightMargins(int left, int right)
    {
        left = Math.Clamp(left, 0, Columns - 1);
        right = Math.Clamp(right, 0, Columns - 1);
        if (right <= left)
        {
            return;
        }

        MarginLeft = left;
        MarginRight = right;
    }

    public void FillAlignmentPattern()
    {
        WrapPending = false;
        CursorX = 0;
        CursorY = 0;
        var glyph = new Cell
        {
            Rune = new Rune('E'),
            Attributes = CellAttributes.Default,
            StoredWidth = 1,
        };
        for (var y = 0; y < Rows; y++)
        {
            var line = GetLiveLine(y);
            line.Wrapped = false;
            line.Rendition = LineRendition.SingleWidth;
            line.Cells.Fill(0, line.Cells.Length, glyph);
        }
    }

    public void ResetViewMargins()
    {
        ScrollTop = 0;
        ScrollBottom = Rows - 1;
        LeftRightMargins = false;
        MarginLeft = 0;
        MarginRight = Columns - 1;
        OriginMode = false;
        ReverseWraparound = false;
    }

    public void SetScrollRegion(int top, int bottom)
    {
        top = Math.Clamp(top, 0, Rows - 1);
        bottom = Math.Clamp(bottom, 0, Rows - 1);
        if (bottom <= top)
        {
            return;
        }

        ScrollTop = top;
        ScrollBottom = bottom;
        SetCursor(0, 0);
    }

    public void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 1:
                EraseLineRange(0, CursorY - 1);
                EraseInLine(1);
                break;
            case 2:
                EraseLineRange(0, Rows - 1);
                break;
            case 3:
                TrimHistory();
                break;
            default:
                EraseInLine(0);
                EraseLineRange(CursorY + 1, Rows - 1);
                break;
        }
    }

    public void SelectiveEraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 1:
                EraseLineRange(0, CursorY - 1, selective: true);
                SelectiveEraseInLine(1);
                break;
            case 2:
                EraseLineRange(0, Rows - 1, selective: true);
                break;
            default:
                SelectiveEraseInLine(0);
                EraseLineRange(CursorY + 1, Rows - 1, selective: true);
                break;
        }
    }

    public void EraseInLine(int mode)
    {
        var row = GetLiveLine(CursorY).Cells;
        switch (mode)
        {
            case 1:
                EraseCells(row, 0, CursorX + 1);
                break;
            case 2:
                EraseCells(row, 0, Columns);
                break;
            default:
                EraseCells(row, CursorX, Columns);
                break;
        }
    }

    public void SelectiveEraseInLine(int mode)
    {
        var row = GetLiveLine(CursorY).Cells;
        switch (mode)
        {
            case 1:
                EraseCells(row, 0, CursorX + 1, selective: true);
                break;
            case 2:
                EraseCells(row, 0, Columns, selective: true);
                break;
            default:
                EraseCells(row, CursorX, Columns, selective: true);
                break;
        }
    }

    public void FillRectangle(Rune rune, int top, int left, int bottom, int right)
    {
        if (!TryNormalizeRectangle(ref top, ref left, ref bottom, ref right))
        {
            return;
        }

        for (var y = top; y <= bottom; y++)
        {
            var row = GetLiveLine(y).Cells;
            ClearGlyphAt(row, left);
            ClearGlyphAt(row, right);
            for (var x = left; x <= right; x++)
            {
                row[x] = new Cell
                {
                    Rune = rune,
                    Attributes = CurrentAttributes,
                    IsProtected = CurrentProtection,
                    StoredWidth = 1,
                };
            }
        }
    }

    public void EraseRectangle(int top, int left, int bottom, int right, bool selective)
    {
        if (!TryNormalizeRectangle(ref top, ref left, ref bottom, ref right))
        {
            return;
        }

        for (var y = top; y <= bottom; y++)
        {
            EraseCells(GetLiveLine(y).Cells, left, right + 1, selective);
        }
    }

    public void CopyRectangle(
        int top,
        int left,
        int bottom,
        int right,
        int destinationTop,
        int destinationLeft)
    {
        if (!TryNormalizeRectangle(ref top, ref left, ref bottom, ref right) ||
            destinationTop >= Rows ||
            destinationLeft >= Columns)
        {
            return;
        }

        destinationTop = Math.Max(0, destinationTop);
        destinationLeft = Math.Max(0, destinationLeft);
        var height = Math.Min(bottom - top + 1, Rows - destinationTop);
        var width = Math.Min(right - left + 1, Columns - destinationLeft);
        var copy = new Cell[height, width];
        for (var y = 0; y < height; y++)
        {
            var source = GetLiveLine(top + y).Cells;
            for (var x = 0; x < width; x++)
            {
                copy[y, x] = source[left + x];
            }

            if (copy[y, 0].IsWideContinuation)
            {
                copy[y, 0] = BlankPreservingAttributes(copy[y, 0]);
            }

            if (copy[y, width - 1].DisplayWidth == 2)
            {
                copy[y, width - 1] = BlankPreservingAttributes(copy[y, width - 1]);
            }
        }

        for (var y = 0; y < height; y++)
        {
            var destination = GetLiveLine(destinationTop + y).Cells;
            ClearGlyphAt(destination, destinationLeft);
            ClearGlyphAt(destination, destinationLeft + width - 1);
            for (var x = 0; x < width; x++)
            {
                destination[destinationLeft + x] = copy[y, x];
            }

            RepairWideCells(destination);
        }
    }

    public void ChangeAttributesRectangle(
        int top,
        int left,
        int bottom,
        int right,
        ReadOnlySpan<int> attributes,
        bool reverse,
        bool rectangular)
    {
        if (!TryNormalizeRectangle(ref top, ref left, ref bottom, ref right))
        {
            return;
        }

        if (!rectangular && top != bottom)
        {
            if (right <= left)
            {
                return;
            }

            ChangeAttributesArea(top, left, top, Columns - 1, attributes, reverse);
            if (bottom - top > 1)
            {
                ChangeAttributesArea(top + 1, 0, bottom - 1, Columns - 1, attributes, reverse);
            }

            ChangeAttributesArea(bottom, 0, bottom, right, attributes, reverse);
            return;
        }

        ChangeAttributesArea(top, left, bottom, right, attributes, reverse);
    }

    public ushort ChecksumRectangle(int top, int left, int bottom, int right)
    {
        if (!TryNormalizeRectangle(ref top, ref left, ref bottom, ref right))
        {
            return 0;
        }

        ushort checksum = 0;
        for (var y = top; y <= bottom; y++)
        {
            var row = GetLiveLine(y).Cells;
            for (var x = left; x <= right; x++)
            {
                var cell = row[x];
                if (!cell.IsWideContinuation)
                {
                    checksum = SubtractChecksum(checksum, cell.Rune.Value == 0x2426 ? 0x1B : cell.Rune.Value);
                    if (cell.CombiningCharacters is { } combining)
                    {
                        foreach (var rune in combining.EnumerateRunes())
                        {
                            checksum = SubtractChecksum(checksum, rune.Value == 0x2426 ? 0x1B : rune.Value);
                        }
                    }
                }

                checksum = SubtractChecksum(checksum, cell.IsProtected ? 0x04 : 0);
                checksum = SubtractChecksum(checksum, (cell.Attributes.Flags & CellFlags.Invisible) != 0 ? 0x08 : 0);
                checksum = SubtractChecksum(checksum, (cell.Attributes.Flags & CellFlags.Underline) != 0 ? 0x10 : 0);
                checksum = SubtractChecksum(checksum, (cell.Attributes.Flags & CellFlags.Inverse) != 0 ? 0x20 : 0);
                checksum = SubtractChecksum(checksum, (cell.Attributes.Flags & CellFlags.Blink) != 0 ? 0x40 : 0);
                checksum = SubtractChecksum(checksum, (cell.Attributes.Flags & CellFlags.Bold) != 0 ? 0x80 : 0);
                checksum = SubtractChecksum(checksum, LegacyColorIndex(cell.Attributes.Foreground, 7) << 4);
                checksum = SubtractChecksum(checksum, LegacyColorIndex(cell.Attributes.Background, 0));
            }
        }

        return checksum;
    }

    private static ushort SubtractChecksum(ushort checksum, int value) =>
        unchecked((ushort)(checksum - value));

    private static int LegacyColorIndex(TermColor color, int defaultIndex) =>
        color.Kind == ColorKind.Indexed && color.Index < 16 ? color.Index : defaultIndex;

    public void InsertLines(int count)
    {
        if (CursorY < ScrollTop || CursorY > ScrollBottom)
        {
            return;
        }

        count = Math.Min(Math.Max(1, count), ScrollBottom - CursorY + 1);
        var start = ViewportStart + CursorY;
        var bottom = ViewportStart + ScrollBottom;
        for (var y = bottom; y >= start + count; y--)
        {
            _lines[y] = _lines[y - count];
        }

        for (var y = start; y < start + count; y++)
        {
            _lines[y] = NewBlankLine(CurrentAttributes);
        }
    }

    public void DeleteLines(int count)
    {
        if (CursorY < ScrollTop || CursorY > ScrollBottom)
        {
            return;
        }

        count = Math.Min(Math.Max(1, count), ScrollBottom - CursorY + 1);
        var start = ViewportStart + CursorY;
        var bottom = ViewportStart + ScrollBottom;
        for (var y = start; y <= bottom - count; y++)
        {
            _lines[y] = _lines[y + count];
        }

        for (var y = bottom - count + 1; y <= bottom; y++)
        {
            _lines[y] = NewBlankLine(CurrentAttributes);
        }
    }

    public void ShiftColumns(int count)
    {
        if (count == 0)
        {
            return;
        }

        var left = LeftBound;
        var right = LeftRightMargins ? MarginRight : Columns - 1;
        var width = right - left + 1;
        var shift = Math.Clamp(Math.Abs(count), 1, width);
        for (var y = ScrollTop; y <= ScrollBottom; y++)
        {
            var row = GetLiveLine(y).Cells;
            if (count < 0)
            {
                row.CopyTo(left + shift, row, left, width - shift);
                for (var x = right - shift + 1; x <= right; x++)
                {
                    row[x] = BlankWith(CurrentAttributes);
                }
            }
            else
            {
                row.CopyTo(left, row, left + shift, width - shift);
                for (var x = left; x < left + shift; x++)
                {
                    row[x] = BlankWith(CurrentAttributes);
                }
            }

            RepairWideCells(row);
        }
    }

    public void InsertCharacters(int count)
    {
        var row = GetLiveLine(CursorY).Cells;
        count = Math.Min(Math.Max(1, count), Columns - CursorX);
        row.CopyTo(CursorX, row, CursorX + count, Columns - CursorX - count);
        for (var x = CursorX; x < CursorX + count; x++)
        {
            row[x] = BlankWith(CurrentAttributes);
        }

        RepairWideCells(row);
    }

    public void DeleteCharacters(int count)
    {
        var row = GetLiveLine(CursorY).Cells;
        count = Math.Min(Math.Max(1, count), Columns - CursorX);
        row.CopyTo(CursorX + count, row, CursorX, Columns - CursorX - count);
        for (var x = Columns - count; x < Columns; x++)
        {
            row[x] = BlankWith(CurrentAttributes);
        }

        RepairWideCells(row);
    }

    public void EraseCharacters(int count)
    {
        var row = GetLiveLine(CursorY).Cells;
        EraseCells(row, CursorX, Math.Min(Columns, CursorX + Math.Max(1, count)));
    }

    public void ScrollUp(int count)
    {
        count = Math.Min(Math.Max(1, count), ScrollBottom - ScrollTop + 1);
        for (var i = 0; i < count; i++)
        {
            if (ScrollTop == 0 && ScrollBottom == Rows - 1 && HasHistory)
            {
                if (_lines.Count == Rows + _historySize)
                {
                    CoordinateVersion++;
                }

                // Once history is full the oldest line is evicted anyway; reuse it so
                // steady-state scrolling allocates nothing.
                _lines.AddLast(_lines.Count == _lines.Capacity
                    ? RecycleLine(_lines.RemoveFirst(), CurrentAttributes)
                    : NewBlankLine(CurrentAttributes));
                ScrollOffset = 0;
            }
            else
            {
                var bottom = ViewportStart + ScrollBottom;
                var scrolledOut = _lines.RotateDown(ViewportStart + ScrollTop, bottom);
                var recycled = RecycleLine(scrolledOut, CurrentAttributes);
                if (!ReferenceEquals(recycled, scrolledOut))
                {
                    _lines[bottom] = recycled;
                }
            }
        }
    }

    public void ScrollDown(int count)
    {
        count = Math.Min(Math.Max(1, count), ScrollBottom - ScrollTop + 1);
        for (var i = 0; i < count; i++)
        {
            var top = ViewportStart + ScrollTop;
            var scrolledOut = _lines.RotateUp(top, ViewportStart + ScrollBottom);
            var recycled = RecycleLine(scrolledOut, CurrentAttributes);
            if (!ReferenceEquals(recycled, scrolledOut))
            {
                _lines[top] = recycled;
            }
        }
    }

    public void SaveCursor()
    {
        SavedCursorX = CursorX;
        SavedCursorY = CursorY;
        SavedAttributes = CurrentAttributes;
        SavedProtection = CurrentProtection;
    }

    public void RestoreCursor()
    {
        CursorY = Math.Clamp(SavedCursorY, 0, Rows - 1);
        CursorX = Math.Clamp(SavedCursorX, 0, EffectiveColumns(GetLiveLine(CursorY)) - 1);
        CurrentAttributes = SavedAttributes;
        CurrentProtection = SavedProtection;
        WrapPending = false;
    }

    public void Reset(bool keepHistory)
    {
        CurrentAttributes = CellAttributes.Default;
        CurrentProtection = false;
        SavedAttributes = CellAttributes.Default;
        SavedProtection = false;
        CurrentHyperlinkUri = null;
        SetShellIntegration(ShellIntegrationKind.None);
        _activeMark = null;
        _activeMarkHasOutput = false;
        _pendingPrepend = null;
        CursorX = 0;
        CursorY = 0;
        WrapPending = false;
        OriginMode = false;
        ReverseWraparound = false;
        ScrollTop = 0;
        ScrollBottom = Rows - 1;
        LeftRightMargins = false;
        MarginLeft = 0;
        MarginRight = Columns - 1;
        ScrollOffset = 0;
        _tabStops = CreateDefaultTabStops(Columns);
        if (!keepHistory)
        {
            _lines.Clear();
        }

        EnsureViewportLines();
        EraseLineRange(0, Rows - 1);
        for (var y = 0; y < Rows; y++)
        {
            GetLiveLine(y).Rendition = LineRendition.SingleWidth;
        }
    }

    public string GetText(int startX, int startY, int endX, int endY)
    {
        Normalize(ref startX, ref startY, ref endX, ref endY);
        var sb = new StringBuilder();
        for (var y = startY; y <= endY; y++)
        {
            var row = GetRow(y);
            var from = y == startY ? startX : 0;
            var to = y == endY ? endX : Columns - 1;
            var last = from - 1;
            for (var x = from; x <= to && x < Columns; x++)
            {
                if (!row[x].IsWideContinuation && !row[x].IsBlank)
                {
                    last = x;
                }
            }

            for (var x = from; x <= last && x < Columns; x++)
            {
                if (!row[x].IsWideContinuation)
                {
                    sb.Append(row[x].Text);
                }
            }

            if (y != endY && !GetVisibleLine(y).Wrapped)
            {
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    public string GetVisibleText() => GetText(0, 0, Columns - 1, Rows - 1);

    public void StartPrompt()
    {
        if (_activeMark is not null)
        {
            SetShellIntegration(ShellIntegrationKind.Prompt);
            return;
        }

        var line = GetLiveLine(CursorY);
        _activeMark = new ShellMark(CursorX);
        line.Marks.Add(_activeMark);
        _activeMarkHasOutput = false;
        SetShellIntegration(ShellIntegrationKind.Prompt);
    }

    public void StartCommand()
    {
        EnsureShellMark();
        SetShellIntegration(ShellIntegrationKind.Command);
    }

    public void StartOutput()
    {
        EnsureShellMark();
        _activeMarkHasOutput = true;
        SetShellIntegration(ShellIntegrationKind.Output);
    }

    public void EndCommand(uint? exitCode)
    {
        SetShellIntegration(ShellIntegrationKind.None);
        if (_activeMark is { } mark)
        {
            mark.ExitCode = exitCode;
        }

        _activeMark = null;
        _activeMarkHasOutput = false;
    }

    private void EnsureShellMark()
    {
        if (_activeMark is not null && !_activeMarkHasOutput)
        {
            return;
        }

        var line = GetLiveLine(CursorY);
        _activeMark = new ShellMark(CursorX);
        line.Marks.Add(_activeMark);
        _activeMarkHasOutput = false;
    }

    private BufferLine GetVisibleLine(int viewportY)
    {
        EnsureViewportLines();
        viewportY = Math.Clamp(viewportY, 0, Rows - 1);
        var index = Math.Clamp(ViewportStart - ScrollOffset + viewportY, 0, _lines.Count - 1);
        return _lines[index];
    }

    private BufferLine GetLiveLine(int viewportY)
    {
        EnsureViewportLines();
        viewportY = Math.Clamp(viewportY, 0, Rows - 1);
        return _lines[ViewportStart + viewportY];
    }

    private void EnsureViewportLines()
    {
        while (_lines.Count < Rows)
        {
            _lines.AddLast(NewBlankLine(CellAttributes.Default));
        }
    }

    private void TrimHistory()
    {
        if (_lines.Count > Rows)
        {
            CoordinateVersion++;
        }

        while (_lines.Count > Rows)
        {
            _lines.RemoveFirst();
        }

        ScrollOffset = 0;
    }

    private BufferLine NewBlankLine(CellAttributes attributes) =>
        new(new CellRow(Columns, attributes), ++_nextLogicalLineId);

    /// <summary>
    /// Blanks a line that just left the buffer (or a scroll region) and gives it a
    /// new logical identity, so image anchors and marks never resolve to it again.
    /// Snapshots clone cells, so no reader still observes the old content.
    /// </summary>
    private BufferLine RecycleLine(BufferLine line, CellAttributes attributes)
    {
        if (line.Cells.Length != Columns)
        {
            return NewBlankLine(attributes);
        }

        line.Cells.Reset(attributes);
        line.Wrapped = false;
        line.Rendition = LineRendition.SingleWidth;
        line.ClearMarks();
        line.LogicalLineId = ++_nextLogicalLineId;
        line.LogicalOffset = 0;
        return line;
    }

    private static BufferLine ResizeLineCells(BufferLine source, int columns)
    {
        var cells = new CellRow(columns, CellAttributes.Default);
        source.Cells.CopyTo(0, cells, 0, Math.Min(source.Cells.Length, columns));

        RepairWideCells(cells);
        var line = new BufferLine(cells, source.LogicalLineId)
        {
            Wrapped = source.Wrapped,
            Rendition = source.Rendition,
            LogicalOffset = source.LogicalOffset,
        };
        foreach (var mark in source.MarksOrEmpty)
        {
            if ((uint)mark.StartColumn < (uint)columns)
            {
                line.Marks.Add(mark);
            }
        }

        return line;
    }

    private void ContinueLogicalLine(BufferLine source)
    {
        var destination = GetLiveLine(CursorY);
        destination.LogicalLineId = source.LogicalLineId;
        destination.LogicalOffset = source.LogicalOffset + EffectiveColumns(source);
    }

    private int EffectiveColumns(BufferLine line) =>
        line.Rendition == LineRendition.SingleWidth ? Columns : Math.Max(1, Columns / 2);

    internal int LeftBound => LeftRightMargins ? MarginLeft : 0;

    private int RightBound(BufferLine line) =>
        LeftRightMargins
            ? Math.Min(MarginRight, EffectiveColumns(line) - 1)
            : EffectiveColumns(line) - 1;

    private static Cell BlankWith(CellAttributes attributes) => new()
    {
        Rune = new Rune(' '),
        Attributes = attributes,
        StoredWidth = 1,
    };

    private static Cell BlankPreservingAttributes(Cell cell)
    {
        cell.Rune = new Rune(' ');
        cell.IsWideContinuation = false;
        cell.StoredWidth = 1;
        cell.CombiningCharacters = null;
        cell.HyperlinkUri = null;
        cell.ShellIntegration = ShellIntegrationKind.None;
        return cell;
    }

    private void EraseLineRange(int fromY, int toY, bool selective = false)
    {
        for (var y = Math.Max(0, fromY); y <= Math.Min(toY, Rows - 1); y++)
        {
            EraseCells(GetLiveLine(y).Cells, 0, Columns, selective);
            GetLiveLine(y).Wrapped = false;
        }
    }

    private void EraseCells(CellRow row, int from, int to, bool selective = false)
    {
        from = Math.Clamp(from, 0, row.Length);
        to = Math.Clamp(to, 0, row.Length);
        if (selective)
        {
            for (var x = from; x < to; x++)
            {
                SelectivelyEraseGlyphAt(row, x);
            }

            return;
        }

        if (from < to)
        {
            ClearGlyphAt(row, from);
            ClearGlyphAt(row, to - 1);
        }

        if (from < to)
        {
            row.FillBlank(from, to - from, CurrentAttributes);
        }
    }

    private static void SelectivelyEraseGlyphAt(CellRow row, int x)
    {
        var first = row[x].IsWideContinuation ? x - 1 : x;
        var last = row[x].DisplayWidth == 2 ? x + 1 : x;
        if (first < 0 || last >= row.Length)
        {
            first = last = x;
        }

        if (row[first].IsProtected || row[last].IsProtected)
        {
            return;
        }

        for (var column = first; column <= last; column++)
        {
            var cell = row[column];
            cell.Rune = new Rune(' ');
            cell.IsProtected = false;
            cell.IsWideContinuation = false;
            cell.StoredWidth = 1;
            cell.CombiningCharacters = null;
            cell.HyperlinkUri = null;
            cell.ShellIntegration = ShellIntegrationKind.None;
            row[column] = cell;
        }
    }

    private bool TryNormalizeRectangle(ref int top, ref int left, ref int bottom, ref int right)
    {
        if (bottom < top || right < left || bottom < 0 || right < 0 || top >= Rows || left >= Columns)
        {
            return false;
        }

        top = Math.Clamp(top, 0, Rows - 1);
        left = Math.Clamp(left, 0, Columns - 1);
        bottom = Math.Clamp(bottom, 0, Rows - 1);
        right = Math.Clamp(right, 0, Columns - 1);
        return true;
    }

    private void ChangeAttributesArea(
        int top,
        int left,
        int bottom,
        int right,
        ReadOnlySpan<int> attributes,
        bool reverse)
    {
        for (var y = top; y <= bottom; y++)
        {
            var row = GetLiveLine(y).Cells;
            for (var x = left; x <= right; x++)
            {
                var cell = row[x];
                ApplyRectangularAttributes(ref cell.Attributes, attributes, reverse);
                row[x] = cell;
            }
        }
    }

    private static void ApplyRectangularAttributes(
        ref CellAttributes attributes,
        ReadOnlySpan<int> parameters,
        bool reverse)
    {
        if (reverse)
        {
            var mask = CellFlags.None;
            for (var index = 0; index < parameters.Length;)
            {
                var parameter = Parameter(parameters, index);
                if (parameter == 0)
                {
                    mask ^= CellFlags.Bold | CellFlags.Faint | CellFlags.Italic |
                        CellFlags.Underline | CellFlags.Blink | CellFlags.Inverse |
                        CellFlags.Invisible | CellFlags.Strikethrough;
                    index++;
                    continue;
                }

                var sample = CellAttributes.Default;
                index += ApplyRectangularSgr(ref sample, parameters, index);
                mask ^= sample.Flags;
            }

            attributes.Flags ^= mask;
            return;
        }

        for (var index = 0; index < parameters.Length;)
        {
            index += ApplyRectangularSgr(ref attributes, parameters, index);
        }
    }

    private static int ApplyRectangularSgr(
        ref CellAttributes attributes,
        ReadOnlySpan<int> parameters,
        int index)
    {
        var value = Parameter(parameters, index);
        switch (value)
        {
            case 0:
                attributes = CellAttributes.Default;
                break;
            case 1:
                attributes.Flags = (attributes.Flags | CellFlags.Bold) & ~CellFlags.Faint;
                break;
            case 2:
                attributes.Flags |= CellFlags.Faint;
                break;
            case 3:
                attributes.Flags |= CellFlags.Italic;
                break;
            case 4:
            case 21:
                attributes.Flags |= CellFlags.Underline;
                break;
            case 5:
            case 6:
                attributes.Flags |= CellFlags.Blink;
                break;
            case 7:
                attributes.Flags |= CellFlags.Inverse;
                break;
            case 8:
                attributes.Flags |= CellFlags.Invisible;
                break;
            case 9:
                attributes.Flags |= CellFlags.Strikethrough;
                break;
            case 22:
                attributes.Flags &= ~(CellFlags.Bold | CellFlags.Faint);
                break;
            case 23:
                attributes.Flags &= ~CellFlags.Italic;
                break;
            case 24:
                attributes.Flags &= ~CellFlags.Underline;
                break;
            case 25:
                attributes.Flags &= ~CellFlags.Blink;
                break;
            case 27:
                attributes.Flags &= ~CellFlags.Inverse;
                break;
            case 28:
                attributes.Flags &= ~CellFlags.Invisible;
                break;
            case 29:
                attributes.Flags &= ~CellFlags.Strikethrough;
                break;
            case 38:
            case 48:
                return ApplyRectangularExtendedColor(ref attributes, parameters, index, value == 38);
            case 39:
                attributes.Foreground = TermColor.Default;
                break;
            case 49:
                attributes.Background = TermColor.Default;
                break;
            case >= 30 and <= 37:
                attributes.Foreground = TermColor.FromIndex(value - 30);
                break;
            case >= 40 and <= 47:
                attributes.Background = TermColor.FromIndex(value - 40);
                break;
            case >= 90 and <= 97:
                attributes.Foreground = TermColor.FromIndex(value - 90 + 8);
                break;
            case >= 100 and <= 107:
                attributes.Background = TermColor.FromIndex(value - 100 + 8);
                break;
        }

        return 1;
    }

    private static int ApplyRectangularExtendedColor(
        ref CellAttributes attributes,
        ReadOnlySpan<int> parameters,
        int index,
        bool foreground)
    {
        var mode = Parameter(parameters, index + 1);
        if (mode == 5 && index + 2 < parameters.Length)
        {
            SetRectangularColor(
                ref attributes,
                TermColor.FromIndex(Parameter(parameters, index + 2)),
                foreground);
            return 3;
        }

        if (mode == 2 && index + 4 < parameters.Length)
        {
            SetRectangularColor(
                ref attributes,
                TermColor.FromRgb(
                    (byte)Math.Clamp(Parameter(parameters, index + 2), 0, 255),
                    (byte)Math.Clamp(Parameter(parameters, index + 3), 0, 255),
                    (byte)Math.Clamp(Parameter(parameters, index + 4), 0, 255)),
                foreground);
            return 5;
        }

        return 1;
    }

    private static void SetRectangularColor(
        ref CellAttributes attributes,
        TermColor color,
        bool foreground)
    {
        if (foreground)
        {
            attributes.Foreground = color;
        }
        else
        {
            attributes.Background = color;
        }
    }

    private static int Parameter(ReadOnlySpan<int> parameters, int index) =>
        (uint)index < (uint)parameters.Length && parameters[index] >= 0 ? parameters[index] : 0;

    private void AppendCombining(Rune rune)
    {
        var y = CursorY;
        var x = CursorX - 1;
        if (WrapPending)
        {
            x = CursorX;
        }
        else if (x < 0 && y > 0 && GetLiveLine(y - 1).Wrapped)
        {
            y--;
            x = Columns - 1;
        }

        if (x < 0)
        {
            return;
        }

        var row = GetLiveLine(y).Cells;
        if (row[x].IsWideContinuation && x > 0)
        {
            x--;
        }

        var cell = row[x];
        if (cell.IsBlank)
        {
            return;
        }

        cell.CombiningCharacters = (cell.CombiningCharacters ?? string.Empty) + rune;
        row[x] = cell;
    }

    private bool TryAppendJoinedRune(Rune rune, out int advance)
    {
        advance = 0;
        var target = GetJoinTarget(rune);
        if (target is null)
        {
            return false;
        }

        var (row, x, desiredWidth, lineColumns) = target.Value;
        var previous = row[x];
        var previousWidth = previous.DisplayWidth;
        advance = Math.Max(0, desiredWidth - previousWidth);
        previous.CombiningCharacters = (previous.CombiningCharacters ?? string.Empty) + rune;
        row[x] = previous;
        if (desiredWidth > previousWidth && x + desiredWidth <= lineColumns)
        {
            ClearGlyphAt(row, x + 1);
            previous.StoredWidth = (byte)desiredWidth;
            row[x] = previous;
            row[x + 1] = new Cell
            {
                Rune = new Rune(' '),
                Attributes = previous.Attributes,
                IsWideContinuation = true,
                HyperlinkUri = previous.HyperlinkUri,
                ShellIntegration = previous.ShellIntegration,
            };

            var newCursor = CursorX + desiredWidth - previousWidth;
            if (newCursor >= lineColumns)
            {
                CursorX = lineColumns - 1;
                WrapPending = true;
            }
            else
            {
                CursorX = newCursor;
            }
        }

        return true;
    }

    private (CellRow Row, int Column, int DesiredWidth, int LineColumns)? GetJoinTarget(Rune rune)
    {
        var y = CursorY;
        var x = WrapPending ? CursorX : CursorX - 1;
        if (!WrapPending && x < 0 && y > 0 && GetLiveLine(y - 1).Wrapped)
        {
            y--;
            x = Columns - 1;
        }

        if (x < 0)
        {
            return null;
        }

        var row = GetLiveLine(y).Cells;
        if (row.IsWideContinuation(x) && x > 0)
        {
            x--;
        }

        // Most runes can only join a lone Prepend (GB9b); rule them out with
        // table loads before evaluating the rules one by one.
        if ((JoinClass(rune) & MayExtendPrevious) == 0 &&
            (!IsPrependScalar(row.RuneAt(x)) || row.CombiningAt(x) is not null))
        {
            return null;
        }

        // Read only the fields the rules need; whole cells are built for the
        // rare ZWJ and Indic checks that walk the combining text.
        if (row.IsBlank(x))
        {
            return null;
        }

        var previousRune = row.RuneAt(x);
        var previousCombining = row.CombiningAt(x);
        var joinedByZwj =
            previousCombining?.EndsWith('\u200D') == true &&
            IsExtendedPictographic(rune) &&
            HasExtendedPictographicBeforeZwj(previousRune, previousCombining);
        var emojiPresentationSelector =
            rune.Value == 0xFE0F &&
            IsExtendedPictographic(previousRune);
        var regionalPair =
            IsRegionalIndicator(previousRune) &&
            IsRegionalIndicator(rune) &&
            // GB12/13 count immediately adjacent RIs only. An intervening
            // Extend or ZWJ terminates the run, even if it joined the RI cell.
            previousCombining is null;
        var hangulSyllable = IsHangulContinuation(previousRune, previousCombining, rune);
        var spacingMark = Rune.GetUnicodeCategory(rune) == UnicodeCategory.SpacingCombiningMark;
        // A Prepend scalar at the front of a completed cell is no longer
        // pending: it applies only to the immediately following grapheme.
        var prepend = IsPrepend(previousRune) &&
            previousCombining is null;
        var indicConjunct = IsIndicConsonant(rune) && EndsWithIndicLinker(row[x]);
        if (!joinedByZwj &&
            !emojiPresentationSelector &&
            !regionalPair &&
            !hangulSyllable &&
            !spacingMark &&
            !prepend &&
            !indicConjunct)
        {
            return null;
        }

        var desiredWidth = joinedByZwj || emojiPresentationSelector || regionalPair
            ? 2
            : Math.Max(row.DisplayWidth(x), Math.Max(1, WcWidth.Width(rune)));
        var lineColumns = EffectiveColumns(GetLiveLine(y));
        return x + desiredWidth <= lineColumns ? (row, x, desiredWidth, lineColumns) : null;
    }

    private static bool IsRegionalIndicator(Rune rune) =>
        rune.Value is >= 0x1F1E6 and <= 0x1F1FF;

    // Walks the cell's text (base rune, then combining text ending in ZWJ)
    // backwards past the ZWJ and any Extend scalars, without building arrays.
    private static bool HasExtendedPictographicBeforeZwj(Rune baseRune, string combining)
    {
        var text = combining.AsSpan();
        Rune.DecodeLastFromUtf16(text, out _, out var consumed); // The final rune is ZWJ.
        text = text[..^consumed];
        while (!text.IsEmpty)
        {
            Rune.DecodeLastFromUtf16(text, out var rune, out consumed);
            if (!IsGraphemeExtend(rune))
            {
                return IsExtendedPictographic(rune);
            }

            text = text[..^consumed];
        }

        return !IsGraphemeExtend(baseRune) && IsExtendedPictographic(baseRune);
    }

    private static bool IsGraphemeExtend(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or
            UnicodeCategory.EnclosingMark ||
        rune.Value is 0x200C or >= 0x1F3FB and <= 0x1F3FF or
            >= 0xE0020 and <= 0xE007F;

    // Merged contiguous Extended_Pictographic ranges from Unicode 16.0.0
    // ucd/emoji/emoji-data.txt (SHA-256 F1365A5173EEE18E1F98B240CDC492E84A25F1CE7E0C9D1094EB29C41A22696A).
    // Unicode License V3 is adjacent to the GraphemeBreakTest fixture in Core.Tests.
    // Do not substitute
    // the whole emoji block: regional indicators and many symbols are not EP.
    private static bool IsExtendedPictographic(Rune rune) =>
        rune.Value is
            0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139 or
            >= 0x2194 and <= 0x2199 or >= 0x21A9 and <= 0x21AA or
            >= 0x231A and <= 0x231B or 0x2328 or 0x2388 or 0x23CF or
            >= 0x23E9 and <= 0x23F3 or >= 0x23F8 and <= 0x23FA or
            0x24C2 or >= 0x25AA and <= 0x25AB or 0x25B6 or 0x25C0 or
            >= 0x25FB and <= 0x25FE or
            >= 0x2600 and <= 0x2605 or >= 0x2607 and <= 0x2612 or
            >= 0x2614 and <= 0x2685 or >= 0x2690 and <= 0x2705 or
            >= 0x2708 and <= 0x2712 or 0x2714 or 0x2716 or 0x271D or
            0x2721 or 0x2728 or >= 0x2733 and <= 0x2734 or 0x2744 or
            0x2747 or 0x274C or 0x274E or >= 0x2753 and <= 0x2755 or
            0x2757 or >= 0x2763 and <= 0x2767 or
            >= 0x2795 and <= 0x2797 or 0x27A1 or 0x27B0 or 0x27BF or
            >= 0x2934 and <= 0x2935 or >= 0x2B05 and <= 0x2B07 or
            >= 0x2B1B and <= 0x2B1C or 0x2B50 or 0x2B55 or 0x3030 or
            0x303D or 0x3297 or 0x3299 or
            >= 0x1F000 and <= 0x1F0FF or
            >= 0x1F10D and <= 0x1F10F or 0x1F12F or
            >= 0x1F16C and <= 0x1F171 or
            >= 0x1F17E and <= 0x1F17F or 0x1F18E or
            >= 0x1F191 and <= 0x1F19A or
            >= 0x1F1AD and <= 0x1F1E5 or
            >= 0x1F201 and <= 0x1F20F or 0x1F21A or 0x1F22F or
            >= 0x1F232 and <= 0x1F23A or
            >= 0x1F23C and <= 0x1F23F or
            >= 0x1F249 and <= 0x1F3FA or
            >= 0x1F400 and <= 0x1F53D or
            >= 0x1F546 and <= 0x1F64F or
            >= 0x1F680 and <= 0x1F6FF or
            >= 0x1F774 and <= 0x1F77F or
            >= 0x1F7D5 and <= 0x1F7FF or
            >= 0x1F80C and <= 0x1F80F or
            >= 0x1F848 and <= 0x1F84F or
            >= 0x1F85A and <= 0x1F85F or
            >= 0x1F888 and <= 0x1F88F or
            >= 0x1F8AE and <= 0x1F8FF or
            >= 0x1F90C and <= 0x1F93A or
            >= 0x1F93C and <= 0x1F945 or
            >= 0x1F947 and <= 0x1FAFF or
            >= 0x1FC00 and <= 0x1FFFD;

    private enum HangulClass : byte
    {
        Other,
        L,
        V,
        T,
        Lv,
        Lvt,
    }

    private static bool IsHangulContinuation(Rune previousRune, string? previousCombining, Rune current)
    {
        // Every continuation needs a Hangul jamo or syllable next; check that
        // before walking the previous cell's combining text.
        var next = Hangul(current);
        if (next == HangulClass.Other)
        {
            return false;
        }

        var prior = Hangul(previousCombining is { Length: > 0 }
            ? previousCombining.EnumerateRunes().Last()
            : previousRune);
        return prior == HangulClass.L && next is HangulClass.L or HangulClass.V or HangulClass.Lv or HangulClass.Lvt ||
               prior is HangulClass.Lv or HangulClass.V && next is HangulClass.V or HangulClass.T ||
               prior is HangulClass.Lvt or HangulClass.T && next == HangulClass.T;
    }

    private static HangulClass Hangul(Rune rune)
    {
        var value = rune.Value;
        if (value is >= 0x1100 and <= 0x115F or >= 0xA960 and <= 0xA97C)
        {
            return HangulClass.L;
        }

        if (value is >= 0x1160 and <= 0x11A7 or >= 0xD7B0 and <= 0xD7C6)
        {
            return HangulClass.V;
        }

        if (value is >= 0x11A8 and <= 0x11FF or >= 0xD7CB and <= 0xD7FB)
        {
            return HangulClass.T;
        }

        if (value is >= 0xAC00 and <= 0xD7A3)
        {
            return (value - 0xAC00) % 28 == 0 ? HangulClass.Lv : HangulClass.Lvt;
        }

        return HangulClass.Other;
    }

    // Grapheme-join properties of every scalar in planes 0 and 1 (which hold the
    // emoji), precomputed from the rule predicates below so most printed runes
    // skip them with one table load.
    private const byte MayExtendPrevious = 1;
    private const byte PrependScalar = 2;
    private const byte ExtendsOnlyAfterZwj = 4;
    private static readonly byte[] JoinClasses = BuildJoinClasses();

    private static byte JoinClass(Rune rune) =>
        rune.Value < JoinClasses.Length ? JoinClasses[rune.Value] : ClassifyJoin(rune);

    private static bool IsPrependScalar(Rune rune) => (JoinClass(rune) & PrependScalar) != 0;

    private static byte[] BuildJoinClasses()
    {
        var classes = new byte[0x20000];
        for (var value = 0; value < classes.Length; value++)
        {
            if (value is < 0xD800 or > 0xDFFF)
            {
                classes[value] = ClassifyJoin(new Rune(value));
            }
        }

        return classes;
    }

    /// <summary>
    /// <see cref="MayExtendPrevious"/> marks the runes for which some rule in
    /// <see cref="GetJoinTarget"/> other than GB9b (a lone Prepend before them)
    /// can hold: a ZWJ target, VS16, a regional indicator, Hangul, a spacing mark
    /// or an Indic consonant.
    /// </summary>
    private static byte ClassifyJoin(Rune rune)
    {
        byte joinClass = 0;
        if (IsExtendedPictographic(rune) ||
            rune.Value == 0xFE0F ||
            IsRegionalIndicator(rune) ||
            Hangul(rune) != HangulClass.Other ||
            Rune.GetUnicodeCategory(rune) == UnicodeCategory.SpacingCombiningMark ||
            IsIndicConsonant(rune))
        {
            joinClass |= MayExtendPrevious;
        }

        if (IsPrepend(rune))
        {
            joinClass |= PrependScalar;
        }

        // An Extended_Pictographic scalar with no other join property extends the
        // previous cell only through GB11 (combining text ending in ZWJ) or GB9b.
        if (IsExtendedPictographic(rune) &&
            rune.Value != 0xFE0F &&
            !IsRegionalIndicator(rune) &&
            Hangul(rune) == HangulClass.Other &&
            Rune.GetUnicodeCategory(rune) != UnicodeCategory.SpacingCombiningMark &&
            !IsIndicConsonant(rune))
        {
            joinClass |= ExtendsOnlyAfterZwj;
        }

        return joinClass;
    }

    private static bool IsPrepend(Rune rune) =>
        rune.Value is >= 0x0600 and <= 0x0605 or
            0x06DD or
            0x070F or
            0x0890 or 0x0891 or
            0x08E2 or
            0x0D4E or
            0x110BD or 0x110CD or
            >= 0x111C2 and <= 0x111C3 or
            0x1193F or 0x11941 or
            0x11A3A or
            >= 0x11A84 and <= 0x11A89 or
            0x11D46;

    // Unicode 16.0.0 DerivedCoreProperties.txt (SHA-256
    // 39D35161F2954497F69E08BDB9E701493F476A3D30222DE20028FEDA36C1DABD),
    // InCB=Consonant, with adjacent ranges merged. General_Category=Lo includes independent vowels,
    // which must not trigger the Indic conjunct rule.
    private static bool IsIndicConsonant(Rune rune) =>
        rune.Value is
            >= 0x0915 and <= 0x0939 or >= 0x0958 and <= 0x095F or
            >= 0x0978 and <= 0x097F or >= 0x0995 and <= 0x09A8 or
            >= 0x09AA and <= 0x09B0 or 0x09B2 or
            >= 0x09B6 and <= 0x09B9 or >= 0x09DC and <= 0x09DD or
            0x09DF or >= 0x09F0 and <= 0x09F1 or
            >= 0x0A95 and <= 0x0AA8 or >= 0x0AAA and <= 0x0AB0 or
            >= 0x0AB2 and <= 0x0AB3 or >= 0x0AB5 and <= 0x0AB9 or
            0x0AF9 or >= 0x0B15 and <= 0x0B28 or
            >= 0x0B2A and <= 0x0B30 or >= 0x0B32 and <= 0x0B33 or
            >= 0x0B35 and <= 0x0B39 or >= 0x0B5C and <= 0x0B5D or
            0x0B5F or 0x0B71 or >= 0x0C15 and <= 0x0C28 or
            >= 0x0C2A and <= 0x0C39 or >= 0x0C58 and <= 0x0C5A or
            >= 0x0D15 and <= 0x0D3A;

    private static bool EndsWithIndicLinker(Cell cell)
    {
        // GB9c requires a preceding InCB consonant, not merely a linker
        // appended to an arbitrary printable base such as ASCII 'a'.
        if (!IsIndicConsonant(cell.Rune) ||
            cell.CombiningCharacters is not { Length: > 0 } combining)
        {
            return false;
        }

        foreach (var rune in combining.EnumerateRunes().Reverse())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (IsIndicLinker(rune))
            {
                return true;
            }

            if (category is not (UnicodeCategory.NonSpacingMark or
                                 UnicodeCategory.Format) &&
                !IsIndicSpacingExtend(rune))
            {
                return false;
            }
        }

        return false;
    }

    // Unicode 16.0.0 InCB=Linker, not all script-specific virama signs.
    private static bool IsIndicLinker(Rune rune) =>
        rune.Value is 0x094D or 0x09CD or 0x0ACD or 0x0B4D or 0x0C4D or 0x0D4D;

    // InCB=Extend with General_Category=Mc in Unicode 16.0.0.
    // Most spacing marks are GB9a, but are NOT InCB extenders for GB9c.
    private static bool IsIndicSpacingExtend(Rune rune) =>
        rune.Value is 0x09BE or 0x09D7 or 0x0B3E or 0x0B57 or
            0x0BBE or 0x0BD7 or 0x0CC0 or 0x0CC2 or
            >= 0x0CC7 and <= 0x0CC8 or
            >= 0x0CCA and <= 0x0CCB or
            >= 0x0CD5 and <= 0x0CD6 or
            0x0D3E or 0x0D57 or 0x0DCF or 0x0DDF or 0x1715 or
            0x1734 or 0x1B35 or 0x1B3B or 0x1B3D or
            >= 0x1B43 and <= 0x1B44 or
            0x1BAA or >= 0x1BF2 and <= 0x1BF3 or
            >= 0x302E and <= 0x302F or
            0xA953 or 0xA9C0 or 0x111C0 or 0x11235 or
            0x1133E or 0x1134D or 0x11357 or
            0x113B8 or 0x113C2 or 0x113C5 or
            >= 0x113C7 and <= 0x113C9 or
            0x113CF or 0x114B0 or 0x114BD or 0x115AF or
            0x116B6 or 0x11930 or 0x1193D or 0x11F41 or
            >= 0x16FF0 and <= 0x16FF1 or
            >= 0x1D165 and <= 0x1D166 or
            >= 0x1D16D and <= 0x1D172;

    private static void ClearGlyphAt(CellRow row, int x)
    {
        // Without wide glyphs in the row there is nothing to split.
        if (!row.MayHoldWide || (uint)x >= (uint)row.Length)
        {
            return;
        }

        // FillBlank with default attributes stores exactly Cell.Blank.
        if (row.IsWideContinuation(x))
        {
            row.FillBlank(x, 1, CellAttributes.Default);
            if (x > 0)
            {
                row.FillBlank(x - 1, 1, CellAttributes.Default);
            }
        }
        else if (row.DisplayWidth(x) == 2)
        {
            row.FillBlank(x, 1, CellAttributes.Default);
            if (x + 1 < row.Length && row.IsWideContinuation(x + 1))
            {
                row.FillBlank(x + 1, 1, CellAttributes.Default);
            }
        }
    }

    private static void RepairWideCells(CellRow row)
    {
        for (var x = 0; x < row.Length; x++)
        {
            if (row[x].IsWideContinuation)
            {
                if (x == 0 || row[x - 1].DisplayWidth != 2)
                {
                    row[x] = Cell.Blank;
                }
            }
            else if (row[x].DisplayWidth == 2)
            {
                if (x + 1 >= row.Length)
                {
                    row[x] = Cell.Blank;
                }
                else
                {
                    row[x + 1] = new Cell
                    {
                        Rune = new Rune(' '),
                        Attributes = row[x].Attributes,
                        IsProtected = row[x].IsProtected,
                        IsWideContinuation = true,
                        StoredWidth = 0,
                        HyperlinkUri = row[x].HyperlinkUri,
                        ShellIntegration = row[x].ShellIntegration,
                    };
                    x++;
                }
            }
        }
    }

    private List<BufferLine> Reflow(
        IReadOnlyList<BufferLine> source,
        int oldColumns,
        int newColumns,
        IReadOnlyList<TerminalImageAnchor>? retainedAnchors,
        int cursorLine,
        int cursorX,
        int viewportTopLine,
        out int newCursorLine,
        out int newCursorX,
        out int newViewportTopLine)
    {
        var result = new List<BufferLine>();
        newCursorLine = 0;
        newCursorX = 0;
        newViewportTopLine = 0;
        var paragraphCells = new List<Cell>();
        var paragraphMarks = new List<(int Offset, ShellMark Mark)>();
        var cursorOffset = -1;
        var viewportTopOffset = -1;
        var paragraphRendition = LineRendition.SingleWidth;
        var paragraphLogicalLineId = 0L;
        var paragraphLogicalOffset = 0;

        for (var lineIndex = 0; lineIndex < source.Count; lineIndex++)
        {
            var line = source[lineIndex];
            if (paragraphCells.Count == 0)
            {
                paragraphRendition = line.Rendition;
                paragraphLogicalLineId = line.LogicalLineId;
                paragraphLogicalOffset = line.LogicalOffset;
            }

            var sourceColumns = line.Rendition == LineRendition.SingleWidth
                ? oldColumns
                : Math.Max(1, oldColumns / 2);
            var used = line.Wrapped ? sourceColumns : Math.Min(sourceColumns, LastContentColumn(line.Cells) + 1);
            if (retainedAnchors is not null)
            {
                for (var anchorIndex = 0; anchorIndex < retainedAnchors.Count; anchorIndex++)
                {
                    var anchor = retainedAnchors[anchorIndex];
                    if (anchor.LogicalLineId == line.LogicalLineId &&
                        anchor.LogicalOffset >= line.LogicalOffset &&
                        anchor.LogicalOffset < line.LogicalOffset + sourceColumns)
                    {
                        used = Math.Max(used, anchor.LogicalOffset - line.LogicalOffset + 1);
                    }
                }
            }
            foreach (var mark in line.MarksOrEmpty)
            {
                paragraphMarks.Add((DisplayWidth(paragraphCells) + mark.StartColumn, mark));
            }

            if (lineIndex == viewportTopLine)
            {
                viewportTopOffset = DisplayWidth(paragraphCells);
            }

            if (lineIndex == cursorLine)
            {
                used = Math.Max(used, cursorX);
                cursorOffset = DisplayWidth(paragraphCells) + Math.Min(cursorX, used);
            }

            for (var x = 0; x < used && x < line.Cells.Length; x++)
            {
                if (!line.Cells[x].IsWideContinuation)
                {
                    paragraphCells.Add(line.Cells[x]);
                }
            }

            if (!line.Wrapped ||
                lineIndex == source.Count - 1 ||
                source[lineIndex + 1].Rendition != line.Rendition)
            {
                var paragraphStart = result.Count;
                var destinationColumns = paragraphRendition == LineRendition.SingleWidth
                    ? newColumns
                    : Math.Max(1, newColumns / 2);
                EmitParagraph(
                    paragraphCells,
                    destinationColumns,
                    paragraphRendition,
                    result,
                    paragraphLogicalLineId,
                    paragraphLogicalOffset,
                    cursorOffset,
                    out var relativeLine,
                    out var relativeColumn);
                foreach (var (offset, mark) in paragraphMarks)
                {
                    var destination = paragraphStart + Math.Min(
                        offset / destinationColumns,
                        result.Count - paragraphStart - 1);
                    mark.StartColumn = offset % destinationColumns;
                    result[destination].Marks.Add(mark);
                }

                if (cursorOffset >= 0)
                {
                    newCursorLine = result.Count - relativeLine;
                    newCursorX = relativeColumn;
                    cursorOffset = -1;
                }

                if (viewportTopOffset >= 0)
                {
                    newViewportTopLine = paragraphStart + Math.Min(
                        viewportTopOffset / destinationColumns,
                        result.Count - paragraphStart - 1);
                    viewportTopOffset = -1;
                }

                paragraphCells.Clear();
                paragraphMarks.Clear();
            }
        }

        return result;
    }

    private void EmitParagraph(
        IReadOnlyList<Cell> cells,
        int columns,
        LineRendition rendition,
        List<BufferLine> output,
        long logicalLineId,
        int logicalOffset,
        int cursorOffset,
        out int linesAfterCursor,
        out int cursorColumn)
    {
        var line = NewBlankLine(CellAttributes.Default);
        line.Rendition = rendition;
        line.LogicalLineId = logicalLineId;
        line.LogicalOffset = logicalOffset;
        var x = 0;
        var cursorLineIndex = -1;
        cursorColumn = 0;
        var consumedWidth = 0;

        if (cursorOffset == 0)
        {
            cursorLineIndex = output.Count;
        }

        foreach (var cell in cells)
        {
            var width = cell.DisplayWidth;
            if (width > columns)
            {
                continue;
            }

            if (x + width > columns)
            {
                line.Wrapped = true;
                output.Add(line);
                line = NewBlankLine(CellAttributes.Default);
                line.Rendition = rendition;
                line.LogicalLineId = logicalLineId;
                line.LogicalOffset = logicalOffset + consumedWidth;
                x = 0;
            }

            if (cursorLineIndex < 0 && cursorOffset >= 0 && consumedWidth >= cursorOffset)
            {
                cursorLineIndex = output.Count;
                cursorColumn = x;
            }

            var stored = cell;
            stored.IsWideContinuation = false;
            line.Cells[x] = stored;
            if (width == 2)
            {
                line.Cells[x + 1] = new Cell
                {
                    Rune = new Rune(' '),
                    Attributes = cell.Attributes,
                    IsProtected = cell.IsProtected,
                    IsWideContinuation = true,
                    StoredWidth = 0,
                    HyperlinkUri = cell.HyperlinkUri,
                    ShellIntegration = cell.ShellIntegration,
                };
            }

            x += width;
            consumedWidth += width;
        }

        if (cursorLineIndex < 0 && cursorOffset >= 0)
        {
            cursorLineIndex = output.Count;
            cursorColumn = Math.Min(x, columns - 1);
        }

        output.Add(line);
        linesAfterCursor = cursorLineIndex < 0 ? 0 : output.Count - cursorLineIndex;
    }

    private static int LastTextLine(
        IReadOnlyList<BufferLine> lines,
        IReadOnlyList<TerminalImageAnchor>? retainedAnchors)
    {
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            var line = lines[index];
            for (var x = 0; x < line.Cells.Length; x++)
            {
                var cell = line.Cells[x];
                // Match conhost: only text counts, so rows that were merely erased
                // with a colored background are trimmed like any other blank row.
                if (!cell.IsWideContinuation &&
                    (cell.Rune.Value != ' ' || !string.IsNullOrEmpty(cell.CombiningCharacters)))
                {
                    return index;
                }
            }

            if (retainedAnchors is not null)
            {
                foreach (var anchor in retainedAnchors)
                {
                    if (anchor.LogicalLineId == line.LogicalLineId)
                    {
                        return index;
                    }
                }
            }
        }

        return -1;
    }

    private static int LastContentColumn(CellRow cells)
    {
        for (var x = cells.Length - 1; x >= 0; x--)
        {
            if (!cells[x].IsBlank && !cells[x].IsWideContinuation)
            {
                return Math.Min(cells.Length - 1, x + cells[x].DisplayWidth - 1);
            }
        }

        return -1;
    }

    private static int DisplayWidth(IReadOnlyList<Cell> cells)
    {
        var width = 0;
        for (var i = 0; i < cells.Count; i++)
        {
            width += cells[i].DisplayWidth;
        }

        return width;
    }

    private static bool[] CreateDefaultTabStops(int columns)
    {
        var stops = new bool[columns];
        for (var x = 8; x < columns; x += 8)
        {
            stops[x] = true;
        }

        return stops;
    }

    private static bool[] ResizeTabStops(bool[] source, int columns)
    {
        var stops = CreateDefaultTabStops(columns);
        Array.Copy(source, stops, Math.Min(source.Length, stops.Length));
        return stops;
    }

    private static void Normalize(ref int x1, ref int y1, ref int x2, ref int y2)
    {
        if (y1 > y2 || (y1 == y2 && x1 > x2))
        {
            (x1, x2) = (x2, x1);
            (y1, y2) = (y2, y1);
        }
    }
}
