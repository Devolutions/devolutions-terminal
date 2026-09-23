using System.Diagnostics;
using System.Runtime.InteropServices;
using Devolutions.Terminal.Core;
using Devolutions.Terminal.Render;
using SkiaSharp;

namespace Devolutions.Terminal.Bench;

internal static class RendererBench
{
    public static void Run(
        byte[] corpus,
        int columns,
        int rows,
        int chunkSize,
        int frameCount,
        int samplesPerRun,
        int warmupCycles,
        int runs,
        int glyphCacheCapacity,
        int rowCacheCapacity,
        bool reuseAsciiGlyphs,
        bool verify)
    {
        if (frameCount < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount), "at least two frames are needed to measure updates");
        }

        using var engine = new TerminalEngine(columns, rows);
        var scrollFrames = new Queue<TerminalRenderFrame>(frameCount);
        for (var offset = 0; offset < corpus.Length; offset += chunkSize)
        {
            engine.Feed(corpus.AsSpan(offset, Math.Min(chunkSize, corpus.Length - offset)));
            if (offset >= corpus.Length - (frameCount * (long)chunkSize))
            {
                scrollFrames.Enqueue(TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme));
            }
        }

        while (scrollFrames.Count < 2)
        {
            engine.Feed($"\r\nscroll frame {scrollFrames.Count}");
            scrollFrames.Enqueue(TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme));
        }

        var statusFrames = new TerminalRenderFrame[frameCount];
        for (var index = 0; index < statusFrames.Length; index++)
        {
            engine.Feed($"\u001b[1;1H\u001b[2Kstatus frame {index:D4} / {frameCount:D4}");
            statusFrames[index] = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);
        }

        var scroll = scrollFrames.ToArray();
        if (verify)
        {
            VerifyPaint([.. scroll, .. statusFrames], columns, rows, glyphCacheCapacity, rowCacheCapacity, reuseAsciiGlyphs);
            return;
        }

        var scheme = engine.Scheme;
        Console.WriteLine(
            $"grid={columns}x{rows} captured-scroll-frames={scroll.Length} " +
            $"status-frames={frameCount} samples/run={samplesPerRun} warmup-cycles={warmupCycles} " +
            $"glyph-cache={glyphCacheCapacity} row-cache={rowCacheCapacity} ascii-cache={reuseAsciiGlyphs}");

        var fixedSnapshot = engine.CreateSnapshot();
        Measure("snapshot", runs, samplesPerRun, warmupCycles, _ =>
        {
            var snapshot = engine.CreateSnapshot();
            GC.KeepAlive(snapshot);
        });
        Measure("plan", runs, samplesPerRun, warmupCycles, _ =>
        {
            var frame = TerminalRenderPlanner.Create(fixedSnapshot, scheme);
            GC.KeepAlive(frame);
        });
        Measure("snapshot+plan", runs, samplesPerRun, warmupCycles, _ =>
        {
            var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), scheme);
            GC.KeepAlive(frame);
        });
        MeasurePaint("paint-static", [statusFrames[^1]], columns, rows, runs, samplesPerRun, warmupCycles, glyphCacheCapacity, rowCacheCapacity, reuseAsciiGlyphs);
        MeasurePaint("paint-one-row", statusFrames, columns, rows, runs, samplesPerRun, warmupCycles, glyphCacheCapacity, rowCacheCapacity, reuseAsciiGlyphs);
        MeasurePaint("paint-scroll", scroll, columns, rows, runs, samplesPerRun, warmupCycles, glyphCacheCapacity, rowCacheCapacity, reuseAsciiGlyphs);
        MeasurePaint("paint-scroll-first-pass", scroll, columns, rows, runs, Math.Min(samplesPerRun, scroll.Length), 0, glyphCacheCapacity, rowCacheCapacity, reuseAsciiGlyphs);
    }

    private static void VerifyPaint(
        TerminalRenderFrame[] frames,
        int columns,
        int rows,
        int glyphCacheCapacity,
        int rowCacheCapacity,
        bool reuseAsciiGlyphs)
    {
        using var renderer = new SkiaTerminalRenderer(new TerminalRendererSettings
        {
            GlyphCacheCapacity = glyphCacheCapacity,
            RowPictureCacheCapacity = rowCacheCapacity,
            ReuseAsciiGlyphs = reuseAsciiGlyphs,
        });
        using var reference = new SkiaTerminalRenderer(new TerminalRendererSettings
        {
            GlyphCacheCapacity = glyphCacheCapacity,
            RowPictureCacheCapacity = 0,
            ReuseAsciiGlyphs = false,
        });
        renderer.Resize(new RenderViewport(columns, rows, 1));
        reference.Resize(new RenderViewport(columns, rows, 1));
        var width = checked((int)Math.Ceiling(columns * renderer.CellSize.Width));
        var height = checked((int)Math.Ceiling(rows * renderer.CellSize.Height));
        using var actual = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var expected = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var actualCanvas = new SKCanvas(actual);
        using var expectedCanvas = new SKCanvas(expected);
        var actualPixels = new byte[actual.ByteCount];
        var expectedPixels = new byte[expected.ByteCount];
        var bounds = new SKRect(0, 0, width, height);
        for (var index = 0; index < frames.Length; index++)
        {
            renderer.Render(actualCanvas, frames[index], TerminalRenderOverlays.Empty, bounds, 0, drawCursor: true);
            reference.Render(expectedCanvas, frames[index], TerminalRenderOverlays.Empty, bounds, 0, drawCursor: true);
            Marshal.Copy(actual.GetPixels(), actualPixels, 0, actualPixels.Length);
            Marshal.Copy(expected.GetPixels(), expectedPixels, 0, expectedPixels.Length);
            if (!actualPixels.AsSpan().SequenceEqual(expectedPixels))
            {
                throw new InvalidOperationException($"pixel mismatch in frame {index} of {frames.Length}");
            }
        }

        Console.WriteLine($"verified {frames.Length} frames against uncached original shaping");
    }

    private static void MeasurePaint(
        string name,
        TerminalRenderFrame[] frames,
        int columns,
        int rows,
        int runs,
        int samplesPerRun,
        int warmupCycles,
        int glyphCacheCapacity,
        int rowCacheCapacity,
        bool reuseAsciiGlyphs)
    {
        var medians = new double[runs];
        var p95s = new double[runs];
        var allocations = new double[runs];
        for (var run = 0; run < runs; run++)
        {
            using var renderer = new SkiaTerminalRenderer(new TerminalRendererSettings
            {
                GlyphCacheCapacity = glyphCacheCapacity,
                RowPictureCacheCapacity = rowCacheCapacity,
                ReuseAsciiGlyphs = reuseAsciiGlyphs,
            });
            renderer.Resize(new RenderViewport(columns, rows, 1));
            var width = checked((int)Math.Ceiling(columns * renderer.CellSize.Width));
            var height = checked((int)Math.Ceiling(rows * renderer.CellSize.Height));
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul))
                ?? throw new InvalidOperationException("could not create a raster surface");
            var canvas = surface.Canvas;
            var bounds = new SKRect(0, 0, width, height);

            void Paint(int index)
            {
                renderer.Render(canvas, frames[index % frames.Length], TerminalRenderOverlays.Empty, bounds, 0, drawCursor: true);
                surface.Flush();
            }

            // Visit every frame before timing, even when the measured sample count is small.
            for (var cycle = 0; cycle < warmupCycles; cycle++)
            {
                for (var index = 0; index < frames.Length; index++)
                {
                    Paint(index);
                }
            }

            var cacheBefore = renderer.CacheStatistics;
            var rowsBefore = renderer.RowCacheStatistics;
            (medians[run], p95s[run], allocations[run]) = Sample(samplesPerRun, Paint);
            var cacheAfter = renderer.CacheStatistics;
            var rowsAfter = renderer.RowCacheStatistics;
            Console.WriteLine(
                $"  {name} run {run + 1}: p50={medians[run]:F3} ms p95={p95s[run]:F3} ms " +
                $"alloc={allocations[run]:F0} B/frame " +
                $"cache-hits={cacheAfter.Hits - cacheBefore.Hits} " +
                $"misses={cacheAfter.Misses - cacheBefore.Misses} " +
                $"evictions={cacheAfter.Evictions - cacheBefore.Evictions} count={cacheAfter.Count} " +
                $"row-hits={rowsAfter.Hits - rowsBefore.Hits} " +
                $"row-misses={rowsAfter.Misses - rowsBefore.Misses} " +
                $"row-evictions={rowsAfter.Evictions - rowsBefore.Evictions} row-count={rowsAfter.Count}");
        }

        Report(name, medians, p95s, allocations);
    }

    private static void Measure(
        string name,
        int runs,
        int samplesPerRun,
        int warmupCycles,
        Action<int> action)
    {
        var medians = new double[runs];
        var p95s = new double[runs];
        var allocations = new double[runs];
        for (var run = 0; run < runs; run++)
        {
            for (var index = 0; index < warmupCycles * samplesPerRun; index++)
            {
                action(index);
            }

            (medians[run], p95s[run], allocations[run]) = Sample(samplesPerRun, action);
            Console.WriteLine(
                $"  {name} run {run + 1}: p50={medians[run]:F3} ms p95={p95s[run]:F3} ms " +
                $"alloc={allocations[run]:F0} B/frame");
        }

        Report(name, medians, p95s, allocations);
    }

    private static (double P50, double P95, double AllocatedBytes) Sample(int count, Action<int> action)
    {
        var samples = new double[count];
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < count; index++)
        {
            var start = Stopwatch.GetTimestamp();
            action(index);
            samples[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        var allocatedBytes = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)count;
        Array.Sort(samples);
        return (Percentile(samples, 0.5), Percentile(samples, 0.95), allocatedBytes);
    }

    private static double Percentile(double[] sorted, double fraction) =>
        sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * fraction) - 1)];

    private static void Report(string name, double[] medians, double[] p95s, double[] allocations)
    {
        Array.Sort(medians);
        Array.Sort(p95s);
        Array.Sort(allocations);
        Console.WriteLine(
            $"{name}: median-run-p50={Percentile(medians, 0.5):F3} ms " +
            $"median-run-p95={Percentile(p95s, 0.5):F3} ms " +
            $"median-alloc={Percentile(allocations, 0.5):F0} B/frame");
    }
}
