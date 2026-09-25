using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Core.Tests;

[Collection("Unicode cell width observations")]
public sealed class GraphemeBreakFixtureTests(ITestOutputHelper output)
{
    private const string ResourceName =
        "Devolutions.Terminal.Core.Tests.UnicodeData._16._0._0.GraphemeBreakTest.txt";
    private const string ExpectedSha256 =
        "EE2B9354D270AC061B29F09662CAFEA06341D77E704B8CC6BD72AAEEDA363CB5";
    private static readonly Lazy<IReadOnlyList<FixtureRow>> Rows = new(LoadRows);

    private sealed record FixtureRow(int Line, string Hex, string[] Clusters, string? Unsupported);

    [Fact]
    public void OfficialFixtureIsCompleteAndEveryRowIsAccountedFor()
    {
        var rows = Rows.Value;
        Assert.Equal(1093, rows.Count);
        var counts = rows.GroupBy(row => row.Unsupported ?? "Representable")
            .ToDictionary(group => group.Key, group => group.Count());
        foreach (var row in rows.Where(row => row.Unsupported is not null))
        {
            output.WriteLine($"line {row.Line}: {row.Hex} => {row.Unsupported}");
        }

        output.WriteLine("Unicode 16.0.0 GraphemeBreakTest classification: " +
            string.Join(", ", counts.OrderBy(pair => pair.Key)
                .Select(pair => $"{pair.Key}={pair.Value}")));
        Assert.Equal(1093, counts.Values.Sum());
        Assert.Equal(0, rows.Count(row => row.Clusters.Length == 0));
        Assert.Equal(259, counts.GetValueOrDefault("TerminalControl"));
        Assert.Equal(78, counts.GetValueOrDefault("UnassignedScalar"));
        Assert.Equal(238, counts.GetValueOrDefault("OrphanZeroWidthCluster"));
        Assert.Equal(124, counts.GetValueOrDefault("DanglingPrepend"));
        Assert.Equal(25, counts.GetValueOrDefault("BlankCellCannotRetainExtenders"));
        Assert.Equal(369, counts.GetValueOrDefault("Representable"));
        Assert.Equal(1093, counts.Values.Sum());
        Assert.Equal(6, counts.Count);
    }

    [Theory]
    [InlineData("÷ 0041 ×\n")]
    [InlineData("× 0041 ÷\n")]
    [InlineData("÷ D800 ÷\n")]
    [InlineData("÷ 110000 ÷\n")]
    [InlineData("÷ ZZZZ ÷\n")]
    [InlineData("÷ 0041 ÷ ÷\n")]
    [InlineData("÷ 0041 Q 0042 ÷\n")]
    public void MalformedFixtureRowsFailWithLineNumber(string contents)
    {
        var error = Assert.Throws<FormatException>(() => Parse(contents));
        Assert.Contains("line 1", error.Message);
    }

    public static IEnumerable<object[]> RepresentableCases() =>
        Rows.Value.Where(row => row.Unsupported is null)
            .Select(row => new object[] { row.Line, row.Hex });

    [Fact]
    public void CrLfHasOnePureUnicodeFixtureClusterButNoPrintableTerminalCase()
    {
        // GB3 is an expectation of a pure segmenter. TextBuffer.Print and
        // TerminalEngine.Feed are not pure segmenters: CR/LF execute controls.
        var row = Rows.Value.Single(row => row.Line == 75);
        Assert.Equal("000D 000A", row.Hex);
        Assert.Equal(new[] { "\r\n" }, row.Clusters);
        Assert.Equal("TerminalControl", row.Unsupported);
        Assert.DoesNotContain(RepresentableCases(), entry => (int)entry[0] == row.Line);
    }

    [Theory]
    [InlineData(1087, "GB6 Hangul L+L", 1)]
    [InlineData(1088, "GB7/8 Hangul LV+T then L", 2)]
    [InlineData(1090, "GB12/13 RI odd/even", 3)]
    [InlineData(1095, "GB9 ZWJ", 1)]
    [InlineData(1096, "GB9 Extend", 2)]
    [InlineData(1097, "GB9a SpacingMark", 2)]
    [InlineData(1098, "GB9b Prepend", 2)]
    [InlineData(1103, "GB11 Extended_Pictographic ZWJ", 1)]
    [InlineData(1108, "GB9c Indic consonant linker", 1)]
    [InlineData(1110, "GB9c Indic linker ZWJ", 1)]
    [InlineData(1107, "GB999 ordinary adjacent bases", 2)]
    public void SupportedBoundaryFamilyHasAnExecutedRepresentableRow(
        int line, string family, int expectedClusters)
    {
        var row = Rows.Value.Single(row => row.Line == line);
        Assert.Null(row.Unsupported);
        Assert.Equal(expectedClusters, row.Clusters.Length);
        Assert.Contains(RepresentableCases(), entry => (int)entry[0] == line);
        output.WriteLine($"{family}: line {line} ({row.Hex})");
    }

    [Theory]
    [MemberData(nameof(RepresentableCases))]
    public void PrintableFixtureRowHasExactOrderedCellsAndCursor(int line, string hex)
    {
        var fixture = Rows.Value.Single(row => row.Line == line);
        Assert.Equal(hex, fixture.Hex);
        Assert.Null(fixture.Unsupported);
        // This asserts only terminal-representable UAX boundaries, not pure
        // segmenter conformance for control or unobservable fixture rows.
        // Avoid terminal wrapping, which is independent of grapheme breaks.
        var buffer = new TextBuffer(80, 2, 0, false);
        foreach (var rune in string.Concat(fixture.Clusters).EnumerateRunes())
        {
            buffer.Print(rune);
        }

        var column = 0;
        foreach (var cluster in fixture.Clusters)
        {
            var width = ExpectedTerminalWidth(cluster);
            Assert.True(column < 80, $"fixture line {line}, {hex}: row overflow");
            Assert.Equal(cluster, buffer.GetCell(column, 0).Text);
            Assert.Equal(width, buffer.GetCell(column, 0).DisplayWidth);
            Assert.False(buffer.GetCell(column, 0).IsWideContinuation);
            if (width == 2)
            {
                Assert.Equal("", buffer.GetCell(column + 1, 0).Text);
                Assert.True(buffer.GetCell(column + 1, 0).IsWideContinuation);
                Assert.Equal(0, buffer.GetCell(column + 1, 0).DisplayWidth);
            }

            column += width;
        }

        Assert.Equal(column, buffer.CursorX);
        Assert.Equal(0, buffer.CursorY);
        Assert.False(buffer.WrapPending);
        Assert.Equal(" ", buffer.GetCell(column, 0).Text);
    }

    private static int ExpectedTerminalWidth(string cluster)
    {
        var runes = cluster.EnumerateRunes().ToArray();
        if (runes.Count(rune => rune.Value is >= 0x1F1E6 and <= 0x1F1FF) == 2 ||
            runes.Any(rune => rune.Value == 0xFE0F) &&
                runes.Any(rune => rune.Value is 0x231A or 0x1F6D1 or 0x2701) ||
            runes.Any(rune => rune.Value == 0x200D) &&
                runes.Any(rune => rune.Value is 0x231A or 0x1F6D1 or 0x2701 or 0x1F476))
        {
            return 2;
        }

        return Math.Max(1, runes.Max(WcWidth.Width));
    }

    private static IReadOnlyList<FixtureRow> LoadRows()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded Unicode fixture: {ResourceName}");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        Assert.Equal(171927, bytes.Length);
        Assert.Equal(ExpectedSha256, Convert.ToHexString(SHA256.HashData(bytes)));
        var rows = Parse(new UTF8Encoding(false, true).GetString(bytes));
        Assert.Equal(1093, rows.Count);
        return rows;
    }

    private static IReadOnlyList<FixtureRow> Parse(string contents)
    {
        var rows = new List<FixtureRow>();
        var lines = contents.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var text = lines[i].Split('#', 2)[0].Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var clusters = new List<string>();
            var scalars = new List<int>();
            if (parts.Length < 3 || parts.Length % 2 == 0 || parts[0] != "÷" ||
                parts[^1] != "÷")
            {
                throw new FormatException($"Malformed fixture line {i + 1}: {text}");
            }

            for (var token = 1; token < parts.Length - 1; token += 2)
            {
                if (!int.TryParse(parts[token], NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture, out var value) ||
                    !Rune.IsValid(value) ||
                    parts[token + 1] is not ("÷" or "×"))
                {
                    throw new FormatException($"Malformed fixture line {i + 1}: {text}");
                }

                scalars.Add(value);
                var character = new Rune(value).ToString();
                if (token == 1 || parts[token - 1] == "÷")
                {
                    clusters.Add(character);
                }
                else
                {
                    clusters[^1] += character;
                }
            }

            // The marker before each scalar is validated separately from the trailing marker.
            for (var token = 2; token < parts.Length - 1; token += 2)
            {
                if (parts[token] is not ("÷" or "×"))
                {
                    throw new FormatException($"Malformed fixture line {i + 1}: {text}");
                }
            }

            var reason = Classify(clusters, scalars);
            rows.Add(new FixtureRow(i + 1, string.Join(" ", scalars.Select(
                scalar => scalar.ToString("X4", CultureInfo.InvariantCulture))),
                clusters.ToArray(), reason));
        }

        return rows;
    }

    private static string? Classify(IReadOnlyList<string> clusters, IReadOnlyList<int> scalars)
    {
        if (scalars.Any(value => value < 0x20 || value is >= 0x7F and <= 0x9F))
        {
            return "TerminalControl";
        }

        if (scalars.Any(value => Rune.GetUnicodeCategory(new Rune(value)) ==
                                 UnicodeCategory.OtherNotAssigned))
        {
            return "UnassignedScalar";
        }

        foreach (var cluster in clusters)
        {
            var runes = cluster.EnumerateRunes().ToArray();
            if (IsPrepend(runes[0]) &&
                !runes.Any(rune => !IsPrepend(rune) && WcWidth.Width(rune) > 0))
            {
                return "DanglingPrepend";
            }
        }

        if (clusters.Any(cluster =>
            {
                var first = cluster.EnumerateRunes().First();
                return WcWidth.Width(first) == 0 && !IsPrepend(first);
            }))
        {
            return "OrphanZeroWidthCluster";
        }

        if (clusters.Any(cluster => cluster[0] == ' ' && cluster.Length > 1))
        {
            return "BlankCellCannotRetainExtenders";
        }

        return null;
    }

    private static bool IsPrepend(Rune rune) =>
        rune.Value is 0x0600 or 0x0D4E;

    internal static string[] RepresentableClustersForStream(int line, string hex)
    {
        var row = Rows.Value.Single(row => row.Line == line);
        Assert.Equal(hex, row.Hex);
        Assert.Null(row.Unsupported);
        return row.Clusters;
    }
}
