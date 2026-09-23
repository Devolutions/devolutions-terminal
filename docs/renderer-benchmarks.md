# Renderer benchmarks

The `render` mode of `tools/Devolutions.Terminal.Bench` measures the CPU work of
preparing and rasterizing terminal frames. It is not a GPU or presentation
benchmark. The existing `engine` and `control` modes measure different parts
of the pipeline; `control` uses Avalonia headless and does **not** paint.

## Reproducible input

Run from the repository root in Release, without concurrent benchmarks:

```powershell
dotnet build tools\Devolutions.Terminal.Bench -c Release
dotnet run --project tools\Devolutions.Terminal.Bench -c Release --no-build -- corpus --mb 8 --output artifacts\terminal-render.vt
dotnet run --project tools\Devolutions.Terminal.Bench -c Release --no-build -- render --data artifacts\terminal-render.vt --runs 5 --samples 120 --warmup 3 --frames 8 --chunk-kb 16
```

The generated file is deterministic; the CLI prints its SHA-256 so the same
input can be verified on another host. `--data` also accepts a pre-generated VT
file. Rendering uses a 120x30 grid unless `--columns`/`--rows` are supplied.
The benchmark executable disables tiered JIT compilation so methods do not
change optimization level between timed runs; this optimized-JIT measurement
is not a substitute for the production NativeAOT application's frame timing.
Preparation samples report `TerminalEngine.CreateSnapshot` and
`TerminalRenderPlanner.Create` separately, as well as their combined cost.
The standalone planning sample reuses one captured snapshot. Paint samples reuse an offscreen
**CPU raster** `SKSurface` and renderer, time `SkiaTerminalRenderer.Render` and
`SKSurface.Flush`, and report p50/p95 latency plus managed bytes allocated per
frame and glyph-cache hit/miss/eviction counts. Static paints repeat one frame;
one-row paints cycle through frames
with changed status text; scroll paints cycle through the last frames produced
by feeding the corpus in 16 KiB chunks. Setup, corpus parsing, renderer
construction, and warmup are excluded. The median of per-run p50 and p95
figures is printed. Warmup excludes initial setup, but a scrolling working set
that exceeds the cache capacities still incurs shaping and evictions.
`paint-scroll-first-pass` uses a fresh renderer and visits each of the first
`min(--samples, --frames)` captured scroll frames exactly once, without
warmup; unlike `paint-scroll`, it does not replay a warmed frame set.
`--glyph-cache 4096` and `--row-cache 256` select the cache capacities;
`--row-cache 0 --ascii-cache 0` reproduces the original uncached row drawing
and run-specific glyph shaping. The default `--ascii-cache 1` reuses
single-character printable ASCII glyphs when their immediate neighbors are
also ASCII. Unicode clusters and adjacent non-ASCII context keep their
run-specific shaping. Recorded rows include their backgrounds and decorations
but are reused only for single-width rows without DRCS glyphs; a row is
recorded only when its content appears a second time. The bounded first-sighting
set and picture cache are both cleared when renderer resources change. This
avoids recording rows that only appear once in a scrolling stream. The CLI
reports glyph and row hit/miss/eviction counts; row-cache misses count picture
recordings, not the first sightings drawn directly.

Check pixel equivalence for the captured scrolling and status frames before
timing an optimized build:

```powershell
dotnet run --project tools\Devolutions.Terminal.Bench -c Release --no-build -- render --data artifacts\terminal-render.vt --frames 256 --verify
```

`--verify` renders both the selected configuration and the original
run-specific shaping without row pictures to CPU bitmaps and fails on the
first pixel mismatch. For fonts with context-sensitive ASCII substitutions,
set `ReuseAsciiGlyphs = false` in `TerminalRendererSettings` if the original
shaping is required; likewise, `RowPictureCacheCapacity = 0` disables row
recording. The pixel check covers the chosen corpus and installed fonts, not
all possible font features.

For example, on one Windows host using the deterministic 8 MiB corpus
(SHA-256 `3911CA717A2867CB97407E53DDDFF6A3C98CDF86C242E40194044EAA12190671`),
120x30, 256 captured scroll frames, 128 timed samples/run, one warmup cycle,
and three Release runs, the median-run CPU-raster results were:

| Paint scenario | Original (`--row-cache 0 --ascii-cache 0`) | Optimized defaults |
| --- | ---: | ---: |
| Static p50 | 1.263 ms | 0.867 ms |
| One-row p50 | 1.849 ms | 0.886 ms |
| Scrolling p50 / p95 | 35.512 / 52.286 ms | 3.435 / 5.932 ms |
| Scrolling managed allocation/frame | 3,428,490 B | 157,920 B |

These are separate runs on one machine, not a speed ratio against Ghostty.
In this workload 16 KiB updates replace most visible rows; the row cache
therefore helps static/localized paints more than scrolling. Replaying only
eight warmed frames gives artificially high row-cache hit rates and is not a
substitute for the longer scrolling case.

For preparation on the same 8 MiB corpus, explicitly measuring each stage
before and after pre-sizing per-run text and cluster buffers gave:

| Stage (median-run p50; managed bytes/frame) | Before | After |
| --- | ---: | ---: |
| Snapshot | 0.009 ms; 149,576 B | 0.010 ms; 149,576 B |
| Plan | 0.112 ms; 415,288 B | 0.087 ms; 317,440 B |
| Snapshot + plan | 0.121 ms; 564,864 B | 0.095 ms; 467,016 B |

Reusing text and cluster scratch storage across style runs, with stack-backed
buffers for rows up to 256 cells, reduced planning allocation further. On the
same 8 MiB corpus at 120x30, with 256 captured frames, 128 samples/run, two
warmup cycles, and five Release runs:

| Stage (median-run p50; managed bytes/frame) | Previous pre-sized runs | Reused row buffers |
| --- | ---: | ---: |
| Snapshot | 0.010 ms; 149,576 B | 0.012 ms; 149,576 B |
| Plan | 0.112 ms; 317,440 B | 0.082 ms; 74,600 B |
| Snapshot + plan | 0.121 ms; 467,016 B | 0.099 ms; 224,176 B |

The previous column is the separately captured baseline, not an interleaved
run; planning latency varied between repetitions (0.072-0.095 ms p50 with
reused heap/stack row buffers), while the 242,840 B/frame allocation reduction
was stable. This changes frame preparation, not the paint path; do not treat
paint latency changes between runs as a planner improvement. The optimized
path passed the 512-frame pixel check against uncached drawing.

For plain printable-ASCII runs, the planner now represents one-cell clusters
by their index instead of allocating and retaining a `TerminalTextCluster[]`
per run. The row-picture hash iterates clusters by index, avoiding enumerator
allocations on these runs; snapshots with no DRCS glyphs avoid constructing an
empty glyph dictionary. On the same 8 MiB corpus, 120x30, 256 frames, 128
samples/run, two warmup cycles, and five Release runs:

| Stage (managed bytes/frame) | Before | After |
| --- | ---: | ---: |
| Snapshot | 149,576 B | 149,424 B |
| Plan | 74,600 B | 23,352 B |
| Snapshot + plan | 224,176 B | 172,776 B |
| First-pass scrolling paint | 94,174 B | 84,985 B |
| Warmed static paint | 3,120 B | 32 B |

First-pass glyph-cache misses remained at 5,358 over 128 frames. The 149 KB
snapshot is primarily independent copies of mutable cells; removing those
copies would change snapshot lifetime and ownership and is not part of this
optimization. CPU-raster timing varied substantially across these separately
captured runs, so the allocation reductions do **not** establish a paint
latency win. The optimized build passed 512-frame pixel parity and the
renderer/control integration tests.

Do not enlarge the glyph cache solely on the basis of warmed scrolling.
At `--frames 256 --samples 128 --warmup 1`, raising its bound from 4,096 to
16,384 eliminated 5,260 misses and reduced warmed scroll p50 from about
3.4 ms to 1.3 ms, but the **first-pass** scrolling workload still incurred
5,358 misses, allocated 159,093 B/frame, and took about 3 ms p50 at both
capacities. First-pass behavior better represents an unrepeated stream; a
larger cache consumes more memory without removing the initial shaping cost.
Keep both workloads and report cache occupancy/evictions before tuning the
application default.

On the same first-pass workload, admitting pictures on the second sighting
instead of the first reduced managed allocation from 159,093 to 94,174 B/frame
at the default 4,096-glyph bound. Median-run p50 changed from 3.388 to
3.275 ms, which is too small a difference to establish a throughput win
from these runs. For fully warmed static and localized paints, use at least
`--warmup 2` so both the candidate and picture passes finish before timing.

## Ghostty reference measurements

This repository pins Ghostty at the commit in
`native/ghostty/ghostty-upstream.json`. Build **that** source with Zig 0.16.0
using `zig build -Demit-bench -Doptimize=ReleaseFast` (on macOS add
`-Demit-macos-app=false`). With the pinned revision on Windows, build the
benchmark-only/lib-vt targets instead; the default full target currently
fails to link because of a duplicate `memset` symbol:

```powershell
zig build -Demit-bench=true -Demit-lib-vt=true -Doptimize=ReleaseFast -j1
```

The upstream `zig-out/bin/ghostty-bench` (`.exe` on Windows) can consume the
exported corpus:

```text
ghostty-bench +screen-clone --mode=render-partial --terminal-rows=30 --terminal-cols=120 --data=<absolute-path-to-terminal-render.vt>
```

Run it repeatedly with warmups (e.g. `hyperfine`) rather than timing
`ghostty-gen | ghostty-bench`: keep generation out of the measurement and use
enough internal operations to amortize process startup and input loading.
Ghostty's `screen-clone` `render`, `render-clean`, and
`render-partial` modes time updates to its CPU `RenderState`, **not** GPU
painting. Their steps respectively perform 50,000 full, 3,000,000 clean, or
2,000,000 partial updates at `--loops=1`. External wall times also include
process startup and corpus loading. They use different semantics and
iteration counts from this project's snapshot/plan stage; record them as
separate diagnostics, not a numeric speed ratio. The `libghostty-vt` binary
used by this project has no Ghostty graphics renderer.

## Full renderer comparison (Linux/macOS)

For a user-visible comparison, benchmark the full GUI applications on the
**same native Linux or macOS machine**, not this CPU harness. Build both in
Release with fixed font files, point size, scale, grid/window size, colors,
cursor behavior, and effects; record each app's graphics backend. Replay the
*same saved* VT stream via a PTY at a controlled cadence, recording the byte
count and timestamps. Include idle redraws, localized TUI updates, and
scrolling separately; verify the rendered screen after each run, and exclude
unsupported image protocols from paired workloads.

Instrument Avalonia at `TermControl.Render` (snapshot/plan), then at
`TerminalSkiaDrawOperation.Render` (Skia submission). Instrument upstream
Ghostty at `RenderState.update` and `renderer/generic.zig:drawFrame`. Both draw
paths may return before presentation: use platform GPU/compositor tracing or
presentation timestamps for end-to-end frame latency and dropped frames.
Report CPU preparation, render submission, GPU completion/presentation, p50/
p95/p99 frame intervals, and CPU/GPU utilization **as distinct metrics**.
Full-app elapsed time also includes each app's engine, transport, and
scheduling; it is not a render-only speed ratio.
Warm up caches, alternate run order, collect repeated runs, and never run both
apps' measurements concurrently. Ghostty's native GUI is available on
Linux/macOS, not Windows; its Windows `libghostty-vt` support cannot stand in
for a native renderer comparison.

For a claim of beating Ghostty **at every measured point**, establish separate
equivalent baselines for VT ingest, CPU frame preparation, idle/static paint,
localized updates, first-pass and replayed scrolling, and presented frame
latency. Compare p50, p95, p99, allocation/peak resident memory, CPU/GPU
utilization, and dropped frames for each workload on the same native host;
require pixel or terminal-state equivalence first. Alternate the applications
across repeated runs and report the run-to-run spread as well as medians. A
failure or unmeasured metric means the all-points claim has **not** passed.
The Windows offscreen raster and upstream `screen-clone` results above measure
different work and cannot satisfy the presented-frame gate.
