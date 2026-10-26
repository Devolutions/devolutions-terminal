namespace Devolutions.Terminal.Core;

internal sealed class CircularBuffer<T>
{
    private T[] _items;
    private int _start;

    public CircularBuffer(int capacity)
    {
        _items = new T[Math.Max(1, capacity)];
    }

    public int Capacity => _items.Length;
    public int Count { get; private set; }

    public T this[int index]
    {
        get
        {
            ValidateIndex(index);
            return _items[PhysicalIndex(index)];
        }
        set
        {
            ValidateIndex(index);
            _items[PhysicalIndex(index)] = value;
        }
    }

    public bool AddLast(T item)
    {
        if (Count < Capacity)
        {
            _items[PhysicalIndex(Count++)] = item;
            return false;
        }

        _items[_start] = item;
        _start = Wrap(_start + 1);
        return true;
    }

    public T RemoveFirst()
    {
        if (Count == 0)
        {
            throw new InvalidOperationException("The circular buffer is empty.");
        }

        var item = _items[_start];
        _items[_start] = default!;
        _start = Wrap(_start + 1);
        Count--;
        return item;
    }

    public T RemoveLast()
    {
        if (Count == 0)
        {
            throw new InvalidOperationException("The circular buffer is empty.");
        }

        var index = PhysicalIndex(Count - 1);
        var item = _items[index];
        _items[index] = default!;
        Count--;
        return item;
    }

    public void Clear()
    {
        Array.Clear(_items);
        _start = 0;
        Count = 0;
    }

    public void ResetCapacity(int capacity, IReadOnlyList<T> items)
    {
        capacity = Math.Max(1, capacity);
        if (_items.Length == capacity)
        {
            // Projected engines replace the viewport every frame; keep the array.
            Array.Clear(_items);
        }
        else
        {
            _items = new T[capacity];
        }

        _start = 0;
        Count = 0;
        var first = Math.Max(0, items.Count - capacity);
        for (var i = first; i < items.Count; i++)
        {
            AddLast(items[i]);
        }
    }

    public List<T> ToList()
    {
        var result = new List<T>(Count);
        for (var i = 0; i < Count; i++)
        {
            result.Add(this[i]);
        }

        return result;
    }

    /// <summary>
    /// Moves the items at logical indexes <paramref name="first"/>+1 through
    /// <paramref name="last"/> down by one and puts the former first item last;
    /// it returns that item. This is a scroll region scrolling up.
    /// </summary>
    public T RotateDown(int first, int last)
    {
        ValidateIndex(first);
        ValidateIndex(last);
        var item = this[first];
        Move(first + 1, first, last - first);
        _items[PhysicalIndex(last)] = item;
        return item;
    }

    /// <summary>The inverse of <see cref="RotateDown"/>: a scroll region scrolling down.</summary>
    public T RotateUp(int first, int last)
    {
        ValidateIndex(first);
        ValidateIndex(last);
        var item = this[last];
        Move(first, first + 1, last - first);
        _items[PhysicalIndex(first)] = item;
        return item;
    }

    // Copies count items between logical ranges, as Array.Copy (memmove) when
    // neither range wraps around the end of the backing array.
    private void Move(int source, int destination, int count)
    {
        if (count <= 0)
        {
            return;
        }

        var physicalSource = PhysicalIndex(source);
        var physicalDestination = PhysicalIndex(destination);
        if (physicalSource + count <= _items.Length && physicalDestination + count <= _items.Length)
        {
            Array.Copy(_items, physicalSource, _items, physicalDestination, count);
            return;
        }

        if (destination < source)
        {
            for (var index = 0; index < count; index++)
            {
                _items[PhysicalIndex(destination + index)] = _items[PhysicalIndex(source + index)];
            }
        }
        else
        {
            for (var index = count - 1; index >= 0; index--)
            {
                _items[PhysicalIndex(destination + index)] = _items[PhysicalIndex(source + index)];
            }
        }
    }

    // _start and a valid logical index are each below the capacity, so one
    // conditional subtraction replaces an integer division.
    private int PhysicalIndex(int logicalIndex) => Wrap(_start + logicalIndex);

    private int Wrap(int index) => index >= _items.Length ? index - _items.Length : index;

    private void ValidateIndex(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }
}
