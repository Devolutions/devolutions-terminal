using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Devolutions.Terminal.Core;
using Devolutions.Terminal.Ghostty;

namespace Devolutions.Terminal.Bench;

/// <summary>
/// Same-process, same-bytes VT ingest comparison between the builtin engine and
/// Ghostty. Three targets consume identical chunks:
///   builtin    — DT's own <see cref="TerminalEngine"/> (the default engine).
///   ghostty-vt — raw <c>ghostty_terminal_vt_write</c>: Ghostty's parser and
///                screen with nothing else, the bar the builtin engine must clear.
///   ghostty    — DT's <see cref="GhosttyTerminalEngine"/>, i.e. ghostty-vt plus
///                the per-feed render-state projection DT needs to paint it.
/// Runs interleave the targets in rotating order so thermal drift and
/// background load hit every target alike; fresh engines are built outside the
/// timed region. A verdict is only "faster"/"slower" when the run ranges do
/// not overlap.
/// </summary>
internal static partial class VtBench
{
    public const string Builtin = "builtin";
    public const string GhosttyVt = "ghostty-vt";
    public const string GhosttyDt = "ghostty";

    public static int Run(string[] args)
    {
        var megabytes = Program.GetOption(args, "--mb", 8);
        var runs = Program.GetOption(args, "--runs", 7);
        var warmup = Program.GetOption(args, "--warmup", 1, allowZero: true);
        var chunkSize = checked(Program.GetOption(args, "--chunk-kb", 16) * 1024);
        var minSeconds = Program.GetOption(args, "--min-ms", 500, allowZero: true) / 1000.0;
        // 0 measures pure ingest; N reads a frame every N KiB, as a renderer would.
        var frameBytes = checked(Program.GetOption(args, "--frame-kb", 0, allowZero: true) * 1024);
        var columns = Program.GetOption(args, "--columns", 120);
        var rows = Program.GetOption(args, "--rows", 30);
        var workloads = Split(Program.GetStringOption(args, "--workloads"), VtWorkloads.Names);
        var targets = Split(Program.GetStringOption(args, "--targets"), [Builtin, GhosttyVt, GhosttyDt]);
        var exportDirectory = Program.GetStringOption(args, "--export");
        var jsonPath = Program.GetStringOption(args, "--json");
        var dataPath = Program.GetStringOption(args, "--data");
        if (Array.IndexOf(args, "--list") >= 0)
        {
            Console.WriteLine(string.Join(Environment.NewLine, VtWorkloads.Names));
            return 0;
        }

        foreach (var target in targets)
        {
            if (target is not (Builtin or GhosttyVt or GhosttyDt))
            {
                throw new ArgumentException($"unknown target '{target}' (expected {Builtin}|{GhosttyVt}|{GhosttyDt})");
            }
        }

        Console.WriteLine(
            $"mode=vt grid={columns}x{rows} chunk={chunkSize / 1024} KiB " +
            $"frame={(frameBytes == 0 ? "off" : $"{frameBytes / 1024} KiB")} runs={runs} warmup={warmup} " +
            $"targets={string.Join(',', targets)} runtime={RuntimeInformation.FrameworkDescription} " +
            $"os={RuntimeInformation.OSDescription} arch={RuntimeInformation.ProcessArchitecture} " +
            $"cpus={Environment.ProcessorCount}");

        var inputs = dataPath is null
            ? workloads.Select(name => (Name: name, Bytes: VtWorkloads.Build(name, checked(megabytes * 1024 * 1024), columns, rows))).ToArray()
            : [(Name: Path.GetFileNameWithoutExtension(dataPath), Bytes: File.ReadAllBytes(dataPath))];

        if (exportDirectory is not null)
        {
            Directory.CreateDirectory(exportDirectory);
            foreach (var (name, bytes) in inputs)
            {
                var path = Path.Combine(exportDirectory, $"{name}.vt");
                File.WriteAllBytes(path, bytes);
                Console.WriteLine($"wrote {path} ({bytes.Length:N0} B sha256={Convert.ToHexString(SHA256.HashData(bytes))})");
            }

            return 0;
        }

        var report = new VtReport(
            DateTimeOffset.UtcNow,
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount,
            RuntimeInformation.FrameworkDescription,
            columns,
            rows,
            chunkSize,
            frameBytes,
            runs,
            []);
        var exitCode = 0;
        foreach (var (name, bytes) in inputs)
        {
            var sha = Convert.ToHexString(SHA256.HashData(bytes));
            Console.WriteLine();
            Console.WriteLine($"[{name}] {bytes.Length / (1024.0 * 1024):F1} MiB sha256={sha[..16]}… {VtWorkloads.Describe(name)}");
            var equivalence = targets.Contains(Builtin) && (targets.Contains(GhosttyVt) || targets.Contains(GhosttyDt))
                ? CheckEquivalence(bytes, columns, rows, chunkSize)
                : null;
            if (equivalence is not null)
            {
                Console.WriteLine($"  screen equivalence vs ghostty: {equivalence}");
            }

            var samples = targets.ToDictionary(target => target, _ => new List<VtSample>());
            for (var run = -warmup; run < runs; run++)
            {
                for (var slot = 0; slot < targets.Length; slot++)
                {
                    // Rotate the starting target every run to cancel ordering bias.
                    var target = targets[(slot + Math.Max(0, run)) % targets.Length];
                    var sample = Measure(target, bytes, columns, rows, chunkSize, minSeconds, frameBytes);
                    if (run >= 0)
                    {
                        samples[target].Add(sample);
                    }
                }
            }

            var results = targets.Select(target => Summarize(target, samples[target])).ToArray();
            foreach (var result in results)
            {
                Console.WriteLine(
                    $"  {result.Target,-10} median={result.MedianMBps,8:F1} MB/s  min={result.MinMBps,8:F1}  " +
                    $"max={result.MaxMBps,8:F1}  spread={result.SpreadPercent,5:F1}%  " +
                    $"alloc={result.AllocatedBytesPerMiB / 1024.0,9:F1} KiB/MiB  gc0/1/2 per MiB={result.Gen0PerMiB:F2}/{result.Gen1PerMiB:F2}/{result.Gen2PerMiB:F2}  " +
                    $"retained={result.RetainedMiB:F1} MiB");
            }

            string? verdict = null;
            double? ratio = null;
            var builtin = results.FirstOrDefault(result => result.Target == Builtin);
            var reference = results.FirstOrDefault(result => result.Target == GhosttyVt);
            if (builtin is not null && reference is not null)
            {
                ratio = builtin.MedianMBps / reference.MedianMBps;
                verdict = builtin.MinMBps > reference.MaxMBps ? "faster"
                    : builtin.MaxMBps < reference.MinMBps ? "slower"
                    : "overlap";
                Console.WriteLine($"  builtin vs ghostty-vt: {ratio:F2}x median throughput, verdict={verdict}");
                if (verdict != "faster")
                {
                    exitCode = 1;
                }
            }

            report.Workloads.Add(new VtWorkloadReport(name, bytes.Length, sha, equivalence, results, ratio, verdict));
        }

        if (jsonPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonPath))!);
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, JsonOptions));
            Console.WriteLine($"wrote {jsonPath}");
        }

        // A non-zero exit means "builtin did not beat ghostty-vt on every workload";
        // scripts use it as a gate, so --no-gate keeps a measurement-only run green.
        return Array.IndexOf(args, "--no-gate") >= 0 ? 0 : exitCode;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string[] Split(string? value, string[] fallback) =>
        value is null || value == "all"
            ? fallback
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static VtSample Measure(
        string target,
        byte[] bytes,
        int columns,
        int rows,
        int chunkSize,
        double minSeconds,
        int frameBytes)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var memoryBefore = PrivateBytes();
        using var engine = Create(target, columns, rows);
        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var passes = 0;
        var sinceFrame = 0L;
        var start = Stopwatch.GetTimestamp();
        // Fast targets replay the corpus into the same engine until the sample is
        // long enough for timer and scheduler noise to stop dominating it.
        do
        {
            for (var offset = 0; offset < bytes.Length; offset += chunkSize)
            {
                var length = Math.Min(chunkSize, bytes.Length - offset);
                engine.Feed(bytes.AsSpan(offset, length));
                sinceFrame += length;
                if (frameBytes > 0 && sinceFrame >= frameBytes)
                {
                    engine.Frame();
                    sinceFrame = 0;
                }
            }

            passes++;
        }
        while (Stopwatch.GetElapsedTime(start).TotalSeconds < minSeconds);

        var elapsed = Stopwatch.GetElapsedTime(start);
        var sample = new VtSample(
            (long)bytes.Length * passes,
            elapsed.TotalSeconds,
            GC.GetAllocatedBytesForCurrentThread() - allocated,
            GC.CollectionCount(0) - gen0,
            GC.CollectionCount(1) - gen1,
            GC.CollectionCount(2) - gen2,
            0);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return sample with { RetainedBytes = Math.Max(0, PrivateBytes() - memoryBefore) };
    }

    private static long PrivateBytes()
    {
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    private static IVtTarget Create(string target, int columns, int rows) => target switch
    {
        Builtin => new EngineTarget(new TerminalEngine(columns, rows)),
        GhosttyDt => new EngineTarget(new GhosttyTerminalEngine(columns, rows)),
        GhosttyVt => new RawGhosttyTarget(columns, rows),
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
    };

    /// <summary>
    /// Throughput is only comparable if both engines did the same work, so the
    /// final visible screen and cursor must match. A mismatch is reported (with the
    /// first differing row) rather than failing, because some workloads hit
    /// documented engine differences such as grapheme-width policy.
    /// </summary>
    private static string CheckEquivalence(byte[] bytes, int columns, int rows, int chunkSize)
    {
        using var builtin = new TerminalEngine(columns, rows);
        using var ghostty = new GhosttyTerminalEngine(columns, rows);
        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            var chunk = bytes.AsSpan(offset, Math.Min(chunkSize, bytes.Length - offset));
            builtin.Feed(chunk);
            ghostty.Feed(chunk);
        }

        var differing = 0;
        var first = -1;
        for (var row = 0; row < rows; row++)
        {
            var expected = builtin.Buffer.GetText(0, row, columns - 1, row).TrimEnd();
            var actual = ghostty.Buffer.GetText(0, row, columns - 1, row).TrimEnd();
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                differing++;
                first = first < 0 ? row : first;
            }
        }

        var cursor = builtin.CursorX == ghostty.CursorX && builtin.CursorY == ghostty.CursorY
            ? "cursor equal"
            : $"cursor builtin=({builtin.CursorX},{builtin.CursorY}) ghostty=({ghostty.CursorX},{ghostty.CursorY})";
        var history = builtin.HistoryCount == ghostty.HistoryCount
            ? $"history equal ({builtin.HistoryCount})"
            : $"history builtin={builtin.HistoryCount} ghostty={ghostty.HistoryCount}";
        return differing == 0
            ? $"identical text, {cursor}, {history}"
            : $"MISMATCH {differing}/{rows} rows (first row {first}), {cursor}, {history}";
    }

    private static VtTargetReport Summarize(string target, List<VtSample> samples)
    {
        static double Mebibytes(VtSample sample) => sample.Bytes / (1024.0 * 1024.0);
        var throughput = samples.Select(sample => Mebibytes(sample) / sample.Seconds).Order().ToArray();
        var median = Median(throughput);
        var fed = samples.Sum(Mebibytes);
        return new VtTargetReport(
            target,
            median,
            throughput[0],
            throughput[^1],
            (throughput[^1] - throughput[0]) / median * 100,
            Median(samples.Select(sample => sample.AllocatedBytes / Mebibytes(sample)).Order().ToArray()),
            samples.Sum(sample => sample.Gen0) / fed,
            samples.Sum(sample => sample.Gen1) / fed,
            samples.Sum(sample => sample.Gen2) / fed,
            Median(samples.Select(sample => sample.RetainedBytes / (1024.0 * 1024.0)).Order().ToArray()),
            throughput);
    }

    private static double Median(double[] sorted) =>
        sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;

    private interface IVtTarget : IDisposable
    {
        void Feed(ReadOnlySpan<byte> data);

        // What a renderer does once per frame to read the screen.
        void Frame();
    }

    private sealed class EngineTarget(ITerminalEngine engine) : IVtTarget
    {
        public void Feed(ReadOnlySpan<byte> data) => engine.Feed(data);

        public void Frame() => GC.KeepAlive(engine.CreateSnapshot());

        public void Dispose() => engine.Dispose();
    }

    /// <summary>
    /// The pinned libghostty-vt with only the scrollback limit configured to match
    /// the builtin engine's 9,001 lines; no effects or projection. A frame is what
    /// Ghostty's renderer does per frame: update its render state, then clean it.
    /// </summary>
    private sealed unsafe partial class RawGhosttyTarget : IVtTarget
    {
        private const int ScrollbackBytesOption = 27;
        private const int ScrollbackLinesOption = 28;
        private nint _terminal;
        private nint _renderState;

        public RawGhosttyTarget(int columns, int rows)
        {
            Check(TerminalNew(0, out _terminal, checked((ushort)columns), checked((ushort)rows)), "ghostty_terminal_new");
            try
            {
                Check(TerminalSet(_terminal, ScrollbackBytesOption, null), "remove scrollback byte limit");
                var lines = (nuint)9001;
                Check(TerminalSet(_terminal, ScrollbackLinesOption, &lines), "set scrollback line limit");
                Check(RenderStateNew(0, out _renderState), "ghostty_render_state_new");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Feed(ReadOnlySpan<byte> data)
        {
            fixed (byte* bytes = data)
            {
                TerminalWrite(_terminal, bytes, (nuint)data.Length);
            }
        }

        public void Frame()
        {
            Check(RenderStateUpdate(_renderState, _terminal), "ghostty_render_state_update");
            Check(RenderStateClean(_renderState), "ghostty_render_state_clean");
        }

        public void Dispose()
        {
            if (_renderState != 0)
            {
                RenderStateFree(_renderState);
                _renderState = 0;
            }

            if (_terminal != 0)
            {
                TerminalFree(_terminal);
                _terminal = 0;
            }
        }

        private static void Check(int result, string operation)
        {
            if (result != 0)
            {
                throw new InvalidOperationException($"{operation} failed with {result}");
            }
        }

        [LibraryImport("ghostty-vt", EntryPoint = "ghostty_terminal_new")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial int TerminalNew(nint allocator, out nint terminal, ushort columns, ushort rows);

        [LibraryImport("ghostty-vt", EntryPoint = "ghostty_terminal_set")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial int TerminalSet(nint terminal, int option, void* value);

        [LibraryImport("ghostty-vt", EntryPoint = "ghostty_terminal_vt_write")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial void TerminalWrite(nint terminal, byte* bytes, nuint length);

        [LibraryImport("ghostty-vt", EntryPoint = "ghostty_terminal_free")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial void TerminalFree(nint terminal);

        [LibraryImport("ghostty-vt", EntryPoint = "ghostty_render_state_new")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial int RenderStateNew(nint allocator, out nint state);

        [LibraryImport("ghostty-vt", EntryPoint = "ghostty_render_state_update")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial int RenderStateUpdate(nint state, nint terminal);

        [LibraryImport("ghostty-vt", EntryPoint = "ghostty_render_state_clean")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial int RenderStateClean(nint state);

        [LibraryImport("ghostty-vt", EntryPoint = "ghostty_render_state_free")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial void RenderStateFree(nint state);
    }

    private readonly record struct VtSample(
        long Bytes,
        double Seconds,
        long AllocatedBytes,
        int Gen0,
        int Gen1,
        int Gen2,
        long RetainedBytes);

    private sealed record VtReport(
        DateTimeOffset Timestamp,
        string Machine,
        string Os,
        string Architecture,
        int ProcessorCount,
        string Runtime,
        int Columns,
        int Rows,
        int ChunkBytes,
        int FrameBytes,
        int Runs,
        List<VtWorkloadReport> Workloads);

    private sealed record VtWorkloadReport(
        string Name,
        long Bytes,
        string Sha256,
        string? Equivalence,
        VtTargetReport[] Targets,
        double? BuiltinVsGhosttyVt,
        string? Verdict);

    private sealed record VtTargetReport(
        string Target,
        double MedianMBps,
        double MinMBps,
        double MaxMBps,
        double SpreadPercent,
        double AllocatedBytesPerMiB,
        double Gen0PerMiB,
        double Gen1PerMiB,
        double Gen2PerMiB,
        double RetainedMiB,
        double[] RunsMBps);
}
