using System.Text;
using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Core.Tests;

/// <summary>
/// <see cref="VtParser"/> parses complete plain CSI sequences in one pass. Fed one
/// byte at a time it can never see a whole sequence, so it uses only the state
/// machine; every dispatch must be identical either way.
/// </summary>
public sealed class CsiFastPathTests
{
    private const string Esc = "\u001b";

    public static TheoryData<string, string> Streams => new()
    {
        { "plain", $"{Esc}[m{Esc}[1;31m{Esc}[38;2;10;20;30;48;5;200mX{Esc}[12;40H{Esc}[K" },
        { "private marker", $"{Esc}[?25l{Esc}[?1049h{Esc}[>4;2m{Esc}[=1c{Esc}[<0;10;5M" },
        { "colon subparameters", $"{Esc}[4:3m{Esc}[38:2::1:2:3m{Esc}[58:5:9m" },
        { "empty parameters", $"{Esc}[;5H{Esc}[3;;7r{Esc}[;m" },
        { "large values", $"{Esc}[99999;123456789H{Esc}[65535;65536m" },
        { "31 separators", $"{Esc}[{string.Join(';', Enumerable.Range(1, 32))}m" },
        { "32 separators", $"{Esc}[{string.Join(';', Enumerable.Range(1, 33))}m" },
        { "33 separators", $"{Esc}[{string.Join(';', Enumerable.Range(1, 34))}mafter" },
        { "intermediate", $"{Esc}[2 q{Esc}[!p{Esc}[1\"q" },
        { "control inside", $"{Esc}[1\r;2H{Esc}[3\n1m{Esc}[\u0007m" },
        { "delete inside", $"{Esc}[1\u007f;2H" },
        { "escape inside", $"{Esc}[1;{Esc}[2Jx" },
        { "cancel inside", $"{Esc}[1;3\u0018m{Esc}[1\u001am" },
        { "marker after parameter", $"{Esc}[1?2h{Esc}[1;<3m" },
        { "high byte inside", "\u001b[1\u00E9;2H" },
        { "unterminated", $"{Esc}[1;2" },
        { "vt52 mode", $"{Esc}[?2lAB{Esc}[H{Esc}<{Esc}[2J" },
        { "after utf8", $"\u00E9{Esc}[1m\u4E00{Esc}[0m" },
    };

    [Theory]
    [MemberData(nameof(Streams))]
    public void WholeAndBytewiseFeedsDispatchIdentically(string name, string stream) =>
        AssertSameDispatch(name, Encoding.UTF8.GetBytes(stream), new Random(name.Length));

    [Fact]
    public void RandomCsiStreamsDispatchIdentically()
    {
        string[] pieces =
        [
            "\u001b[", "\u001b[?", "\u001b[>", "0", "1", "5", "38", "48", "2", "255", "65535", "99999",
            ";", ";", ":", "m", "H", "J", "K", "h", "l", "r", " ", "!", "?", "\r", "\n", "\u0007",
            "\u007f", "\u001b", "\u0018", "x", "y", "\u00E9", "\u4E00",
        ];
        var random = new Random(77);
        for (var iteration = 0; iteration < 500; iteration++)
        {
            var builder = new StringBuilder();
            for (var piece = 0; piece < 40; piece++)
            {
                builder.Append(pieces[random.Next(pieces.Length)]);
            }

            AssertSameDispatch($"random #{iteration}", Encoding.UTF8.GetBytes(builder.ToString()), random);
        }
    }

    [Fact]
    public void RandomUtf8StreamsDispatchIdentically()
    {
        // Well-formed sequences take the one-pass decode; everything malformed or
        // interrupted must still reach the state machine unchanged.
        byte[][] pieces =
        [
            [0xC3, 0xA9], [0xE4, 0xB8, 0x80], [0xF0, 0x9F, 0x98, 0x80], [0xEF, 0xBF, 0xBD],
            [0xC0, 0xAF], [0xC1, 0xBF], [0xE0, 0x80, 0xAF], [0xED, 0xA0, 0x80], [0xF4, 0x90, 0x80, 0x80],
            [0xF5, 0x80], [0xFF], [0x80], [0xBF], [0xE4, 0xB8], [0xF0, 0x9F], [0xC3],
            [0x9B, (byte)'1', (byte)'m'], [0x9D], [0x1B, (byte)'[', (byte)'m'], [0x1B], [0x0A], [0x41], [0x7F],
        ];
        var random = new Random(99);
        for (var iteration = 0; iteration < 500; iteration++)
        {
            var bytes = new List<byte>();
            for (var piece = 0; piece < 30; piece++)
            {
                bytes.AddRange(pieces[random.Next(pieces.Length)]);
            }

            AssertSameDispatch($"utf8 #{iteration}", [.. bytes], random);
        }
    }

    [Fact]
    public void RandomWideSequenceRunsDispatchIdentically()
    {
        // Runs of 3- and 4-byte sequences are decoded four at a time; any block
        // holding one malformed or out-of-range sequence must fall back and decode
        // exactly as the byte path does. Edge scalars sit next to the rejected forms.
        int[] threeByteScalars = [0x800, 0xD7FF, 0xE000, 0xFFFF, 0x4E00, 0x9FFF, 0x3042];
        int[] fourByteScalars = [0x10000, 0x10FFFF, 0x1F600, 0x1F64F, 0x20000];
        byte[][] malformed =
        [
            [0xE0, 0x9F, 0xBF], // overlong U+07FF
            [0xED, 0xA0, 0x80], // surrogate U+D800
            [0xED, 0xBF, 0xBF], // surrogate U+DFFF
            [0xF0, 0x8F, 0xBF, 0xBF], // overlong U+FFFF
            [0xF4, 0x90, 0x80, 0x80], // U+110000
            [0xF5, 0x80, 0x80, 0x80], // lead past F4
            [0xF7, 0xBF, 0xBF, 0xBF],
            [0xF8, 0x90, 0x80, 0x80], // never a lead; its low bits would give U+10000
            [0xFC, 0x80, 0x80, 0x80], // likewise U+100000
            [0xE4, 0xB8], // truncated
            [0xF0, 0x9F, 0x98], // truncated
            [0xF1, 0x80, 0x80], // truncated, would read as a 3-byte sequence past U+0800
            [0xF4, 0x8F, 0xBF], // truncated, likewise
            [0xE4, 0xC3, 0xA9], // lead, then a 2-byte sequence where continuations belong
            [0xE4, 0xB8, 0xC3], // lead, continuation, then a 2-byte lead
            [0xF0, 0x9F, 0x98, 0xC3], // three of four bytes, then a 2-byte lead
            [0xF0, 0xC3, 0x98, 0x80],
            [0x80], [0xBF], [0xC3, 0xA9], [0x0A], [0x1B, (byte)'[', (byte)'m'],
        ];
        byte[] soup = [0x80, 0xBF, 0xC3, 0xE4, 0xED, 0xEF, 0xF0, 0xF4, 0xF5, 0x41];
        var random = new Random(2027);
        for (var iteration = 0; iteration < 400; iteration++)
        {
            var bytes = new List<byte>();
            for (var piece = 0; piece < 12; piece++)
            {
                var kind = random.Next(12);
                if (kind == 8)
                {
                    bytes.AddRange(malformed[random.Next(malformed.Length)]);
                }
                else if (kind == 9)
                {
                    // Random lead and continuation bytes in any order.
                    for (var count = random.Next(1, 5); count > 0; count--)
                    {
                        bytes.Add(soup[random.Next(soup.Length)]);
                    }
                }
                else if (kind == 10)
                {
                    // A short ASCII stretch joins the run and shifts block alignment
                    // within the 256-rune buffer the parser decodes into.
                    for (var count = random.Next(1, 4); count > 0; count--)
                    {
                        bytes.Add((byte)random.Next(0x21, 0x7F));
                    }
                }
                else
                {
                    // A run of one length class: usually long enough for a few blocks,
                    // sometimes longer than the 256-rune buffer.
                    var four = kind is >= 4 and < 8;
                    var length = kind == 11 ? random.Next(250, 300) : random.Next(1, 14);
                    for (var index = 0; index < length; index++)
                    {
                        var scalar = random.Next(3) == 0
                            ? (four ? fourByteScalars : threeByteScalars)[random.Next(four ? fourByteScalars.Length : threeByteScalars.Length)]
                            : four ? random.Next(0x10000, 0x110000) : RandomThreeByteScalar(random);
                        bytes.AddRange(Encoding.UTF8.GetBytes(char.ConvertFromUtf32(scalar)));
                    }
                }
            }

            AssertSameDispatch($"wide runs #{iteration}", [.. bytes], random);
        }

        static int RandomThreeByteScalar(Random random)
        {
            while (true)
            {
                var scalar = random.Next(0x800, 0x10000);
                if (scalar is < 0xD800 or > 0xDFFF)
                {
                    return scalar;
                }
            }
        }
    }

    [Fact]
    public void NestedCsiLeavesTheOuterParametersIntact()
    {
        // The one-pass parse writes into reused dispatch arrays; a handler that
        // feeds the parser again must not overwrite the sequence it is handling.
        var dispatch = new ReentrantDispatch();
        dispatch.Parser = new VtParser(dispatch);
        dispatch.Parser.Process(Encoding.UTF8.GetBytes($"{Esc}[1;2:3m"));

        Assert.Equal(["H [9,8] [False,False]", "m [1,2,3] [False,False,True]"], dispatch.Calls);
    }

    private static void AssertSameDispatch(string name, byte[] bytes, Random random)
    {
        var bytewise = new RecordingDispatch();
        var bytewiseParser = new VtParser(bytewise);
        foreach (var value in bytes)
        {
            bytewiseParser.Process([value]);
        }

        var whole = new RecordingDispatch();
        new VtParser(whole).Process(bytes);

        // Random chunk boundaries also split sequences mid-way.
        var chunked = new RecordingDispatch();
        var chunkedParser = new VtParser(chunked);
        for (var offset = 0; offset < bytes.Length;)
        {
            var length = Math.Min(bytes.Length - offset, random.Next(1, 12));
            chunkedParser.Process(bytes.AsSpan(offset, length));
            offset += length;
        }

        Assert.True(bytewise.Calls.SequenceEqual(whole.Calls), $"{name} (whole):\n{Diff(bytewise.Calls, whole.Calls)}");
        Assert.True(bytewise.Calls.SequenceEqual(chunked.Calls), $"{name} (chunked):\n{Diff(bytewise.Calls, chunked.Calls)}");
    }

    private static string Diff(List<string> expected, List<string> actual)
    {
        var index = 0;
        while (index < expected.Count && index < actual.Count && expected[index] == actual[index])
        {
            index++;
        }

        return $"first difference at call {index}: expected '{(index < expected.Count ? expected[index] : "<end>")}' " +
               $"actual '{(index < actual.Count ? actual[index] : "<end>")}'";
    }

    private sealed class RecordingDispatch : IVtDispatch
    {
        public List<string> Calls { get; } = [];

        public void Print(Rune rune) => Calls.Add($"print {rune.Value:X}");

        public void ExecuteC0(byte control) => Calls.Add($"c0 {control:X2}");

        public void EscDispatch(char final, byte intermediate) => Calls.Add($"esc {final} {intermediate}");

        public void EscDispatch(char final, ReadOnlySpan<byte> intermediates) =>
            Calls.Add($"esc {final} [{string.Join(',', intermediates.ToArray())}]");

        public void Vt52Dispatch(char final, byte row = 0, byte column = 0) => Calls.Add($"vt52 {final} {row} {column}");

        public void CsiDispatch(char final, ReadOnlySpan<int> parameters, byte intermediate, bool privateMarker) =>
            throw new InvalidOperationException("the parser dispatches the detailed overload");

        public void CsiDispatch(
            char final,
            ReadOnlySpan<int> parameters,
            ReadOnlySpan<bool> subparameters,
            byte intermediate,
            byte privateMarker) =>
            Calls.Add(
                $"csi {final} [{string.Join(',', parameters.ToArray())}] " +
                $"[{string.Join(',', subparameters.ToArray())}] {intermediate} {privateMarker}");

        public void DcsDispatch(
            char final,
            ReadOnlySpan<int> parameters,
            ReadOnlySpan<byte> intermediates,
            byte privateMarker,
            ReadOnlySpan<byte> data) =>
            Calls.Add($"dcs {final} [{string.Join(',', parameters.ToArray())}] {Convert.ToHexString(data)}");

        public void OscDispatch(int command, ReadOnlySpan<char> data) => Calls.Add($"osc {command} {data.ToString()}");

        public void ApcDispatch(ReadOnlySpan<char> data) => Calls.Add($"apc {data.ToString()}");
    }

    // Handles SGR by feeding a cursor move first, then records the SGR it was given.
    private sealed class ReentrantDispatch : IVtDispatch
    {
        public VtParser Parser { get; set; } = null!;

        public List<string> Calls { get; } = [];

        public void Print(Rune rune)
        {
        }

        public void ExecuteC0(byte control)
        {
        }

        public void EscDispatch(char final, byte intermediate)
        {
        }

        public void CsiDispatch(char final, ReadOnlySpan<int> parameters, byte intermediate, bool privateMarker) =>
            throw new InvalidOperationException("the parser dispatches the detailed overload");

        public void CsiDispatch(
            char final,
            ReadOnlySpan<int> parameters,
            ReadOnlySpan<bool> subparameters,
            byte intermediate,
            byte privateMarker)
        {
            if (final == 'm')
            {
                Parser.Process(Encoding.UTF8.GetBytes($"{Esc}[9;8H"));
            }

            Calls.Add($"{final} [{string.Join(',', parameters.ToArray())}] [{string.Join(',', subparameters.ToArray())}]");
        }

        public void OscDispatch(int command, ReadOnlySpan<char> data)
        {
        }
    }
}
