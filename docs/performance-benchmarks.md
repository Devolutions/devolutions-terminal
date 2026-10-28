# Performance benchmarks

This page explains how DT benchmarks VT ingest in its terminal engine
(`Devolutions.Terminal.Core.TerminalEngine`) and how to read the results
across sessions and machines.

For renderer-only details (caches, pixel verification, historical numbers)
see [renderer benchmarks](renderer-benchmarks.md). The earlier comparison
against `libghostty-vt`, from before the Ghostty engine was removed, is kept in
[Ghostty comparison benchmarks](history/ghostty-comparison-benchmarks.md).

## DT's benchmark layers

All modes live in `tools/Devolutions.Terminal.Bench`. That tool runs on the
JIT with tiered compilation disabled. It is **not** the NativeAOT shipping
build, so treat its numbers as relative.

| Layer | Mode |
| --- | --- |
| 1. VT ingest | `vt` |
| 2. Frame preparation | `render` (snapshot, plan) |
| 3. CPU paint | `render` (paint-*) |
| 4. PTY to UI coalescing | `control` |

## `vt`: same-process ingest

```powershell
dotnet run --project tools\Devolutions.Terminal.Bench -c Release -- vt --mb 8 --runs 7 --json artifacts\bench\vt.json
```

Two targets consume the same bytes in the same chunks (16 KiB by default,
the ConPTY read size; change it with `--chunk-kb`):

- `builtin`: DT's `TerminalEngine`, the engine under test.
- `control`: an FNV-1a hash over the fed bytes. Each step depends on the
  previous one, so it is a serial loop that does no terminal work, and its
  speed tracks only the core clock and load.

The control exists because absolute MB/s is not stable. On laptop CPUs it
drifts by up to about 2.5x between invocations, mostly with the CPU's turbo
state. Both targets see the same clock within a run, so the
**builtin / control ratio** (`builtinVsControl` in the JSON) is the number to
compare across sessions. Absolute MB/s is only meaningful within one run.

`--frame-kb N` makes `builtin` take a `CreateSnapshot` every N KiB fed,
inside the timed region. The default of `0` measures pure ingest. `256`
approximates a busy screen at a frame rate the renderer can sustain.

Methodology:

- **Identical, deterministic bytes.** Each workload is seeded, and its
  SHA-256 is printed. `--export <dir>` writes the `.vt` files. `--data <file>`
  replays a capture, for example an asciinema or vim session.
- **Fair timing.**
  - Engines are built outside the timed region.
  - Runs interleave the targets in rotating order and include a discarded
    warmup, so thermal drift and background load hit both alike.
  - A sample replays the corpus into the same engine until at least `--min-ms`
    (500 ms by default) has elapsed, so fast workloads are not dominated by
    timer noise.
  - Laptop CPUs leave turbo a few seconds into sustained load, and throughput
    can fall about 3x and stay there. With 500 ms samples, runs land on both
    sides of that drop and spreads become large. Samples of ~3 s
    (`--min-ms 3000`, or the script's `-MinMs 3000`) all run at the sustained
    clock.
- **Cost metrics**: managed bytes allocated per MiB fed, GCs per MiB, and
  retained private memory after the run. Retained memory is a process-wide
  delta, so read it as an order of magnitude.

Workloads (`--workloads a,b,...`; `vt --list` prints them):

| Workload | Shape |
| --- | --- |
| `ascii` | unbroken 0x21–0x7E stream relying on autowrap |
| `ascii-lines` | printable lines with CRLF (`cat` of a log) |
| `utf8` | Latin, Greek, Cyrillic, CJK, emoji, ZWJ, combining marks (DEC mode 2027 on) |
| `cjk` | lines of CJK ideographs only (wide, 3-byte UTF-8) |
| `emoji` | lines of emoji only (wide, 4-byte UTF-8) |
| `styled` | short SGR runs, OSC 8, wide text |
| `dense-cells` | every cell gets its own truecolor/256 SGR (vtebench `dense_cells`) |
| `light-cells` | full-screen plain text from home (vtebench `light_cells`) |
| `cursor-motion` | random CUP, short writes, EL (vtebench `cursor_motion`, TUIs) |
| `scrolling` | `y\n` (vtebench `scrolling`) |
| `scroll-region` | DECSTBM region scrolling (vtebench `scrolling_*_region`) |
| `osc` | titles, OSC 133 marks, OSC 8 links |
| `mixed` | the legacy `engine`/`render` corpus |

## Running the whole suite

```powershell
pwsh scripts/Invoke-TerminalBenchmarks.ps1                                                # vt + render, results in artifacts/bench/<timestamp>
pwsh scripts/Invoke-TerminalBenchmarks.ps1 -Quick                                         # fast local signal
pwsh scripts/Invoke-TerminalBenchmarks.ps1 -Suite vt -MinMs 3000                          # ~3 s samples: stable on CPUs that leave turbo under load
pwsh scripts/Invoke-TerminalBenchmarks.ps1 -Baseline artifacts/bench/<previous>/vt.json  # fails if builtin/control drops >15% on identical inputs
```

The script runs one process per `vt` workload: in one long process running
every workload, ratios drifted by up to 50% because heap state carried over
between workloads. `-Baseline` only compares workloads with the same SHA-256
(use the same `-Quick` and `-Workloads` settings as the baseline) and gates on
the builtin/control ratio, not on absolute MB/s. `-RegressionPercent` changes
the 15% threshold.

Run `scripts/Invoke-TerminalBenchmarks.ps1 -Baseline` on every engine change
so wins are kept, and record the commit, machine and `vt.json` with each
published number.
