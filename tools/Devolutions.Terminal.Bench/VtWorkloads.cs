using System.Text;

namespace Devolutions.Terminal.Bench;

/// <summary>
/// Deterministic VT byte streams for engine-ingest comparisons. Each workload
/// isolates one kind of terminal work so a regression or a gap against Ghostty
/// points at a code path instead of an average. The shapes follow
/// alacritty/vtebench (dense/light cells, cursor motion, scrolling regions,
/// unicode) and Ghostty's own `ghostty-gen` generators (+ascii, +utf8, +osc).
/// Generation is seeded, so the printed SHA-256 identifies the exact bytes.
/// </summary>
internal static class VtWorkloads
{
    private const string Esc = "\u001b";
    private const int Seed = 0x5EED;

    public static readonly string[] Names =
    [
        "ascii", "ascii-lines", "utf8", "cjk", "emoji", "styled", "dense-cells", "light-cells", "cursor-motion",
        "scrolling", "scroll-region", "osc", "mixed",
    ];

    public static byte[] Build(string name, int targetBytes, int columns, int rows) => name switch
    {
        "ascii" => AsciiStream(targetBytes),
        "ascii-lines" => Ascii(targetBytes, columns),
        "utf8" => Utf8(targetBytes, columns),
        "cjk" => SingleScript(targetBytes, columns, 0x4E00, 0x9FFF),
        "emoji" => SingleScript(targetBytes, columns, 0x1F600, 0x1F64F),
        "styled" => Styled(targetBytes),
        "dense-cells" => DenseCells(targetBytes, columns, rows),
        "light-cells" => LightCells(targetBytes, columns, rows),
        "cursor-motion" => CursorMotion(targetBytes, columns, rows),
        "scrolling" => Scrolling(targetBytes),
        "scroll-region" => ScrollRegion(targetBytes, columns, rows),
        "osc" => Osc(targetBytes, columns),
        "mixed" => Program.BuildMixedCorpus(targetBytes),
        _ => throw new ArgumentException(
            $"unknown workload '{name}' (expected {string.Join('|', Names)})"),
    };

    public static string Describe(string name) => name switch
    {
        "ascii" => "unbroken 0x21-0x7E stream relying on autowrap (`ghostty-gen ascii`)",
        "ascii-lines" => "printable ASCII lines with CRLF (`cat` of a log or source file)",
        "utf8" => "mixed-width UTF-8 lines: Latin, Greek, CJK, emoji, ZWJ, combining marks",
        "cjk" => "lines of CJK ideographs only (wide, 3-byte UTF-8)",
        "emoji" => "lines of emoji only (wide, 4-byte UTF-8, Extended_Pictographic)",
        "styled" => "CRLF lines of short styled runs, SGR + OSC 8 + wide text (`ghostty-gen styled`)",
        "dense-cells" => "full-screen repaints, every cell with its own truecolor/256 SGR",
        "light-cells" => "full-screen repaints of plain text from the home position",
        "cursor-motion" => "TUI-style CUP jumps with short writes and EL, no scrolling",
        "scrolling" => "short lines that scroll the whole screen into history (`yes`)",
        "scroll-region" => "DECSTBM partial-screen region scrolling (`less`, vim splits)",
        "osc" => "OSC titles, OSC 8 hyperlinks and OSC 133 prompt marks around text",
        "mixed" => "the legacy `engine`/`render` corpus (70% ASCII, SGR, CJK, CUP)",
        _ => name,
    };

    private static byte[] AsciiStream(int targetBytes)
    {
        var random = new Random(Seed);
        return Emit(targetBytes, writer =>
        {
            for (var index = 0; index < 1024; index++)
            {
                writer.Append((char)random.Next(0x21, 0x7F));
            }
        });
    }

    /// <summary>
    /// Mirrors the defaults of Ghostty's `styled` generator: 40-80 character lines
    /// split into 4-16 character runs, an SGR at a run boundary half of the time
    /// drawn uniformly from eight kinds (quantized RGB keeps the style set
    /// bounded), occasional OSC 8 links, and ASCII/Latin/CJK/emoji text with rare
    /// combining marks. Style and link are closed at the end of each line.
    /// </summary>
    private static byte[] Styled(int targetBytes)
    {
        var random = new Random(Seed);
        int[] levels = [0, 51, 102, 153, 204, 255];
        int[] attributes = [1, 3, 4, 7, 9];
        var link = 0;
        return Emit(targetBytes, writer =>
        {
            var length = random.Next(40, 81);
            var linked = false;
            var written = 0;
            while (written < length)
            {
                if (random.Next(2) == 0)
                {
                    writer.Append(random.Next(8) switch
                    {
                        0 => $"{Esc}[0m",
                        1 => $"{Esc}[{random.Next(30, 38)}m",
                        2 => $"{Esc}[{random.Next(40, 48)}m",
                        3 => $"{Esc}[38;5;{random.Next(256)}m",
                        4 => $"{Esc}[48;5;{random.Next(256)}m",
                        5 => $"{Esc}[38;2;{levels[random.Next(6)]};{levels[random.Next(6)]};{levels[random.Next(6)]}m",
                        6 => $"{Esc}[48;2;{levels[random.Next(6)]};{levels[random.Next(6)]};{levels[random.Next(6)]}m",
                        _ => $"{Esc}[{attributes[random.Next(attributes.Length)]}m",
                    });
                }

                if (random.Next(50) == 0)
                {
                    writer.Append(linked
                        ? $"{Esc}]8;;\u0007"
                        : $"{Esc}]8;;https://example.test/{link++}\u0007");
                    linked = !linked;
                }

                var run = Math.Min(random.Next(4, 17), length - written);
                for (var index = 0; index < run; index++)
                {
                    var roll = random.Next(100);
                    if (roll < 80)
                    {
                        writer.Append(random.Next(100) < 15 ? ' ' : (char)random.Next(0x21, 0x7F));
                    }
                    else if (roll < 90)
                    {
                        writer.Append((char)random.Next(0x00C0, 0x0250));
                    }
                    else if (roll < 97)
                    {
                        writer.Append((char)random.Next(0x4E00, 0x5000));
                    }
                    else
                    {
                        writer.Append(char.ConvertFromUtf32(random.Next(0x1F600, 0x1F650)));
                    }

                    if (random.Next(100) == 0)
                    {
                        writer.Append((char)random.Next(0x0300, 0x0370));
                    }
                }

                written += run;
            }

            if (linked)
            {
                writer.Append($"{Esc}]8;;\u0007");
            }

            writer.Append($"{Esc}[0m\r\n");
        });
    }

    private static byte[] Ascii(int targetBytes, int columns)
    {
        var random = new Random(Seed);
        return Emit(targetBytes, writer =>
        {
            var length = random.Next(0, columns * 2);
            for (var index = 0; index < length; index++)
            {
                writer.Append((char)random.Next(0x20, 0x7F));
            }

            writer.Append("\r\n");
        });
    }

    private static readonly (int Start, int End)[] Utf8Ranges =
    [
        (0x00A1, 0x00FF), // Latin-1 supplement
        (0x0391, 0x03C9), // Greek
        (0x0410, 0x044F), // Cyrillic
        (0x4E00, 0x4FFF), // CJK ideographs (wide)
        (0x3041, 0x3096), // Hiragana (wide)
        (0x1F600, 0x1F64F), // emoji (wide)
        (0x2500, 0x257F), // box drawing
    ];

    private static byte[] Utf8(int targetBytes, int columns)
    {
        var random = new Random(Seed);
        var started = false;
        return Emit(targetBytes, writer =>
        {
            if (!started)
            {
                // DECSET 2027 (grapheme clustering): without it Ghostty measures a ZWJ
                // sequence per codepoint while the builtin engine clusters it, and the
                // two screens would wrap differently.
                writer.Append($"{Esc}[?2027h");
                started = true;
            }

            var length = random.Next(0, columns);
            for (var index = 0; index < length; index++)
            {
                var roll = random.Next(100);
                if (roll < 30)
                {
                    writer.Append((char)random.Next(0x21, 0x7F));
                }
                else if (roll < 33 && writer.Length > 0)
                {
                    // Combining acute accent on the previous base character.
                    writer.Append('\u0301');
                }
                else if (roll < 35)
                {
                    // ZWJ family sequence: one grapheme cluster, several codepoints.
                    writer.Append("\U0001F468\u200D\U0001F469\u200D\U0001F467");
                }
                else
                {
                    var (start, end) = Utf8Ranges[random.Next(Utf8Ranges.Length)];
                    writer.Append(char.ConvertFromUtf32(random.Next(start, end + 1)));
                }
            }

            writer.Append("\r\n");
        });
    }

    private static byte[] SingleScript(int targetBytes, int columns, int first, int last)
    {
        var random = new Random(Seed);
        return Emit(targetBytes, writer =>
        {
            // Wide characters: at most a row's worth per line.
            var length = random.Next(0, columns / 2);
            for (var index = 0; index < length; index++)
            {
                writer.Append(char.ConvertFromUtf32(random.Next(first, last + 1)));
            }

            writer.Append("\r\n");
        });
    }

    private static byte[] DenseCells(int targetBytes, int columns, int rows)
    {
        var random = new Random(Seed);
        var frame = 0;
        return Emit(targetBytes, writer =>
        {
            writer.Append($"{Esc}[H");
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    var attribute = random.Next(4) switch
                    {
                        0 => "1;",
                        1 => "3;",
                        2 => "4;",
                        _ => "",
                    };
                    if (random.Next(2) == 0)
                    {
                        writer.Append(
                            $"{Esc}[{attribute}38;2;{random.Next(256)};{random.Next(256)};{random.Next(256)};" +
                            $"48;2;{random.Next(256)};{random.Next(256)};{random.Next(256)}m");
                    }
                    else
                    {
                        writer.Append($"{Esc}[{attribute}38;5;{random.Next(256)};48;5;{random.Next(256)}m");
                    }

                    writer.Append((char)('A' + ((frame + row + column) % 26)));
                }
            }

            writer.Append($"{Esc}[0m");
            frame++;
        });
    }

    private static byte[] LightCells(int targetBytes, int columns, int rows)
    {
        var frame = 0;
        return Emit(targetBytes, writer =>
        {
            writer.Append($"{Esc}[H");
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    writer.Append((char)('a' + ((frame + row + column) % 26)));
                }
            }

            frame++;
        });
    }

    private static byte[] CursorMotion(int targetBytes, int columns, int rows)
    {
        var random = new Random(Seed);
        return Emit(targetBytes, writer =>
        {
            var row = random.Next(1, rows + 1);
            var column = random.Next(1, columns + 1);
            writer.Append($"{Esc}[{row};{column}H");
            switch (random.Next(4))
            {
                case 0:
                    writer.Append($"{Esc}[K");
                    break;
                case 1:
                    writer.Append($"{Esc}[{random.Next(31, 38)}m{random.Next(1000):D3}%{Esc}[0m");
                    break;
                default:
                    writer.Append((char)random.Next(0x21, 0x7F));
                    break;
            }
        });
    }

    private static byte[] Scrolling(int targetBytes) =>
        Emit(targetBytes, writer => writer.Append("y\n"));

    private static byte[] ScrollRegion(int targetBytes, int columns, int rows)
    {
        var random = new Random(Seed);
        var top = Math.Max(1, rows / 4);
        var bottom = Math.Max(top + 1, rows - 2);
        var header = $"{Esc}[{top};{bottom}r{Esc}[{bottom};1H";
        var started = false;
        return Emit(targetBytes, writer =>
        {
            if (!started)
            {
                writer.Append(header);
                started = true;
            }

            var length = random.Next(1, columns);
            for (var index = 0; index < length; index++)
            {
                writer.Append((char)random.Next(0x20, 0x7F));
            }

            writer.Append("\r\n");
        });
    }

    private static byte[] Osc(int targetBytes, int columns)
    {
        var random = new Random(Seed);
        var line = 0;
        return Emit(targetBytes, writer =>
        {
            switch (random.Next(4))
            {
                case 0:
                    writer.Append($"{Esc}]0;job {line} - {random.Next(100)}% complete\u0007");
                    break;
                case 1:
                    writer.Append($"{Esc}]133;A\u0007PS C:\\src\\{line}> {Esc}]133;B\u0007dir{Esc}]133;C\u0007");
                    break;
                case 2:
                    writer.Append($"{Esc}]133;D;0\u0007");
                    break;
                default:
                    writer.Append(
                        $"{Esc}]8;id={line};https://example.test/item/{random.Next(1_000_000)}{Esc}\\" +
                        $"item-{line}{Esc}]8;;{Esc}\\");
                    break;
            }

            var length = random.Next(0, columns / 2);
            for (var index = 0; index < length; index++)
            {
                writer.Append((char)random.Next(0x20, 0x7F));
            }

            writer.Append("\r\n");
            line++;
        });
    }

    private static byte[] Emit(int targetBytes, Action<StringBuilder> next)
    {
        var builder = new StringBuilder();
        using var stream = new MemoryStream(targetBytes + 64 * 1024);
        while (stream.Length < targetBytes)
        {
            builder.Clear();
            next(builder);
            var bytes = Encoding.UTF8.GetBytes(builder.ToString());
            stream.Write(bytes);
        }

        return stream.ToArray();
    }
}
