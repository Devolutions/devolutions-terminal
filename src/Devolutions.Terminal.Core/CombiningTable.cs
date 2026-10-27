using System.Text;

namespace Devolutions.Terminal.Core;

/// <summary>
/// Interns the combining text of cells (the scalars after the base rune) as int
/// IDs, so rows store ints instead of strings: a cluster seen before costs no
/// allocation, and the side arrays hold nothing for the GC to trace. ID 0 means no
/// combining text; the empty string has an ID of its own, as a cell can carry it.
/// One table serves the rows of one <see cref="TextBuffer"/>, on its thread.
/// </summary>
internal sealed class CombiningTable
{
    // Compaction never runs below this many IDs, and otherwise waits for the
    // table to double since the last one, so its cost stays amortized.
    private const int MinimumCompactionCount = 4096;

    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    // (ID, scalar) to the ID of that text with the scalar appended.
    private readonly Dictionary<long, int> _extended = [];
    private string[] _texts = new string[16];
    private int _count = 1;
    private int _compactAt = MinimumCompactionCount;

    /// <summary>The number of IDs handed out, counting 0.</summary>
    public int Count => _count;

    public bool NeedsCompaction => _count > _compactAt;

    public string? this[int id] => id == 0 ? null : _texts[id];

    public int Intern(string? text)
    {
        if (text is null)
        {
            return 0;
        }

        if (_ids.TryGetValue(text, out var id))
        {
            return id;
        }

        if (_count == _texts.Length)
        {
            Array.Resize(ref _texts, _texts.Length * 2);
        }

        id = _count++;
        _texts[id] = text;
        _ids.Add(text, id);
        return id;
    }

    /// <summary>The ID of <paramref name="id"/>'s text with <paramref name="rune"/> appended.</summary>
    public int Extend(int id, Rune rune)
    {
        var key = ((long)id << 21) | (uint)rune.Value;
        if (!_extended.TryGetValue(key, out var extended))
        {
            extended = Intern((this[id] ?? string.Empty) + rune);
            _extended.Add(key, extended);
        }

        return extended;
    }

    /// <summary>
    /// Drops every ID not marked in <paramref name="live"/> (indexed by ID, at least
    /// <see cref="Count"/> long) and renumbers the rest. Returns the old-to-new map
    /// that every row using this table must then apply.
    /// </summary>
    public int[] Compact(bool[] live)
    {
        var map = new int[_count];
        var texts = new string[Math.Max(16, _texts.Length)];
        var count = 1;
        _ids.Clear();
        _extended.Clear();
        for (var id = 1; id < _count; id++)
        {
            if (live[id])
            {
                map[id] = count;
                texts[count] = _texts[id];
                _ids.Add(_texts[id], count);
                count++;
            }
        }

        _texts = texts;
        _count = count;
        _compactAt = Math.Max(MinimumCompactionCount, count * 2);
        return map;
    }
}
