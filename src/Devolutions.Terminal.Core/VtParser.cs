using System.Buffers;
using System.Text;

namespace Devolutions.Terminal.Core;

public sealed class VtParser
{
    private const int MaxParameters = 32;
    private const int MaxStringBytes = 1024 * 1024;
    // A 768-KiB inline image expands to exactly 1 MiB of base64; leave
    // bounded room for the OSC command and its metadata as well.
    private const int MaxOscStringBytes = MaxStringBytes + (16 * 1024);
    private const int MaxDcsPayloadBytes = TerminalImageLimits.MaximumDcsPayloadBytes;
    private const int MaxDcsIntermediates = 2;
    private const int MaxEscIntermediates = 3;

    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        CsiIgnore,
        DcsEntry,
        DcsParam,
        DcsIntermediate,
        DcsPassthrough,
        DcsEscape,
        DcsIgnore,
        Vt52CursorRow,
        Vt52CursorColumn,
        OscString,
        OscEscape,
        ApcString,
        ApcEscape,
        StringIgnore,
        StringEscape,
    }

    private readonly IVtDispatch _dispatch;
    private readonly int[] _parameters = new int[MaxParameters];
    private readonly bool[] _subparameters = new bool[MaxParameters];
    private readonly int[] _csiParameters = new int[MaxParameters];
    private readonly bool[] _csiSubparameters = new bool[MaxParameters];
    private int _csiDispatchDepth;
    private readonly Rune[] _runeScratch = new Rune[256];
    private int _printRunesDepth;
    private bool _nextIsSubparameter;
    private readonly List<byte> _osc = [];
    private readonly List<byte> _apc = [];
    private readonly List<byte> _dcs = [];
    private readonly byte[] _dcsIntermediates = new byte[MaxDcsIntermediates];
    private readonly byte[] _escIntermediates = new byte[MaxEscIntermediates];
    private State _state;
    private int _parameterCount;
    private int _currentParameter = -1;
    private byte _intermediate;
    private byte _privateMarker;
    private byte _dcsFinal;
    private int _dcsIntermediateCount;
    private int _escIntermediateCount;
    private bool _dcsEscapeCanDispatch;
    private byte _vt52Row;
    private bool _ansiMode = true;
    private int _utf8Needed;
    private int _utf8Accumulator;
    private int _utf8Minimum;

    public VtParser(IVtDispatch dispatch)
    {
        _dispatch = dispatch;
    }

    public void Process(ReadOnlySpan<byte> data)
    {
        var index = 0;
        while (index < data.Length)
        {
            if (_state == State.Ground && _utf8Needed == 0 && IsPrintableAscii(data[index]))
            {
                // Hand whole printable-ASCII runs to the dispatcher. Single characters
                // between escape sequences are common, so only start the vectorized
                // scan once a second printable byte follows.
                var run = 1;
                if (index + 1 < data.Length && IsPrintableAscii(data[index + 1]))
                {
                    run = data[index..].IndexOfAnyExceptInRange((byte)0x20, (byte)0x7E);
                    run = run < 0 ? data.Length - index : run;
                }

                _dispatch.PrintAscii(data.Slice(index, run));
                index += run;
                continue;
            }

            if (data[index] >= 0xC2 && _state == State.Ground && _utf8Needed == 0)
            {
                var consumed = DecodePrintableRun(data[index..]);
                if (consumed > 0)
                {
                    index += consumed;
                    continue;
                }
            }

            if (data[index] == 0x1B && _state == State.Ground && _utf8Needed == 0)
            {
                var consumed = TryProcessCsi(data[index..]);
                if (consumed > 0)
                {
                    index += consumed;
                    continue;
                }
            }

            if (data[index] < 0x20 && data[index] is not (0x18 or 0x1A or 0x1B) &&
                _state == State.Ground && _utf8Needed == 0)
            {
                // What ProcessByte does with a C0 control in ground state with no
                // UTF-8 pending, without its string-state checks: CR and LF end
                // every line.
                _dispatch.ExecuteC0(data[index++]);
                continue;
            }

            ProcessByte(data[index++]);
        }
    }

    /// <summary>
    /// Decodes a run that starts with a well-formed UTF-8 sequence and continues
    /// with well-formed sequences and short printable-ASCII stretches, and hands it
    /// to <see cref="IVtDispatch.PrintRunes"/>. Each well-formed sequence is exactly
    /// what the byte path would decode into one Print, and each printable ASCII byte
    /// one Print too. The run stops before controls, malformed or split input, and
    /// long ASCII stretches (left to the ASCII path). Returns the bytes consumed.
    /// </summary>
    private int DecodePrintableRun(ReadOnlySpan<byte> data)
    {
        // A handler that feeds this parser again must not overwrite the run the
        // outer call is still printing.
        var runes = _printRunesDepth == 0 ? _runeScratch : new Rune[_runeScratch.Length];
        var count = 0;
        var index = 0;
        while (index < data.Length && count < runes.Length)
        {
            var value = data[index];
            if (IsPrintableAscii(value))
            {
                // One look per ASCII stretch: 16 or more printable bytes go to the
                // ASCII path, a shorter stretch joins the run whole. Stretches inside
                // non-ASCII text are short, so a scalar count beats a vector search.
                var limit = Math.Min(16, data.Length - index);
                var stretch = 1;
                while (stretch < limit && IsPrintableAscii(data[index + stretch]))
                {
                    stretch++;
                }

                if (stretch == 16)
                {
                    break;
                }

                stretch = Math.Min(stretch, runes.Length - count);
                for (var offset = 0; offset < stretch; offset++)
                {
                    runes[count++] = new Rune(data[index + offset]);
                }

                index += stretch;
                continue;
            }

            // Two- to four-byte sequences, decoded inline with the validity rules
            // of the byte path (no overlong forms, no surrogates, nothing past
            // U+10FFFF); everything else goes through the general decoder.
            if (value <= 0xDF && value >= 0xC2 && index + 1 < data.Length && (data[index + 1] & 0xC0) == 0x80)
            {
                runes[count++] = new Rune(((value & 0x1F) << 6) | (data[index + 1] & 0x3F));
                index += 2;
                continue;
            }

            if (value is >= 0xE0 and <= 0xEF && index + 2 < data.Length &&
                (data[index + 1] & 0xC0) == 0x80 && (data[index + 2] & 0xC0) == 0x80)
            {
                var scalar = ((value & 0x0F) << 12) | ((data[index + 1] & 0x3F) << 6) | (data[index + 2] & 0x3F);
                if (scalar >= 0x800 && scalar is < 0xD800 or > 0xDFFF)
                {
                    runes[count++] = new Rune(scalar);
                    index += 3;
                    continue;
                }
            }

            if (value is >= 0xF0 and <= 0xF4 && index + 3 < data.Length &&
                (data[index + 1] & 0xC0) == 0x80 && (data[index + 2] & 0xC0) == 0x80 && (data[index + 3] & 0xC0) == 0x80)
            {
                var scalar = ((value & 0x07) << 18) | ((data[index + 1] & 0x3F) << 12) |
                             ((data[index + 2] & 0x3F) << 6) | (data[index + 3] & 0x3F);
                if (scalar is >= 0x10000 and <= 0x10FFFF)
                {
                    runes[count++] = new Rune(scalar);
                    index += 4;
                    continue;
                }
            }

            if (value < 0xC2 ||
                Rune.DecodeFromUtf8(data[index..], out var rune, out var length) != OperationStatus.Done)
            {
                break;
            }

            runes[count++] = rune;
            index += length;
        }

        if (count > 0)
        {
            _printRunesDepth++;
            try
            {
                _dispatch.PrintRunes(runes.AsSpan(0, count));
            }
            finally
            {
                _printRunesDepth--;
            }
        }

        return index;
    }

    /// <summary>
    /// Parses a complete, plain CSI sequence (<c>ESC [</c>, optional private
    /// marker, digits, ';' and ':' separators, final byte) in one pass instead of
    /// feeding each byte through the state machine. Parameters go through the same
    /// helpers and <see cref="DispatchCsi"/> as the byte path. Anything else
    /// (intermediates, embedded controls, ignored or overflowing sequences, or a
    /// sequence that continues in the next chunk) returns 0 without consuming
    /// input, and the state machine handles it.
    /// </summary>
    private int TryProcessCsi(ReadOnlySpan<byte> data)
    {
        if (!_ansiMode || data.Length < 3 || data[1] != (byte)'[')
        {
            return 0;
        }

        var start = 2;
        var privateMarker = data[2] is >= 0x3C and <= 0x3F ? data[2] : (byte)0;
        if (privateMarker != 0)
        {
            start = 3;
        }

        var final = -1;
        var separators = 0;
        for (var index = start; index < data.Length; index++)
        {
            var value = data[index];
            if (value is >= (byte)'0' and <= (byte)'9')
            {
                continue;
            }

            if (value is (byte)';' or (byte)':')
            {
                separators++;
                continue;
            }

            if (IsFinal(value))
            {
                final = index;
            }

            break;
        }

        // A separator pushing past the limit puts the byte path in CsiIgnore and
        // drops the sequence. An overflowing final push behaves the same in both
        // paths (DispatchCsi still dispatches), so only that case is excluded.
        if (final < 0 || separators > MaxParameters)
        {
            return 0;
        }

        // The state after ESC and '[' (and the private marker, if any).
        ClearSequence();
        _state = State.CsiParam;
        _privateMarker = privateMarker;
        for (var index = start; index < final; index++)
        {
            var value = data[index];
            if (value == (byte)';')
            {
                PushParameter();
                _nextIsSubparameter = false;
            }
            else if (value == (byte)':')
            {
                PushParameter();
                _nextIsSubparameter = true;
            }
            else
            {
                _currentParameter = _currentParameter < 0 ? 0 : _currentParameter;
                _currentParameter = Math.Min((_currentParameter * 10) + (value - '0'), 65535);
            }
        }

        DispatchCsi(data[final]);
        return final + 1;
    }

    public void Reset()
    {
        _state = State.Ground;
        ClearSequence();
        _osc.Clear();
        _apc.Clear();
        ClearDcs();
        ResetUtf8();
        _ansiMode = true;
    }

    private void ProcessByte(byte value)
    {
        if (_state is State.DcsEntry or State.DcsParam or State.DcsIntermediate or
            State.DcsPassthrough or State.DcsEscape or State.DcsIgnore)
        {
            ProcessDcs(value);
            return;
        }

        if (_state == State.OscString)
        {
            ProcessOsc(value);
            return;
        }

        if (_state == State.OscEscape)
        {
            if (value is 0x18 or 0x1A)
            {
                _osc.Clear();
                _state = State.Ground;
                _dispatch.ExecuteC0(value);
            }
            else if (value == (byte)'\\')
            {
                FinishOsc();
            }
            else
            {
                // ESC not followed by '\' aborts the string and starts a new
                // sequence (VT500 parser), as DCS does; otherwise an unterminated
                // string would swallow all following output.
                _osc.Clear();
                _state = State.Escape;
                ClearSequence();
                ProcessByte(value);
            }

            return;
        }

        if (_state == State.ApcString)
        {
            ProcessApc(value);
            return;
        }

        if (_state == State.ApcEscape)
        {
            if (value is 0x18 or 0x1A)
            {
                _apc.Clear();
                _state = State.Ground;
                _dispatch.ExecuteC0(value);
            }
            else if (value == (byte)'\\')
            {
                FinishApc();
            }
            else
            {
                // ESC not followed by '\' aborts the string and starts a new
                // sequence (VT500 parser), as DCS does; otherwise an unterminated
                // string would swallow all following output.
                _apc.Clear();
                _state = State.Escape;
                ClearSequence();
                ProcessByte(value);
            }

            return;
        }

        if (_state == State.StringIgnore)
        {
            if (value is 0x18 or 0x1A)
            {
                _state = State.Ground;
                _dispatch.ExecuteC0(value);
            }
            else if (value == 0x1B)
            {
                _state = State.StringEscape;
            }
            else if (value == 0x07)
            {
                _state = State.Ground;
            }

            return;
        }

        if (_state == State.StringEscape)
        {
            if (value is 0x18 or 0x1A)
            {
                _state = State.Ground;
                _dispatch.ExecuteC0(value);
            }
            else
            {
                _state = value == (byte)'\\' ? State.Ground : State.StringIgnore;
            }

            return;
        }

        if (value is 0x18 or 0x1A)
        {
            _state = State.Ground;
            ClearSequence();
            ResetUtf8();
            return;
        }

        if (value == 0x1B)
        {
            EmitIncompleteUtf8();
            _state = State.Escape;
            ClearSequence();
            return;
        }

        if (value < 0x20)
        {
            EmitIncompleteUtf8();
            _dispatch.ExecuteC0(value);
            return;
        }

        if (value == 0x7F)
        {
            EmitIncompleteUtf8();
            return;
        }

        switch (_state)
        {
            case State.Ground:
                ProcessGround(value);
                break;
            case State.Escape:
                ProcessEscape(value);
                break;
            case State.EscapeIntermediate:
                ProcessEscapeIntermediate(value);
                break;
            case State.CsiEntry:
                ProcessCsiEntry(value);
                break;
            case State.CsiParam:
                ProcessCsiParam(value);
                break;
            case State.CsiIntermediate:
                ProcessCsiIntermediate(value);
                break;
            case State.CsiIgnore:
                if (IsFinal(value))
                {
                    _state = State.Ground;
                }

                break;
            case State.Vt52CursorRow:
                _vt52Row = value;
                _state = State.Vt52CursorColumn;
                break;
            case State.Vt52CursorColumn:
                _state = State.Ground;
                _dispatch.Vt52Dispatch('Y', _vt52Row, value);
                break;
        }
    }

    private void ProcessGround(byte value)
    {
        if (_utf8Needed > 0)
        {
            if ((value & 0xC0) != 0x80)
            {
                EmitReplacement();
                ResetUtf8();
                ProcessGround(value);
                return;
            }

            _utf8Accumulator = (_utf8Accumulator << 6) | (value & 0x3F);
            _utf8Needed--;
            if (_utf8Needed == 0)
            {
                var scalar = _utf8Accumulator;
                if (scalar >= _utf8Minimum && Rune.IsValid(scalar))
                {
                    _dispatch.Print(new Rune(scalar));
                }
                else
                {
                    EmitReplacement();
                }

                ResetUtf8();
            }

            return;
        }

        if (value < 0x80)
        {
            _dispatch.Print(new Rune(value));
            return;
        }

        if (!_ansiMode && value is >= 0x80 and <= 0x9F)
        {
            return;
        }

        if (value is 0x9B)
        {
            EnterCsi();
            return;
        }

        if (value is 0x9D)
        {
            EnterOsc();
            return;
        }

        if (value == 0x90)
        {
            EnterDcs();
            return;
        }

        if (value is 0x9F)
        {
            EnterApc();
            return;
        }

        if (value is 0x98 or 0x9E)
        {
            _state = State.StringIgnore;
            return;
        }

        if (value is 0x8E or 0x8F)
        {
            _dispatch.ExecuteC0(value);
            return;
        }

        if (value is >= 0xC2 and <= 0xDF)
        {
            StartUtf8(value & 0x1F, 1, 0x80);
        }
        else if (value is >= 0xE0 and <= 0xEF)
        {
            StartUtf8(value & 0x0F, 2, 0x800);
        }
        else if (value is >= 0xF0 and <= 0xF4)
        {
            StartUtf8(value & 0x07, 3, 0x10000);
        }
        else
        {
            EmitReplacement();
        }
    }

    private void ProcessEscape(byte value)
    {
        if (!_ansiMode)
        {
            if (value == (byte)'Y')
            {
                _state = State.Vt52CursorRow;
            }
            else
            {
                _state = State.Ground;
                if (value == (byte)'<')
                {
                    _ansiMode = true;
                }

                if (value is >= 0x30 and <= 0x7E)
                {
                    _dispatch.Vt52Dispatch((char)value);
                }
            }

            return;
        }

        switch (value)
        {
            case (byte)'[':
                EnterCsi();
                break;
            case (byte)']':
                EnterOsc();
                break;
            case (byte)'P':
                EnterDcs();
                break;
            case (byte)'X':
            case (byte)'^':
                _state = State.StringIgnore;
                break;
            case (byte)'_':
                EnterApc();
                break;
            case >= 0x20 and <= 0x2F:
                AppendEscIntermediate(value);
                _state = State.EscapeIntermediate;
                break;
            case >= 0x30 and <= 0x7E:
                _state = State.Ground;
                _dispatch.EscDispatch((char)value, 0);
                break;
            default:
                _state = State.Ground;
                break;
        }
    }

    private void ProcessEscapeIntermediate(byte value)
    {
        if (value is >= 0x20 and <= 0x2F)
        {
            AppendEscIntermediate(value);
        }
        else if (value is >= 0x30 and <= 0x7E)
        {
            _state = State.Ground;
            _dispatch.EscDispatch((char)value, _escIntermediates.AsSpan(0, _escIntermediateCount));
        }
        else
        {
            _state = State.Ground;
        }
    }

    private void ProcessCsiEntry(byte value)
    {
        if (value is >= 0x3C and <= 0x3F)
        {
            _privateMarker = value;
            _state = State.CsiParam;
        }
        else
        {
            ProcessCsiParam(value);
        }
    }

    private void ProcessCsiParam(byte value)
    {
        if (value is >= (byte)'0' and <= (byte)'9')
        {
            _currentParameter = _currentParameter < 0 ? 0 : _currentParameter;
            _currentParameter = Math.Min((_currentParameter * 10) + (value - '0'), 65535);
            _state = State.CsiParam;
        }
        else if (value == (byte)';')
        {
            PushParameter();
            _nextIsSubparameter = false;
        }
        else if (value == (byte)':')
        {
            PushParameter();
            _nextIsSubparameter = true;
        }
        else if (value is >= 0x20 and <= 0x2F)
        {
            PushParameterIfNeeded();
            _intermediate = value;
            _state = State.CsiIntermediate;
        }
        else if (IsFinal(value))
        {
            DispatchCsi(value);
        }
        else if (value is >= 0x3C and <= 0x3F)
        {
            _state = State.CsiIgnore;
        }
    }

    private void ProcessCsiIntermediate(byte value)
    {
        if (value is >= 0x20 and <= 0x2F)
        {
            _intermediate = value;
        }
        else if (IsFinal(value))
        {
            DispatchCsi(value);
        }
        else
        {
            _state = State.CsiIgnore;
        }
    }

    private void DispatchCsi(byte final)
    {
        if (_state != State.CsiIntermediate)
        {
            PushParameterIfNeeded();
        }

        var enterVt52 = final == (byte)'l' &&
                        _privateMarker == (byte)'?' &&
                        _parameterCount == 1 &&
                        _parameters[0] == 2;
        _state = State.Ground;
        var parameterCount = _parameterCount;
        var intermediate = _intermediate;
        var privateMarker = _privateMarker;
        // Copies keep the dispatched values stable if the handler resets the
        // parser. Reused scratch arrays avoid per-sequence heap arrays and the
        // zeroing a stackalloc pays; a handler that feeds this parser again
        // gets fresh copies so it cannot overwrite the outer sequence.
        var nested = _csiDispatchDepth > 0;
        var parameters = nested ? new int[parameterCount] : _csiParameters;
        var subparameters = nested ? new bool[parameterCount] : _csiSubparameters;
        // Usually a handful of values: a loop beats a Memmove call.
        for (var index = 0; index < parameterCount; index++)
        {
            parameters[index] = _parameters[index];
            subparameters[index] = _subparameters[index];
        }
        ClearSequence();
        _csiDispatchDepth++;
        try
        {
            _dispatch.CsiDispatch(
                (char)final,
                parameters.AsSpan(0, parameterCount),
                subparameters.AsSpan(0, parameterCount),
                intermediate,
                privateMarker);
        }
        finally
        {
            _csiDispatchDepth--;
        }

        if (enterVt52)
        {
            _ansiMode = false;
        }
    }

    private void ProcessOsc(byte value)
    {
        if (value is 0x18 or 0x1A)
        {
            _osc.Clear();
            _state = State.Ground;
            _dispatch.ExecuteC0(value);
        }
        else if (value == 0x07)
        {
            FinishOsc();
        }
        else if (value == 0x1B)
        {
            _state = State.OscEscape;
        }
        else if (value >= 0x20 || value == 0x09)
        {
            AppendOsc(value);
        }
    }

    private void AppendOsc(byte value)
    {
        if (_osc.Count < MaxOscStringBytes)
        {
            _osc.Add(value);
        }
        else
        {
            _osc.Clear();
            _state = State.StringIgnore;
        }
    }

    private void FinishOsc()
    {
        var text = Encoding.UTF8.GetString(_osc.ToArray());
        var separator = text.IndexOf(';');
        var command = -1;
        ReadOnlySpan<char> data = [];
        if (separator >= 0)
        {
            if (int.TryParse(text.AsSpan(0, separator), out var parsed))
            {
                command = parsed;
            }

            data = text.AsSpan(separator + 1);
        }
        else if (int.TryParse(text, out var bare))
        {
            command = bare;
        }

        if (command >= 0)
        {
            _dispatch.OscDispatch(command, data);
        }

        _osc.Clear();
        _state = State.Ground;
    }

    private void EnterCsi()
    {
        ClearSequence();
        _state = State.CsiEntry;
    }

    private void EnterOsc()
    {
        _osc.Clear();
        _state = State.OscString;
    }

    private void EnterApc()
    {
        _apc.Clear();
        _state = State.ApcString;
    }

    private void ProcessApc(byte value)
    {
        // ECMA-48: APC is terminated by ST only — BEL does not end it.
        if (value is 0x18 or 0x1A)
        {
            _apc.Clear();
            _state = State.Ground;
            _dispatch.ExecuteC0(value);
        }
        else if (value == 0x1B)
        {
            _state = State.ApcEscape;
        }
        else if (value >= 0x20 || value == 0x09)
        {
            AppendApc(value);
        }
    }

    private void AppendApc(byte value)
    {
        if (_apc.Count < MaxStringBytes)
        {
            _apc.Add(value);
        }
        else
        {
            _apc.Clear();
            _state = State.StringIgnore;
        }
    }

    private void FinishApc()
    {
        var text = Encoding.UTF8.GetString(_apc.ToArray());
        _apc.Clear();
        _state = State.Ground;
        _dispatch.ApcDispatch(text);
    }

    private void EnterDcs()
    {
        ClearSequence();
        ClearDcs();
        _state = State.DcsEntry;
    }

    private void ProcessDcs(byte value)
    {
        if (value is 0x18 or 0x1A)
        {
            CancelDcs();
            _dispatch.ExecuteC0(value);
            return;
        }

        if (_state == State.DcsEscape)
        {
            if (value == (byte)'\\')
            {
                TerminateDcs(_dcsEscapeCanDispatch);
            }
            else
            {
                CancelDcs();
                _state = State.Escape;
                ProcessByte(value);
            }

            return;
        }

        if (value == 0x1B)
        {
            _dcsEscapeCanDispatch = _state == State.DcsPassthrough;
            _state = State.DcsEscape;
            return;
        }

        if (value < 0x20)
        {
            _dispatch.ExecuteC0(value);
            return;
        }

        if (value == 0x7F)
        {
            return;
        }

        switch (_state)
        {
            case State.DcsEntry:
                ProcessDcsEntry(value);
                break;
            case State.DcsParam:
                ProcessDcsParam(value);
                break;
            case State.DcsIntermediate:
                ProcessDcsIntermediate(value);
                break;
            case State.DcsPassthrough:
                AppendDcs(value);
                break;
            case State.DcsIgnore:
                break;
        }
    }

    private void ProcessDcsEntry(byte value)
    {
        if (value is >= 0x3C and <= 0x3F)
        {
            _privateMarker = value;
            _state = State.DcsParam;
        }
        else if (value is >= (byte)'0' and <= (byte)'9' or (byte)';')
        {
            _state = State.DcsParam;
            ProcessDcsParam(value);
        }
        else if (value == (byte)':')
        {
            _state = State.DcsIgnore;
        }
        else if (value is >= 0x20 and <= 0x2F)
        {
            AppendDcsIntermediate(value);
        }
        else if (IsFinal(value))
        {
            StartDcsPassthrough(value);
        }
        else
        {
            _state = State.DcsIgnore;
        }
    }

    private void ProcessDcsParam(byte value)
    {
        if (value is >= (byte)'0' and <= (byte)'9')
        {
            _currentParameter = _currentParameter < 0 ? 0 : _currentParameter;
            _currentParameter = Math.Min((_currentParameter * 10) + (value - '0'), 65535);
        }
        else if (value == (byte)';')
        {
            PushDcsParameter();
        }
        else if (value == (byte)':' || value is >= 0x3C and <= 0x3F)
        {
            _state = State.DcsIgnore;
        }
        else if (value is >= 0x20 and <= 0x2F)
        {
            PushDcsParameterIfNeeded();
            if (_state != State.DcsIgnore)
            {
                AppendDcsIntermediate(value);
            }
        }
        else if (IsFinal(value))
        {
            PushDcsParameterIfNeeded();
            if (_state != State.DcsIgnore)
            {
                StartDcsPassthrough(value);
            }
        }
        else
        {
            _state = State.DcsIgnore;
        }
    }

    private void ProcessDcsIntermediate(byte value)
    {
        if (value is >= 0x20 and <= 0x2F)
        {
            AppendDcsIntermediate(value);
        }
        else if (IsFinal(value))
        {
            StartDcsPassthrough(value);
        }
        else
        {
            _state = State.DcsIgnore;
        }
    }

    private void AppendDcsIntermediate(byte value)
    {
        if (_dcsIntermediateCount == _dcsIntermediates.Length)
        {
            _state = State.DcsIgnore;
            return;
        }

        _dcsIntermediates[_dcsIntermediateCount++] = value;
        _state = State.DcsIntermediate;
    }

    private void StartDcsPassthrough(byte final)
    {
        _dcsFinal = final;
        _state = State.DcsPassthrough;
    }

    private void AppendDcs(byte value)
    {
        if (_dcs.Count == MaxDcsPayloadBytes)
        {
            _dcs.Clear();
            _state = State.DcsIgnore;
            return;
        }

        _dcs.Add(value);
    }

    private void TerminateDcs(bool dispatch)
    {
        if (!dispatch)
        {
            CancelDcs();
            return;
        }

        var final = (char)_dcsFinal;
        var parameters = _parameters.AsSpan(0, _parameterCount).ToArray();
        var intermediates = _dcsIntermediates.AsSpan(0, _dcsIntermediateCount).ToArray();
        var privateMarker = _privateMarker;
        var data = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_dcs).ToArray();
        CancelDcs();
        _dispatch.DcsDispatch(final, parameters, intermediates, privateMarker, data);
    }

    private void CancelDcs()
    {
        ClearDcs();
        ClearSequence();
        _state = State.Ground;
    }

    private void ClearDcs()
    {
        _dcs.Clear();
        _dcsFinal = 0;
        _dcsIntermediateCount = 0;
        _dcsEscapeCanDispatch = false;
    }

    private void PushDcsParameter()
    {
        if (_parameterCount >= _parameters.Length)
        {
            _state = State.DcsIgnore;
            return;
        }

        _parameters[_parameterCount++] = _currentParameter;
        _currentParameter = -1;
    }

    private void PushDcsParameterIfNeeded()
    {
        if (_currentParameter >= 0 || _parameterCount > 0)
        {
            PushDcsParameter();
        }
    }

    private void PushParameter()
    {
        if (_parameterCount >= _parameters.Length)
        {
            _state = State.CsiIgnore;
            return;
        }

        _subparameters[_parameterCount] = _nextIsSubparameter;
        _parameters[_parameterCount++] = _currentParameter;
        _currentParameter = -1;
    }

    private void PushParameterIfNeeded()
    {
        if (_currentParameter >= 0 || _parameterCount > 0)
        {
            PushParameter();
        }
    }

    private void ClearSequence()
    {
        // PushParameter sets flags only below the count, so only those can be set.
        for (var index = 0; index < _parameterCount; index++)
        {
            _subparameters[index] = false;
        }

        _parameterCount = 0;
        _currentParameter = -1;
        _intermediate = 0;
        _privateMarker = 0;
        _escIntermediateCount = 0;
        _nextIsSubparameter = false;
    }

    private void AppendEscIntermediate(byte value)
    {
        if (_escIntermediateCount < _escIntermediates.Length)
        {
            _escIntermediates[_escIntermediateCount++] = value;
        }
    }

    private void StartUtf8(int accumulator, int needed, int minimum)
    {
        _utf8Accumulator = accumulator;
        _utf8Needed = needed;
        _utf8Minimum = minimum;
    }

    private void EmitIncompleteUtf8()
    {
        if (_utf8Needed > 0)
        {
            EmitReplacement();
            ResetUtf8();
        }
    }

    private void EmitReplacement() => _dispatch.Print(Rune.ReplacementChar);

    private void ResetUtf8()
    {
        _utf8Needed = 0;
        _utf8Accumulator = 0;
        _utf8Minimum = 0;
    }

    private static bool IsFinal(byte value) => value is >= 0x40 and <= 0x7E;

    private static bool IsPrintableAscii(byte value) => value is >= 0x20 and <= 0x7E;
}
