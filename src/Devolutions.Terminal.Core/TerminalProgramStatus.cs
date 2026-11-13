using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace Devolutions.Terminal.Core;

public enum TerminalProgramState
{
    Idle,
    Working,
    Done,
    Blocked,
    Error,
}

public enum TerminalProgramBlockKind
{
    Permission,
    Question,
    Auth,
}

public sealed record TerminalProgramStatusRecord(
    string Id,
    TerminalProgramState State,
    TerminalProgramBlockKind? Kind,
    int? Progress,
    string? App,
    string? Title,
    string? Message);

public sealed class TerminalProgramStatusSnapshot
{
    internal TerminalProgramStatusSnapshot(Dictionary<string, TerminalProgramStatusRecord> records) =>
        Records = new ReadOnlyDictionary<string, TerminalProgramStatusRecord>(records);

    public IReadOnlyDictionary<string, TerminalProgramStatusRecord> Records { get; }

    public string? GetEffectiveApp(string id)
    {
        while (true)
        {
            if (Records.TryGetValue(id, out var record) && record.App is not null)
            {
                return record.App;
            }

            if (id.Length == 0)
            {
                return null;
            }

            var slash = id.LastIndexOf('/');
            id = slash < 0 ? string.Empty : id[..slash];
        }
    }
}

internal sealed class TerminalProgramStatusStore
{
    private const int MaximumRecords = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Dictionary<string, TerminalProgramStatusRecord> _records = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recency = new();
    private readonly Dictionary<string, LinkedListNode<string>> _nodes = new(StringComparer.Ordinal);
    private bool _processExited;

    public TerminalProgramStatusSnapshot Snapshot { get; private set; } = new(new(StringComparer.Ordinal));

    public bool Apply(ReadOnlySpan<byte> data)
    {
        string? state = null, id = null, kind = null, progress = null, app = null, title = null, message = null;
        var hasId = false;
        while (true)
        {
            var separator = data.IndexOf((byte)':');
            var pair = separator < 0 ? data : data[..separator];
            var equals = pair.IndexOf((byte)'=');
            if (equals >= 0)
            {
                var key = TrimAsciiWhitespace(pair[..equals]);
                var value = TrimAsciiWhitespace(pair[(equals + 1)..]);
                if (key.Length > 16)
                {
                    return false;
                }

                var isId = key.SequenceEqual("id"u8);
                var isApp = key.SequenceEqual("app"u8);
                var isMessage = key.SequenceEqual("msg"u8);
                var isTitle = key.SequenceEqual("title"u8);
                if ((isId && value.Length > 128) ||
                    (isApp && value.Length > 32) ||
                    (isMessage && value.Length > 2732) ||
                    (isTitle && value.Length > 256))
                {
                    return false;
                }

                if (isId)
                {
                    hasId = true;
                    id = IsValue(value) ? Encoding.ASCII.GetString(value) : null;
                }

                if (!isId && IsKey(key) && IsValue(value))
                {
                    if (isApp)
                    {
                        app = Encoding.ASCII.GetString(value);
                    }
                    else if (isMessage || isTitle)
                    {
                        if (!TryDecodeText(value, isMessage ? 2732 : 256, isMessage ? 2048 : 192, out var text))
                        {
                            return false;
                        }
                        if (isMessage) message = text;
                        else title = text;
                    }
                    else if (key.SequenceEqual("state"u8)) state = Encoding.ASCII.GetString(value);
                    else if (key.SequenceEqual("kind"u8)) kind = Encoding.ASCII.GetString(value);
                    else if (key.SequenceEqual("progress"u8)) progress = Encoding.ASCII.GetString(value);
                }
            }
            if (separator < 0) break;
            data = data[(separator + 1)..];
        }

        if (hasId && (id is null || ExceedsIdLimits(id) || !IsId(id)))
        {
            return false;
        }

        id ??= string.Empty;
        if (state == "clear")
        {
            return ClearSubtree(id);
        }

        var parsedState = state switch
        {
            "idle" => TerminalProgramState.Idle,
            "working" => TerminalProgramState.Working,
            "done" => TerminalProgramState.Done,
            "blocked" => TerminalProgramState.Blocked,
            "error" => TerminalProgramState.Error,
            _ => (TerminalProgramState?)null,
        };
        if (parsedState is null) return false;
        if (_processExited && parsedState is TerminalProgramState.Working or TerminalProgramState.Blocked)
        {
            return false;
        }

        var parsedKind = parsedState == TerminalProgramState.Blocked ? kind switch
        {
            "permission" => TerminalProgramBlockKind.Permission,
            "question" => TerminalProgramBlockKind.Question,
            "auth" => TerminalProgramBlockKind.Auth,
            _ => (TerminalProgramBlockKind?)null,
        } : null;
        int? parsedProgress = null;
        if (parsedState is TerminalProgramState.Working or TerminalProgramState.Blocked &&
            progress is not null &&
            int.TryParse(progress, NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
            number is >= 0 and <= 100)
        {
            parsedProgress = number;
        }

        if (app is not null && !IsSegment(app)) app = null;
        var record = new TerminalProgramStatusRecord(id, parsedState.Value, parsedKind, parsedProgress, app, title, message);
        if (_nodes.Remove(id, out var existing))
        {
            _recency.Remove(existing);
        }
        else if (_records.Count == MaximumRecords)
        {
            var oldest = _recency.First!;
            _recency.RemoveFirst();
            _nodes.Remove(oldest.Value);
            _records.Remove(oldest.Value);
        }
        _records[id] = record;
        _nodes[id] = _recency.AddLast(id);
        Publish();
        return true;
    }

    public bool Clear()
    {
        _processExited = false;
        return ClearSubtree(string.Empty);
    }

    public bool ProcessExited()
    {
        _processExited = true;
        return ClearTransient();
    }

    public bool BeginPrompt()
    {
        _processExited = false;
        return ClearTransient();
    }

    private bool ClearTransient()
    {
        var ids = _records.Values
            .Where(static record => record.State is TerminalProgramState.Working or TerminalProgramState.Blocked)
            .Select(static record => record.Id)
            .ToArray();
        return Remove(ids);
    }

    private bool ClearSubtree(string id) =>
        Remove(_records.Keys.Where(key => id.Length == 0 || key == id ||
            key.StartsWith(id + "/", StringComparison.Ordinal)).ToArray());

    private bool Remove(string[] ids)
    {
        if (ids.Length == 0) return false;
        foreach (var id in ids)
        {
            _records.Remove(id);
            _recency.Remove(_nodes[id]);
            _nodes.Remove(id);
        }
        Publish();
        return true;
    }

    private void Publish() => Snapshot = new(new Dictionary<string, TerminalProgramStatusRecord>(_records, StringComparer.Ordinal));

    private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> value)
    {
        while (!value.IsEmpty && value[0] is (byte)' ' or (byte)'\t') value = value[1..];
        while (!value.IsEmpty && value[^1] is (byte)' ' or (byte)'\t') value = value[..^1];
        return value;
    }

    private static bool IsKey(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty) return false;
        foreach (var character in key)
        {
            if (character is < (byte)'a' or > (byte)'z') return false;
        }
        return true;
    }

    private static bool IsValue(ReadOnlySpan<byte> value)
    {
        foreach (var character in value)
        {
            if (character is not (>= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'_' or (byte)'.' or (byte)',' or (byte)'+' or (byte)'/' or (byte)'=' or (byte)'-'))
                return false;
        }
        return true;
    }

    private static bool IsSegment(ReadOnlySpan<char> segment)
    {
        if (segment.IsEmpty || segment.Length > 32) return false;
        foreach (var character in segment)
        {
            if (character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '+' or '-'))
                return false;
        }
        return true;
    }

    private static bool ExceedsIdLimits(ReadOnlySpan<char> id)
    {
        var levels = 1;
        while (true)
        {
            var slash = id.IndexOf('/');
            var segment = slash < 0 ? id : id[..slash];
            if (segment.Length > 32 || levels > 8) return true;
            if (slash < 0) return false;
            id = id[(slash + 1)..];
            levels++;
        }
    }

    private static bool IsId(ReadOnlySpan<char> id)
    {
        while (true)
        {
            var slash = id.IndexOf('/');
            if (!IsSegment(slash < 0 ? id : id[..slash])) return false;
            if (slash < 0) return true;
            id = id[(slash + 1)..];
        }
    }

    private static bool TryDecodeText(ReadOnlySpan<byte> value, int encodedLimit, int decodedLimit, out string? text)
    {
        text = null;
        if (value.Length > encodedLimit ||
            value.Length % 4 == 1 ||
            (value.Contains((byte)'=') && value.Length % 4 != 0))
        {
            return false;
        }
        var padded = Encoding.ASCII.GetString(value).PadRight((value.Length + 3) / 4 * 4, '=');
        var bytes = new byte[padded.Length / 4 * 3];
        if (!Convert.TryFromBase64String(padded, bytes, out var written) || written > decodedLimit) return false;
        try
        {
            text = StrictUtf8.GetString(bytes, 0, written);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        foreach (var character in text)
        {
            if (character is <= '\u001f' or >= '\u007f' and <= '\u009f') return false;
        }
        return true;
    }
}
