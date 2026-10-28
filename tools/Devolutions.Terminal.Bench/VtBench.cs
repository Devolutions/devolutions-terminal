using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Devolutions.Terminal.Core;

namespace Devolutions.Terminal.Bench;

/// <summary>
/// Same-process, same-bytes VT ingest throughput of the builtin engine, measured
/// against an in-run control. Two targets consume identical chunks:
///   builtin — DT's <see cref="TerminalEngine"/>.
///   control — FNV-1a over the fed bytes: a serial loop that does no terminal
///             work, so its speed follows only the core clock and load.
/// Runs interleave the targets in rotating order so thermal drift and
/// background load hit both alike; fresh engines are built outside the timed
/// region. builtin/control is the number to compare across sessions, because
/// absolute MB/s drifts with the CPU's turbo state.
/// </summary>
internal static class VtBench
{
    public const string Builtin = "builtin";
    public const string Control = "control";

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
        var targets = Split(Program.GetStringOption(args, "--targets"), [Builtin, Control]);
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
            if (target is not (Builtin or Control))
            {
                throw new ArgumentException($"unknown target '{target}' (expected {Builtin}|{Control})");
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
        foreach (var (name, bytes) in inputs)
        {
            var sha = Convert.ToHexString(SHA256.HashData(bytes));
            Console.WriteLine();
            Console.WriteLine($"[{name}] {bytes.Length / (1024.0 * 1024):F1} MiB sha256={sha[..16]}… {VtWorkloads.Describe(name)}");

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

            double? ratio = null;
            var builtin = results.FirstOrDefault(result => result.Target == Builtin);
            var control = results.FirstOrDefault(result => result.Target == Control);
            if (builtin is not null && control is not null)
            {
                ratio = builtin.MedianMBps / control.MedianMBps;
                Console.WriteLine($"  builtin / control: {ratio:F4} median throughput");
            }

            report.Workloads.Add(new VtWorkloadReport(name, bytes.Length, sha, results, ratio));
        }

        if (jsonPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonPath))!);
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, JsonOptions));
            Console.WriteLine($"wrote {jsonPath}");
        }

        return 0;
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
        Control => new ControlTarget(),
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
    };

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
    /// FNV-1a over every fed byte. Each step depends on the previous one, so the
    /// loop cannot be vectorized and its speed tracks the core clock rather than
    /// memory bandwidth, which is what drifts between sessions.
    /// </summary>
    private sealed class ControlTarget : IVtTarget
    {
        private uint _hash = 2166136261;

        public void Feed(ReadOnlySpan<byte> data)
        {
            var hash = _hash;
            foreach (var value in data)
            {
                hash = (hash ^ value) * 16777619;
            }

            _hash = hash;
        }

        public void Frame()
        {
        }

        public void Dispose() => GC.KeepAlive(_hash);
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
        VtTargetReport[] Targets,
        double? BuiltinVsControl);

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
