using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Devolutions.Terminal.Connection;
using Devolutions.Terminal.Core;
using Devolutions.Terminal.Settings;

namespace Devolutions.Terminal.Bench;

/// <summary>
/// Throughput harness for the PTY -> engine -> invalidation path, modeled on the
/// winterm-ghostty methodology (fixed corpus, 16 KiB chunks like the ConPTY read loop,
/// medians over runs).
///
/// Modes:
///   engine  — pure TerminalEngine.Feed throughput, no UI.
///   control — full TermControl path: a producer thread raises OutputReceived like the
///             ConPTY ReadLoop; the UI dispatcher drains invalidations via RunJobs.
///             Reports engine invalidations vs posts vs actual UI drains (the
///             coalescing ratio).
///
/// Caveat: headless mode does not paint, so drain cost covers dispatch + listener
/// fan-out, not Skia rendering.
/// </summary>
internal static class Program
{
    private const string Esc = "\u001b";

    private static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "control";
        var megabytes = GetOption(args, "--mb", 8);
        var runs = GetOption(args, "--runs", 5);
        var chunkKb = GetOption(args, "--chunk-kb", 16);

        var corpus = Corpus.Build(megabytes * 1024 * 1024);
        Console.WriteLine($"mode={mode} corpus={corpus.Length / (1024.0 * 1024):F1} MiB chunk={chunkKb} KiB runs={runs}");

        if (mode == "control")
        {
            // Avalonia setup is process-global; do it once before any run.
            AppBuilder.Configure<Application>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                .SetupWithoutStarting();
            if (!Dispatcher.UIThread.CheckAccess())
            {
                throw new InvalidOperationException("headless setup did not bind the UI dispatcher to this thread");
            }
        }

        var samples = new List<double>();
        for (var run = 0; run < runs; run++)
        {
            var mbPerSec = mode switch
            {
                "engine" => RunEngine(corpus, chunkKb * 1024),
                "control" => RunControl(corpus, chunkKb * 1024),
                _ => throw new ArgumentException($"unknown mode '{mode}' (expected engine|control)"),
            };
            samples.Add(mbPerSec);
            Console.WriteLine($"  run {run + 1}: {mbPerSec:F1} MB/s");
        }

        samples.Sort();
        Console.WriteLine($"median: {samples[samples.Count / 2]:F1} MB/s  min: {samples[0]:F1}  max: {samples[^1]:F1}");
        return 0;
    }

    private static int GetOption(string[] args, string name, int fallback)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value)
            ? value
            : fallback;
    }

    private static double RunEngine(byte[] corpus, int chunkSize)
    {
        using var engine = new TerminalEngine();
        var watch = Stopwatch.StartNew();
        for (var offset = 0; offset < corpus.Length; offset += chunkSize)
        {
            engine.Feed(corpus.AsSpan(offset, Math.Min(chunkSize, corpus.Length - offset)));
        }

        watch.Stop();
        return corpus.Length / (1024.0 * 1024.0) / watch.Elapsed.TotalSeconds;
    }

    private static double RunControl(byte[] corpus, int chunkSize)
    {
        var connection = new FakeConnection();
        var control = new TermControl
        {
            ConnectionFactory = _ => connection,
        };
        long engineInvalidations = 0;
        control.Engine.Invalidated += (_, _) => Interlocked.Increment(ref engineInvalidations);
        // Emulate the App shell's scrollbar/notification listeners.
        control.ScrollMarksChanged += (_, _) => _ = control.Engine.HistoryCount;
        control.ViewportChanged += (_, _) => _ = control.Engine.ScrollOffset;
        control.AccessibilityTextChanged += (_, _) => _ = control.Engine.CursorY;

        control.StartAsync(new ProfileSettings { Name = "bench" }, columns: 120, rows: 30)
            .GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.SystemIdle);

        var watch = Stopwatch.StartNew();
        var producer = new Thread(() =>
        {
            for (var offset = 0; offset < corpus.Length; offset += chunkSize)
            {
                connection.Emit(corpus.AsMemory(offset, Math.Min(chunkSize, corpus.Length - offset)));
            }
        });
        producer.Start();

        // Drain posted invalidations while the producer is feeding, paced at one
        // display frame (60 Hz): in the real app the UI thread is busy rendering
        // between vsyncs, so per-chunk invalidations queue up within a frame.
        // Draining eagerly here would hide exactly the batching this measures.
        var markerRan = false;
        while (producer.IsAlive || !markerRan)
        {
            if (!producer.IsAlive && !markerRan)
            {
                // Marker at Send priority executes after every queued Render-priority drain.
                Dispatcher.UIThread.Post(() => markerRan = true, DispatcherPriority.Send);
            }

            Dispatcher.UIThread.RunJobs(DispatcherPriority.SystemIdle);
            Thread.Sleep(16);
        }

        watch.Stop();
        var mbPerSec = corpus.Length / (1024.0 * 1024.0) / watch.Elapsed.TotalSeconds;
        Console.WriteLine(
            $"    engine invalidations: {Interlocked.Read(ref engineInvalidations)}, " +
            $"posts: {control.InvalidationPosts}, drains: {control.InvalidationDrains}");
        return mbPerSec;
    }

    private sealed class FakeConnection : IRestartableTerminalConnection
    {
#pragma warning disable CS0067 // events required by the interface, unused by the bench
        public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
        public event EventHandler<int>? Exited;
        public event EventHandler<Exception>? Faulted;
        public event EventHandler<TerminalExitInfo>? SessionExited;
#pragma warning restore CS0067

        public bool IsRunning => true;
        public int Columns => 120;
        public int Rows => 30;
        public TerminalConnectionCapabilities Capabilities => TerminalConnectionCapabilities.None;
        public TerminalConnectionState State => TerminalConnectionState.Connected;
        public TerminalProcessMetadata? ProcessMetadata => null;
        public TerminalExitInfo? LastExitInfo => null;

        public void Emit(ReadOnlyMemory<byte> data) => OutputReceived?.Invoke(this, data);

        public Task StartAsync(TerminalLaunchOptions options, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StartAsync(string commandLine, string? workingDirectory, int columns, int rows, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void Write(ReadOnlySpan<byte> data)
        {
        }

        public void Write(string text)
        {
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public void Resize(int columns, int rows)
        {
        }

        public Task RestartAsync(TerminalLaunchOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static class Corpus
    {
        private static readonly string[] Sgr =
        [
            $"{Esc}[31m", $"{Esc}[32m", $"{Esc}[33m", $"{Esc}[34m",
            $"{Esc}[1m", $"{Esc}[0m", $"{Esc}[38;5;123m", $"{Esc}[48;5;240m",
        ];

        public static byte[] Build(int targetBytes)
        {
            var random = new Random(1234);
            using var stream = new MemoryStream(targetBytes + 4096);
            using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            var line = 0;
            while (stream.Length < targetBytes)
            {
                var roll = random.Next(100);
                if (roll < 70)
                {
                    writer.Write($"{line:D8}  The quick brown fox jumps over the lazy dog {random.Next(1_000_000):D6}");
                    if (roll % 3 == 0)
                    {
                        writer.Write(Sgr[random.Next(Sgr.Length)]);
                    }

                    writer.Write("  pack my box with five dozen liquor jugs\r\n");
                }
                else if (roll < 85)
                {
                    writer.Write(Sgr[random.Next(Sgr.Length)]);
                    writer.Write($"[INFO] worker-{random.Next(64)} processed batch {line} in {random.Next(900)}ms");
                    writer.Write($"{Esc}[0m\r\n");
                }
                else if (roll < 95)
                {
                    writer.Write($"進捗 {line}: 完了 ✅ テスト用文字列 🚀\r\n");
                }
                else
                {
                    writer.Write($"{Esc}[{random.Next(1, 30)};{random.Next(1, 110)}H");
                    writer.Write(Sgr[random.Next(Sgr.Length)]);
                    writer.Write($"status:{random.Next(100)}%");
                    writer.Write($"{Esc}[K");
                }

                line++;
            }

            writer.Flush();
            return stream.ToArray();
        }
    }
}
