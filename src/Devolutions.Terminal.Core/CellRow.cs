using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace Devolutions.Terminal.Core;

/// <summary>
/// The 16-byte, reference-free storage form of a <see cref="Cell"/>. Text,
/// width, flags and both colors are packed losslessly; combining text and
/// hyperlink URIs live in the owning <see cref="CellRow"/>'s side arrays, so
/// scrollback holds no GC references per cell and filling or copying cells is a
/// plain memory operation without write barriers.
/// </summary>
internal readonly struct PackedCell
{
    // _meta: rune (21 bits) | stored width (3) | wide continuation (1) |
    // protected (1) | shell integration (2) | foreground kind (2) | background kind (2).
    private const int WidthShift = 21;
    private const int WideShift = 24;
    private const int ProtectedShift = 25;
    private const int ShellShift = 26;
    private const int ForegroundKindShift = 28;
    private const int BackgroundKindShift = 30;
    private const uint RuneMask = 0x1F_FFFF;

    private readonly uint _meta;
    private readonly uint _foreground;
    private readonly uint _background;
    private readonly ushort _flags;

    private PackedCell(uint meta, uint foreground, uint background, ushort flags)
    {
        _meta = meta;
        _foreground = foreground;
        _background = background;
        _flags = flags;
    }

    public bool IsWideContinuation => ((_meta >> WideShift) & 1) != 0;

    public Rune Rune => Unsafe.BitCast<uint, Rune>(_meta & RuneMask);

    public byte StoredWidth => (byte)((_meta >> WidthShift) & 7);

    /// <summary>Matches <see cref="Cell.DisplayWidth"/>.</summary>
    public int DisplayWidth =>
        IsWideContinuation
            ? 0
            : StoredWidth == 0
                ? Math.Max(1, WcWidth.Width(Rune))
                : StoredWidth;

    /// <summary>
    /// Matches <see cref="Cell.IsBlank"/> except for combining text, which lives
    /// in the row's side array.
    /// </summary>
    public bool IsBlankIgnoringCombining =>
        // Rune is a space; not a continuation, not protected; both colors default.
        (_meta & (RuneMask | (1u << WideShift) | (1u << ProtectedShift) | (3u << ForegroundKindShift) | (3u << BackgroundKindShift))) == ' ' &&
        _flags == 0;

    public static PackedCell Blank(CellAttributes attributes) =>
        From(new Rune(' '), attributes, isProtected: false, isWideContinuation: false, storedWidth: 1, ShellIntegrationKind.None);

    public static PackedCell From(in Cell cell) =>
        From(cell.Rune, cell.Attributes, cell.IsProtected, cell.IsWideContinuation, cell.StoredWidth, cell.ShellIntegration);

    public static PackedCell From(
        Rune rune,
        CellAttributes attributes,
        bool isProtected,
        bool isWideContinuation,
        byte storedWidth,
        ShellIntegrationKind shellIntegration)
    {
        var meta = (uint)rune.Value |
                   (Math.Min((uint)storedWidth, 7u) << WidthShift) |
                   (isWideContinuation ? 1u << WideShift : 0) |
                   (isProtected ? 1u << ProtectedShift : 0) |
                   (((uint)shellIntegration & 3) << ShellShift) |
                   (((uint)attributes.Foreground.Kind & 3) << ForegroundKindShift) |
                   (((uint)attributes.Background.Kind & 3) << BackgroundKindShift);
        return new PackedCell(
            meta,
            PackColor(attributes.Foreground),
            PackColor(attributes.Background),
            (ushort)attributes.Flags);
    }

    public PackedCell WithRune(Rune rune) =>
        new((_meta & ~RuneMask) | (uint)rune.Value, _foreground, _background, _flags);

    /// <summary>This cell's 16 bytes with the rune cleared, for <see cref="StoreWithRune"/>.</summary>
    public Vector128<ulong> RuneFreeBits => Unsafe.BitCast<PackedCell, Vector128<ulong>>(WithRune(default));

    /// <summary>
    /// Stores the cell whose <see cref="RuneFreeBits"/> are given with
    /// <paramref name="rune"/> as its rune: one OR and one 16-byte write instead of
    /// a write per field.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreWithRune(ref PackedCell destination, Vector128<ulong> runeFreeBits, int rune)
    {
        if (!BitConverter.IsLittleEndian)
        {
            destination = Unsafe.BitCast<Vector128<ulong>, PackedCell>(runeFreeBits).WithRune(Unsafe.BitCast<int, Rune>(rune));
            return;
        }

        // On little-endian hardware the rune bits are the low bits of the first word.
        (runeFreeBits | Vector128.CreateScalar((ulong)(uint)rune)).StoreUnsafe(ref Unsafe.As<PackedCell, ulong>(ref destination));
    }

    public PackedCell WithWidth(byte storedWidth, bool isWideContinuation) =>
        new(
            (_meta & ~((7u << WidthShift) | (1u << WideShift))) |
            ((uint)storedWidth << WidthShift) |
            (isWideContinuation ? 1u << WideShift : 0),
            _foreground,
            _background,
            _flags);

    public bool SameBits(PackedCell other) =>
        _meta == other._meta &&
        _foreground == other._foreground &&
        _background == other._background &&
        _flags == other._flags;

    /// <summary>
    /// Stores <paramref name="template"/> once per byte with that byte as its rune,
    /// as two 64-bit writes per cell: on little-endian hardware the first word holds
    /// the rune bits and the foreground, the second the background and flags.
    /// </summary>
    public static void FillAscii(Span<PackedCell> destination, ReadOnlySpan<byte> text, PackedCell template)
    {
        if (!BitConverter.IsLittleEndian)
        {
            for (var cell = 0; cell < destination.Length; cell++)
            {
                destination[cell] = template.WithRune(new Rune(text[cell]));
            }

            return;
        }

        ref var words = ref Unsafe.As<PackedCell, ulong>(ref System.Runtime.InteropServices.MemoryMarshal.GetReference(destination));
        ref var templateWords = ref Unsafe.As<PackedCell, ulong>(ref template);
        var low = templateWords & ~(ulong)RuneMask;
        var high = Unsafe.Add(ref templateWords, 1);
        var index = 0;
        if (Avx2.IsSupported)
        {
            // Four cells per iteration: widen four bytes to four 64-bit lanes, add
            // the template's rune-free low word, then interleave with the constant
            // high word into two 32-byte stores.
            ref var bytes = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(text);
            var lows = Vector256.Create(low);
            var highs = Vector256.Create(high);
            for (; index + 4 <= destination.Length; index += 4)
            {
                var four = Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref bytes, index))).AsByte();
                var cellLows = Avx2.Or(Avx2.ConvertToVector256Int64(four).AsUInt64(), lows);
                var even = Avx2.UnpackLow(cellLows, highs);
                var odd = Avx2.UnpackHigh(cellLows, highs);
                Avx2.Permute2x128(even, odd, 0x20).StoreUnsafe(ref words, (nuint)(2 * index));
                Avx2.Permute2x128(even, odd, 0x31).StoreUnsafe(ref words, (nuint)((2 * index) + 4));
            }
        }

        for (; index < destination.Length; index++)
        {
            Unsafe.Add(ref words, 2 * index) = low | text[index];
            Unsafe.Add(ref words, (2 * index) + 1) = high;
        }
    }

    public Cell ToCell(string? combining, string? hyperlink) => new()
    {
        Rune = Unsafe.BitCast<uint, Rune>(_meta & RuneMask),
        Attributes = new CellAttributes
        {
            Foreground = UnpackColor((ColorKind)((_meta >> ForegroundKindShift) & 3), _foreground),
            Background = UnpackColor((ColorKind)((_meta >> BackgroundKindShift) & 3), _background),
            Flags = (CellFlags)_flags,
        },
        IsProtected = ((_meta >> ProtectedShift) & 1) != 0,
        IsWideContinuation = IsWideContinuation,
        StoredWidth = (byte)((_meta >> WidthShift) & 7),
        CombiningCharacters = combining,
        HyperlinkUri = hyperlink,
        ShellIntegration = (ShellIntegrationKind)((_meta >> ShellShift) & 3),
    };

    private static uint PackColor(TermColor color) =>
        color.Index | ((uint)color.R << 8) | ((uint)color.G << 16) | ((uint)color.B << 24);

    private static TermColor UnpackColor(ColorKind kind, uint value) =>
        new(kind, (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24));
}

/// <summary>
/// One line of packed cells. The indexer converts to and from the public
/// <see cref="Cell"/> so buffer code keeps its value semantics; hot paths use the
/// bulk members. Side arrays exist only while some cell of the line carries
/// combining text or a hyperlink, and are bounded by the line width.
/// </summary>
/// <remarks>
/// Only cells in <c>[_lo, _hi)</c> are stored; every other cell is logically
/// <c>_blank</c> with no side entries, whatever the array still holds. Blanking a
/// recycled scrollback line, the coldest memory in the buffer, is therefore O(1),
/// and writes touch only the cells they store plus any gap between them and the
/// stored range, which is filled to keep that range contiguous.
/// </remarks>
internal sealed class CellRow
{
    private readonly PackedCell[] _cells;
    private string?[]? _combining;
    private string?[]? _hyperlinks;
    private PackedCell _blank;
    private int _lo;
    private int _hi;
    // Conservative: false guarantees no cell is a wide glyph or its continuation,
    // so writes cannot split one and need no clearing around them.
    private bool _mayHoldWide;

    public CellRow(int columns, CellAttributes attributes)
    {
        _cells = GC.AllocateUninitializedArray<PackedCell>(columns);
        _blank = PackedCell.Blank(attributes);
    }

    private CellRow(PackedCell[] cells)
    {
        _cells = cells;
    }

    public int Length => _cells.Length;

    public Cell this[int x]
    {
        get => CellAt(x).ToCell(_combining?[x], _hyperlinks?[x]);
        set
        {
            PrepareWrite(x, 1);
            var packed = PackedCell.From(value);
            _cells[x] = packed;
            NoteStored(packed);
            SetSide(ref _combining, x, value.CombiningCharacters);
            SetSide(ref _hyperlinks, x, value.HyperlinkUri);
        }
    }

    /// <summary>Equivalent to the indexer storing a cell with these fields, without building one.</summary>
    public void Store(
        int x,
        Rune rune,
        CellAttributes attributes,
        bool isProtected,
        bool isWideContinuation,
        byte storedWidth,
        string? combining,
        string? hyperlink,
        ShellIntegrationKind shellIntegration)
    {
        PrepareWrite(x, 1);
        var packed = PackedCell.From(rune, attributes, isProtected, isWideContinuation, storedWidth, shellIntegration);
        _cells[x] = packed;
        NoteStored(packed);
        SetSide(ref _combining, x, combining);
        SetSide(ref _hyperlinks, x, hyperlink);
    }

    /// <summary>
    /// Stores runes of the given widths (1 or 2) from <paramref name="start"/> to
    /// <paramref name="end"/>: each narrow rune as <paramref name="narrow"/>, each
    /// wide one as <paramref name="wide"/> followed by <paramref name="continuation"/>,
    /// all without combining text and with <paramref name="hyperlink"/>.
    /// </summary>
    public void WriteRunes(
        int start,
        int end,
        ReadOnlySpan<Rune> runes,
        ReadOnlySpan<byte> widths,
        PackedCell narrow,
        PackedCell wide,
        PackedCell continuation,
        string? hyperlink)
    {
        PrepareWrite(start, end - start);
        var cells = _cells.AsSpan(start, end - start);
        var narrowBits = narrow.RuneFreeBits;
        var wideBits = wide.RuneFreeBits;
        var x = 0;
        for (var index = 0; index < runes.Length; index++)
        {
            var rune = runes[index].Value;
            if (widths[index] == 2)
            {
                PackedCell.StoreWithRune(ref cells[x], wideBits, rune);
                cells[x + 1] = continuation;
                x += 2;
                _mayHoldWide = true;
            }
            else
            {
                PackedCell.StoreWithRune(ref cells[x++], narrowBits, rune);
            }
        }

        ClearSide(_combining, start, end - start);
        if (hyperlink is null)
        {
            ClearSide(_hyperlinks, start, end - start);
        }
        else
        {
            _hyperlinks ??= new string?[_cells.Length];
            _hyperlinks.AsSpan(start, end - start).Fill(hyperlink);
        }
    }

    /// <summary>Stores a packed cell with no combining text.</summary>
    public void StorePacked(int x, PackedCell cell, string? hyperlink)
    {
        PrepareWrite(x, 1);
        _cells[x] = cell;
        NoteStored(cell);
        SetSide(ref _combining, x, null);
        SetSide(ref _hyperlinks, x, hyperlink);
    }

    // Field reads that avoid materializing a whole Cell on hot paths.
    public bool IsWideContinuation(int x) => CellAt(x).IsWideContinuation;

    public int DisplayWidth(int x) => CellAt(x).DisplayWidth;

    public Rune RuneAt(int x) => CellAt(x).Rune;

    public string? CombiningAt(int x) => _combining?[x];

    public bool IsBlank(int x) => CellAt(x).IsBlankIgnoringCombining && string.IsNullOrEmpty(_combining?[x]);

    public static CellRow FromCells(ReadOnlySpan<Cell> cells, int columns)
    {
        var row = new CellRow(columns, CellAttributes.Default);
        var count = Math.Min(cells.Length, columns);
        for (var x = 0; x < count; x++)
        {
            row[x] = cells[x];
        }

        return row;
    }

    public Cell[] ToArray()
    {
        var result = new Cell[_cells.Length];
        for (var x = 0; x < result.Length; x++)
        {
            result[x] = this[x];
        }

        return result;
    }

    /// <summary>Blanks the whole line and releases its side arrays, in O(1).</summary>
    public void Reset(CellAttributes attributes)
    {
        _blank = PackedCell.Blank(attributes);
        _lo = 0;
        _hi = 0;
        _mayHoldWide = false;
        _combining = null;
        _hyperlinks = null;
    }

    /// <summary>Equivalent to storing a blank with <paramref name="attributes"/> in each cell.</summary>
    public void FillBlank(int start, int count, CellAttributes attributes)
    {
        if (count <= 0)
        {
            return;
        }

        if (start == 0 && count == _cells.Length)
        {
            Reset(attributes);
            return;
        }

        ClearSide(_combining, start, count);
        ClearSide(_hyperlinks, start, count);
        if (start + count == _cells.Length)
        {
            // Erasing through the end of the line becomes the new blank tail.
            // [0, start) must keep reading as it does now, including cells that
            // are blank only logically, so it is stored first; for text written
            // from column 0 that costs nothing.
            Materialize(0, start);
            _hi = start;
            _blank = PackedCell.Blank(attributes);
            return;
        }

        PrepareWrite(start, count);
        _cells.AsSpan(start, count).Fill(PackedCell.Blank(attributes));
    }

    /// <summary>Equivalent to storing <paramref name="cell"/> in each cell of the range.</summary>
    public void Fill(int start, int count, in Cell cell)
    {
        if (count <= 0)
        {
            return;
        }

        PrepareWrite(start, count);
        var packed = PackedCell.From(cell);
        _cells.AsSpan(start, count).Fill(packed);
        NoteStored(packed);
        FillSide(ref _combining, start, count, cell.CombiningCharacters);
        FillSide(ref _hyperlinks, start, count, cell.HyperlinkUri);
    }

    /// <summary>Equivalent to storing <paramref name="template"/> with each byte as its rune.</summary>
    public void WriteAscii(int start, ReadOnlySpan<byte> text, in Cell template) =>
        WriteAscii(start, text, PackedCell.From(template), template.HyperlinkUri);

    /// <summary>
    /// Stores <paramref name="template"/> once per byte with that byte as its rune,
    /// no combining text and <paramref name="hyperlink"/>.
    /// </summary>
    public void WriteAscii(int start, ReadOnlySpan<byte> text, PackedCell template, string? hyperlink)
    {
        if (text.IsEmpty)
        {
            return;
        }

        PrepareWrite(start, text.Length);
        PackedCell.FillAscii(_cells.AsSpan(start, text.Length), text, template);
        ClearSide(_combining, start, text.Length);
        if (hyperlink is null)
        {
            ClearSide(_hyperlinks, start, text.Length);
        }
        else
        {
            _hyperlinks ??= new string?[_cells.Length];
            _hyperlinks.AsSpan(start, text.Length).Fill(hyperlink);
        }
    }

    /// <summary>
    /// Equivalent to <see cref="WriteAscii(int, ReadOnlySpan{byte}, PackedCell, string?)"/>
    /// with the single byte <paramref name="value"/>.
    /// </summary>
    public void StoreAscii(int x, byte value, PackedCell template, string? hyperlink)
    {
        PrepareWrite(x, 1);
        _cells[x] = template.WithRune(new Rune(value));
        if (_combining is not null)
        {
            _combining[x] = null;
        }

        if (hyperlink is not null)
        {
            _hyperlinks ??= new string?[_cells.Length];
            _hyperlinks[x] = hyperlink;
        }
        else if (_hyperlinks is not null)
        {
            _hyperlinks[x] = null;
        }
    }

    public void CopyTo(int sourceIndex, CellRow destination, int destinationIndex, int count)
    {
        if (count <= 0)
        {
            return;
        }

        Materialize(sourceIndex, sourceIndex + count);
        destination.PrepareWrite(destinationIndex, count);
        destination._mayHoldWide |= _mayHoldWide;
        _cells.AsSpan(sourceIndex, count).CopyTo(destination._cells.AsSpan(destinationIndex, count));
        CopySide(_combining, ref destination._combining, sourceIndex, destinationIndex, count, destination.Length);
        CopySide(_hyperlinks, ref destination._hyperlinks, sourceIndex, destinationIndex, count, destination.Length);
    }

    public CellRow Clone() => new((PackedCell[])_cells.Clone())
    {
        _combining = (string?[]?)_combining?.Clone(),
        _hyperlinks = (string?[]?)_hyperlinks?.Clone(),
        _blank = _blank,
        _lo = _lo,
        _hi = _hi,
        _mayHoldWide = _mayHoldWide,
    };

    public bool MayHoldWide => _mayHoldWide;

    /// <summary>
    /// Whether <see cref="BeginAppend"/> may start at <paramref name="start"/>: the
    /// stored range is empty or starts at or before it, so no gap can open on the
    /// right of the cells appended.
    /// </summary>
    public bool CanAppendAt(int start) => _lo == _hi || start >= _lo;

    /// <summary>
    /// Returns the cells from <paramref name="start"/> to the end of the line for
    /// the caller to overwrite from the start, after storing the blank in any gap
    /// before them. <see cref="EndAppend"/> must follow with the end written.
    /// </summary>
    public Span<PackedCell> BeginAppend(int start)
    {
        if (_lo != _hi && start > _hi)
        {
            _cells.AsSpan(_hi, start - _hi).Fill(_blank);
            _hi = start;
        }

        return _cells.AsSpan(start);
    }

    /// <summary>Completes <see cref="BeginAppend"/>: the cells written carry no combining text.</summary>
    public void EndAppend(int start, int end, string? hyperlink, bool wroteWide)
    {
        if (_lo == _hi)
        {
            _lo = start;
            _hi = end;
        }
        else if (end > _hi)
        {
            _hi = end;
        }

        ClearSide(_combining, start, end - start);
        if (hyperlink is null)
        {
            ClearSide(_hyperlinks, start, end - start);
        }
        else
        {
            _hyperlinks ??= new string?[_cells.Length];
            _hyperlinks.AsSpan(start, end - start).Fill(hyperlink);
        }

        _mayHoldWide |= wroteWide;
    }

    private void NoteStored(PackedCell cell)
    {
        if (cell.IsWideContinuation || cell.DisplayWidth == 2)
        {
            _mayHoldWide = true;
        }
    }

    private ref readonly PackedCell CellAt(int x)
    {
        ref readonly var stored = ref _cells[x];
        return ref (uint)(x - _lo) < (uint)(_hi - _lo) ? ref stored : ref _blank;
    }

    // [start, start + count) is about to be overwritten: grow the stored range
    // to include it, filling any gap between them with the blank they read as.
    private void PrepareWrite(int start, int count)
    {
        var end = start + count;
        if (_lo == _hi)
        {
            _lo = start;
            _hi = end;
            return;
        }

        if (start > _hi)
        {
            _cells.AsSpan(_hi, start - _hi).Fill(_blank);
        }

        if (end < _lo)
        {
            _cells.AsSpan(end, _lo - end).Fill(_blank);
        }

        _lo = Math.Min(_lo, start);
        _hi = Math.Max(_hi, end);
    }

    // [start, end) is about to be read from the array: store the blank in the
    // cells that are blank only logically, then grow the stored range.
    private void Materialize(int start, int end)
    {
        if (start >= end)
        {
            return;
        }

        if (_lo == _hi)
        {
            _cells.AsSpan(start, end - start).Fill(_blank);
        }
        else
        {
            if (start < _lo)
            {
                _cells.AsSpan(start, Math.Min(end, _lo) - start).Fill(_blank);
            }

            if (end > _hi)
            {
                var from = Math.Max(start, _hi);
                _cells.AsSpan(from, end - from).Fill(_blank);
            }
        }

        PrepareWrite(start, end - start);
    }

    private void SetSide(ref string?[]? side, int x, string? value)
    {
        if (value is not null)
        {
            side ??= new string?[_cells.Length];
            side[x] = value;
        }
        else if (side is not null)
        {
            side[x] = null;
        }
    }

    private void FillSide(ref string?[]? side, int start, int count, string? value)
    {
        if (value is not null)
        {
            side ??= new string?[_cells.Length];
            side.AsSpan(start, count).Fill(value);
        }
        else
        {
            ClearSide(side, start, count);
        }
    }

    private static void ClearSide(string?[]? side, int start, int count)
    {
        if (side is not null)
        {
            Array.Clear(side, start, count);
        }
    }

    private static void CopySide(
        string?[]? source,
        ref string?[]? destination,
        int sourceIndex,
        int destinationIndex,
        int count,
        int destinationLength)
    {
        if (source is not null)
        {
            destination ??= new string?[destinationLength];
            Array.Copy(source, sourceIndex, destination, destinationIndex, count);
        }
        else if (destination is not null)
        {
            Array.Clear(destination, destinationIndex, count);
        }
    }
}
