# Performance benchmarks vs Ghostty

> **Historical record.** The optional Ghostty engine has been removed from
> Devolutions Terminal; the builtin engine is now the only one. The numbers
> below were measured against `libghostty-vt` pinned at commit
> `3c1ef5b32fc5ea6b93d28493fabf193f595139cf`. The `ghostty-vt` and `ghostty`
> bench targets, the equivalence gate, `--no-gate`, `-RequireBeatGhostty`,
> `-GhosttyBench` and `native/ghostty/` no longer exist, so the commands on
> this page will not run as written. For the current benchmark, see
> [performance benchmarks](../performance-benchmarks.md).

This page explains how Ghostty backs up its "fastest terminal" positioning,
what its own benchmarks do and do not measure, and how DT benchmarks its
**builtin** engine and renderer against it. The goal is a claim that holds up
under review: "with the default engine, DT matches or beats Ghostty at
workload X, measured this way, on this machine."

For renderer-only details (caches, pixel verification, historical numbers)
see [renderer benchmarks](../renderer-benchmarks.md).

## How Ghostty supports its performance claims

Ghostty's public claims are mostly qualitative. Few of them come with a method
someone else could rerun.

| Claim | Where | Evidence |
| --- | --- | --- |
| "Same performance category" as the fastest terminals; "~100x faster than Terminal.app and iTerm" | README | None. Ghostty's own devlog measured about 6x against iTerm2 and under 2x against Terminal.app |
| `cat` of plain text 4x faster than iTerm/Kitty, ~60 fps under load | v1.0 README | No method given. The README itself says "we need better benchmarks" |
| SIMD parser: 7.3x faster ASCII parse, 16.6x faster UTF-8 decode; `time cat` of a 5.4 MB Japanese text: Ghostty 73 ms, Alacritty 66 ms, Kitty 392 ms, iTerm2 470 ms | [Devlog 006](https://mitchellh.com/writing/ghostty-devlog-006) | **The only write-up with a real method**: hyperfine microbenchmarks with noop/memcpy floors, plus 10-run `time cat` averages. Alacritty beat Ghostty in that table |
| 480–500 fps in DOOM-fire | [Zig patterns talk](https://mitchellh.com/writing/ghostty-and-useful-zig-patterns) | Anecdote. The same talk says input latency was never measured |
| 1.3: IO 20–30% faster on a 4 GB asciinema replay; frame builds 2–5x faster | [1.3.0 notes](https://ghostty.org/docs/install/release-notes/1-3-0) | Real-world replay, but the corpus is not public |
| `cat` of 150 MB ASCII in 575 ms (nightly) | Social post, second-hand | One run, no hardware listed. Unverified |

The [about page](https://ghostty.org/docs/about) itself is careful: Ghostty is
"in some benchmarks faster, in others slower."

### What Ghostty's in-tree benchmarks measure

Ghostty's benchmarks live in `src/benchmark/` at the pinned commit (see
`native/ghostty/ghostty-upstream.json`). They are built with
`zig build -Demit-bench`, which produces two binaries:

- `ghostty-bench +<action>` runs one component microbenchmark. It prints
  nothing; the documented method is to time the **whole process** with
  `hyperfine --warmup N`. That time includes process startup and setup. This is
  why many actions have a `noop` mode to subtract.
- `ghostty-gen <generator>` writes synthetic input: `ascii`, `utf8`, `osc`,
  `kitty` and `styled`. Seeds are time-based by default, so you save the output
  once and reuse the file.

The actions that matter for a comparison with DT:

| Action | What it times | Closest DT measurement |
| --- | --- | --- |
| `+terminal-stream --data=F` | Full VT parse and terminal state update, read in 64 KiB chunks | `vt` (`ghostty-vt` target) |
| `+terminal-parser` | State machine only, actions discarded | none (DT's parser is not separable) |
| `+screen-clone --mode=render\|render-clean\|render-partial` | Updates to the CPU `RenderState` while holding the lock | `render` snapshot/plan |
| `+codepoint-width`, `+grapheme-break` | Unicode tables | none |
| `+terminal-resize` | Reflow | none yet |

**CI only builds these benchmarks.** Nothing runs them, Ghostty publishes no
result history, and nothing gates regressions. Ghostty does **not** measure:

- input latency
- frame presentation or dropped frames
- PTY-to-screen throughput outside of hand runs of `vtebench` or `time cat`
- memory per line of scrollback
- startup time

On Windows its IO path is a serial `ReadFile` loop, and no Ghostty GUI exists
there.

### Why Ghostty is fast (what the builtin engine has to match)

- **SIMD ground state**: Ghostty scans for ESC and decodes UTF-8 to UTF-32 in
  bulk (simdutf/Highway). Each printable run goes to the screen as one slice
  (`stream.zig`), not one byte at a time.
- **Bulk cell writes**: `printSlice` writes same-width runs against a template
  cell. Widths come from a 3-level lookup table, with a shortcut for
  codepoints up to 0xFF.
- **8-byte cells in pooled, offset-addressed pages**: when scrollback is full,
  the oldest page is reused. Steady-state scrolling allocates nothing.
- **Separate IO and render threads**: the renderer rebuilds only dirty rows,
  once per frame, never once per write.

## DT's benchmark layers

All modes live in `tools/Devolutions.Terminal.Bench`. That tool runs on the
JIT with tiered compilation disabled. It is **not** the NativeAOT shipping
build, so treat its numbers as relative.

| Layer | Mode | Ghostty counterpart | Comparable on Windows? |
| --- | --- | --- | --- |
| 1. VT ingest | `vt` | `ghostty-bench +terminal-stream` | **Yes, in-process on identical bytes** |
| 2. Frame preparation | `render` (snapshot, plan) | `+screen-clone --mode=render*` | Diagnostic only; the two do different work |
| 3. CPU paint | `render` (paint-*) | none (GPU renderer) | No |
| 4. PTY to UI coalescing | `control` | termio/renderer wakeups | No |
| 5. End-to-end | manual: `vtebench`, `time cat` | same | Linux/macOS only, same machine |

### Layer 1: `vt`, same-process ingest A/B

```powershell
dotnet run --project tools\Devolutions.Terminal.Bench -c Release -- vt --mb 8 --runs 7 --json artifacts\bench\vt.json
```

Three targets consume the same bytes in the same chunks (16 KiB by default,
the ConPTY read size; `--chunk-kb 64` matches `+terminal-stream`):

- `builtin`: DT's `TerminalEngine`, the engine under test.
- `ghostty-vt`: raw `ghostty_terminal_vt_write` from the pinned
  `libghostty-vt`, with only the scrollback limit set to the builtin's 9,001
  lines. This is the bar to beat. It is the same work as `+terminal-stream`,
  without process startup.
- `ghostty`: DT's `GhosttyTerminalEngine`, which adds the projection into DT's
  cell model. This is what the opt-in Ghostty mode actually costs. Projection
  is lazy, so measure it with `--frame-kb`.

`--frame-kb N` reads one frame every N KiB fed, inside the timed region:

- builtin and `ghostty` take a `CreateSnapshot`;
- `ghostty-vt` does what Ghostty's renderer does per frame, a render-state
  update followed by a clean.

The default of `0` measures pure ingest. `256` approximates a busy screen at a
frame rate the renderer can sustain.

Methodology:

- **Identical, deterministic bytes.** Each workload is seeded, and its
  SHA-256 is printed. `--export <dir>` writes the `.vt` files so
  `ghostty-bench` can consume exactly the same input. `--data <file>` replays
  a capture, for example an asciinema or vim session.
- **Equivalence gate.** Before timing, both engines consume the workload, and
  the visible text, cursor and history are compared. A throughput number only
  counts when the screens are identical. The `utf8` workload enables DEC mode
  2027 because, without it, Ghostty measures ZWJ emoji per codepoint while
  builtin clusters them. Ghostty trims history by page, so its history count
  can be a few hundred lines below 9,001; that difference is expected.
- **Fair timing.**
  - Engines are built outside the timed region.
  - Runs interleave the targets in rotating order and include a discarded
    warmup.
  - A sample replays the corpus into the same engine until at least `--min-ms`
    (500 ms by default) has elapsed, so fast targets are not dominated by
    timer noise.
  - Laptop CPUs leave turbo a few seconds into sustained load: on the machine
    below, throughput falls about 3x after ~2.7 s and stays there, whatever
    the engine does. With 500 ms samples, runs land on both sides of that drop
    and spreads reach 50–180%. Samples of ~3 s (`--min-ms 3000`, or the
    script's `-MinMs 3000`) all run at the sustained clock.
- **Conservative verdict.**
  - `faster` only when builtin's *slowest* run beats Ghostty's *fastest*.
  - `slower` when the reverse holds.
  - `overlap` otherwise.
  - Without `--no-gate`, the exit code is non-zero unless every workload is
    `faster`.
- **Cost metrics**: managed bytes allocated per MiB fed, GCs per MiB, and
  retained private memory after the run. Retained memory is a process-wide
  delta, so read it as an order of magnitude. Native allocations by Ghostty
  appear only in that column.

Workloads (`--workloads a,b,...`):

| Workload | Shape | Source of the idea |
| --- | --- | --- |
| `ascii` | unbroken 0x21–0x7E stream relying on autowrap | `ghostty-gen ascii` |
| `ascii-lines` | printable lines with CRLF | `cat` of a log |
| `utf8` | Latin, Greek, Cyrillic, CJK, emoji, ZWJ, combining marks | `ghostty-gen utf8`, restricted to assigned characters |
| `cjk` | lines of CJK ideographs only (wide, 3-byte UTF-8) | `time cat` of CJK text |
| `emoji` | lines of emoji only (wide, 4-byte UTF-8) | emoji-heavy logs and prompts |
| `styled` | short SGR runs, OSC 8, wide text | `ghostty-gen styled` |
| `dense-cells` | every cell gets its own truecolor/256 SGR | vtebench `dense_cells` |
| `light-cells` | full-screen plain text from home | vtebench `light_cells` |
| `cursor-motion` | random CUP, short writes, EL | vtebench `cursor_motion`, TUIs |
| `scrolling` | `y\n` | vtebench `scrolling` |
| `scroll-region` | DECSTBM region scrolling | vtebench `scrolling_*_region` |
| `osc` | titles, OSC 133 marks, OSC 8 links | `ghostty-gen osc` (valid sequences only) |
| `mixed` | the legacy `engine`/`render` corpus | DT history |

### Running the whole suite

```powershell
pwsh scripts/Invoke-TerminalBenchmarks.ps1                  # vt + render, results in artifacts/bench/<timestamp>
pwsh scripts/Invoke-TerminalBenchmarks.ps1 -Quick           # fast local signal
pwsh scripts/Invoke-TerminalBenchmarks.ps1 -Suite vt -MinMs 3000                           # ~3 s samples: stable on CPUs that leave turbo under load
pwsh scripts/Invoke-TerminalBenchmarks.ps1 -Baseline artifacts/bench/<previous>/vt.json   # fails if builtin/ghostty-vt drops >15% on identical inputs
pwsh scripts/Invoke-TerminalBenchmarks.ps1 -RequireBeatGhostty                             # fails unless builtin wins every vt workload
pwsh scripts/Invoke-TerminalBenchmarks.ps1 -GhosttyBench <path>\ghostty-bench.exe          # also replays the corpora through upstream +terminal-stream
```

`-GhosttyBench` times whole processes, as Ghostty does, so its figures include
process startup. It cross-checks the in-process `ghostty-vt` target; it does
not replace it. Build `ghostty-bench` as described in
[Ghostty reference measurements](#ghostty-reference-measurements) below.

### End-to-end (layer 5) on Linux/macOS

Ghostty's only cross-terminal numbers come from `time cat` and vtebench. To
reproduce them, run on one native Linux or macOS machine, with both apps in
Release and matching font, size, grid and scrollback:

- `time cat` of a large ASCII file and of a large CJK file, 10 runs each.
- vtebench's 12 workloads, run inside each terminal. vtebench measures how fast
  the terminal drains the PTY (backpressure), not paint time.

Never run both terminals at the same time; alternate their order and report
the spread. The checklist in
[renderer comparison against Ghostty](#renderer-comparison-against-ghostty-linuxmacos)
lists the full set of gates.

## Results at the time of removal

Setup for these numbers:

- One Windows 11 x64 laptop (Intel Core Ultra 9 285HX, 24 cores, AVX2),
  .NET 10.0.14 (JIT, tiered compilation off), and the pinned `libghostty-vt`
  (ReleaseFast).
- 120x30 grid, 16 KiB chunks, 8 MiB per workload, 7 timed runs plus 1 warmup,
  samples of at least 3 s.
- Each workload ran in its own process, via:

  ```powershell
  pwsh scripts/Invoke-TerminalBenchmarks.ps1 -Suite vt -MinMs 3000
  ```

This machine's absolute speed drifts by up to 2.5x between invocations,
mostly with the CPU's turbo state (see *Fair timing* above). So read the
**builtin / ghostty-vt ratio**, which is measured within each run, and treat
anything within about ±10% of 1.0 as a tie. Throughput is the median MB/s.

### Ingest: builtin engine vs raw `libghostty-vt`

| Workload | builtin | ghostty-vt | ghostty (DT) | ratio | verdict | builtin alloc / MiB | screens |
| --- | ---: | ---: | ---: | ---: | --- | ---: | --- |
| osc | 147 | 25.3 | 23.5 | **5.82x** | **faster** | 6.7 MB | identical |
| styled | 45.9 | 18.0 | 13.2 | **2.54x** | **faster** | 1.4 MB | 1 row differs |
| mixed | 787 | 366 | 229 | **2.15x** | **faster** | 8 KB | identical |
| dense-cells | 262 | 138 | 51 | **1.90x** | **faster** | 0 | identical |
| scrolling | 56.5 | 32.6 | 31.9 | **1.73x** | **faster** | 104 KB | identical |
| utf8 | 80.1 | 59.0 | 69.3 | **1.36x** | overlap | 92 KB | identical |
| light-cells | 2,265 | 1,737 | 62 | **1.30x** | **faster** | 0 | identical |
| emoji* | 816 | 672 | 301 | **1.21x** | **faster** | 7 KB | identical |
| ascii | 1,145 | 971 | 448 | **1.18x** | **faster** | 5 KB | identical |
| ascii-lines | 716 | 626 | 286 | **1.14x** | overlap | 9 KB | identical |
| cjk* | 583 | 522 | 237 | **1.12x** | **faster** | 10 KB | identical |
| scroll-region | 865 | 782 | 58 | **1.11x** | **faster** | 0 | identical |
| cursor-motion | 188 | 172 | 95 | **1.09x** | **faster** | 32 KB | identical |

\* From a later invocation of those two workloads alone. In the full run they
landed in a noisy stretch (spreads over 50%) and measured 0.98x and 1.22x.
Other quiet runs of the same code measured 1.07–1.21x on cjk and 1.18x on
emoji.

`ghostty (DT)` skips projection without `--frame-kb`. The one-row difference in
`styled` is a behavior difference, not a measurement artifact: builtin drops a
combining mark that follows a space, and Ghostty keeps it.

How builtin got here, as the median builtin/ghostty-vt ratio per round (each
round measured against its own in-run Ghostty control):

| Workload | `dc1cd26` | round 1 | round 2 (packed cells) | round 3 | round 4 (`f45b95b`) | round 5 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| ascii | 0.009 | 0.20 | 0.55 | 1.21 | 1.24 | 1.18 |
| ascii-lines | 0.009 | 0.20 | 0.50 | 1.13 | 1.19 | 1.14 |
| light-cells | 0.009 | 0.32 | 0.48 | 1.20 | 1.23 | 1.30 |
| scrolling | 0.015 | 0.14 | 0.40 | 1.11 | 1.33 | 1.73 |
| dense-cells | 0.42 | 0.63 | 0.62 | 1.57 | 1.44 | 1.90 |
| styled | 0.30 | 1.33 | 1.14 | 2.82 | 2.91 | 2.54 |
| osc | 0.29 | 2.60 | 4.52 | 4.57 | 4.59 | 5.82 |
| mixed | 0.03 | 0.39 | 0.45 | 1.56 | 1.84 | 2.15 |
| scroll-region | 0.018 | 0.22 | 0.34 | 0.90 | 0.96 | 1.11 |
| cursor-motion | 0.11 | 0.23 | 0.17 | 0.94 | 0.90 | 1.09 |
| utf8 | 0.10 | 0.36 | 0.27 | 0.57 | 0.77 | 1.36 |
| cjk | — | — | — | 0.53 | 0.77 | 1.12 |
| emoji | — | — | — | 0.50 | 0.80 | 1.21 |

Round 5 was measured with 3 s samples; earlier rounds used 500 ms samples.

The `-Baseline` gate flagged real regressions twice:

- **Round 2:** utf8 and cursor-motion.
- **Round 4:** dense-cells and cursor-motion, from caching packed templates on
  the short-ASCII path.

Each was confirmed with an interleaved A/B build and fixed before moving on.
Round 5 passed the gate after each change. Moves of about ±10% between rounds
(ascii and ascii-lines into round 5, for example) are within this machine's
noise. styled's 2.54x comes from a run with a 136% spread; single-workload
runs of the same code measured 3.39–3.61x.

### DT's Ghostty mode vs raw `libghostty-vt`, with frames

These numbers use `--frame-kb 256`, so a projection is taken every 256 KiB.
They were measured in round 4; round 5 did not change the Ghostty engine.

| Workload | ghostty (DT) before | after | ghostty-vt | ratio before → after |
| --- | ---: | ---: | ---: | ---: |
| ascii | 49.8 | 214.4 | 960 | 0.05 → 0.22 |
| ascii-lines | 46.5 | 220.0 | 660 | 0.07 → 0.33 |
| cursor-motion | 36.9 | 79.9 | 146 | 0.25 → 0.55 |
| mixed | 41.1 | 146.5 | 295 | 0.12 → 0.50 |
| light-cells | 45.6 | 57.5 | 1,580 | 0.03 → 0.04 |

`light-cells` rewrites every row between frames, so every row is dirty and the
per-cell P/Invoke projection still dominates.

### Frame preparation and CPU paint

`render`, 120x30, median p50 / p95, measured in round 4:

| Stage | before packed cells | now |
| --- | --- | --- |
| snapshot | 0.009 / 0.011 ms | 0.040 / 0.044 ms |
| snapshot + plan | 0.074 / 0.082 ms | 0.113 / 0.129 ms |
| static paint | 0.69 / 1.08 ms | 0.75 / 1.00 ms |
| one-row update | 0.69 / 1.70 ms | 0.76 / 1.06 ms |
| scrolling paint | 3.29 / 9.72 ms | 2.90 / 5.54 ms |

Snapshots now unpack each packed cell into a public `Cell`, which costs about
0.03 ms per 120x30 frame. Every stage still fits well inside a 16.7 ms frame.
No Ghostty counterpart exists on Windows.

### What changed in the builtin engine

Every fast path is checked against the per-character path by differential
tests (`AsciiFastPathTests`, `CsiFastPathTests`, `CellRowModelTests`,
`PackedCellStorageTests`, `CombiningTableTests`). Each new branch was also
checked by deliberately breaking it and confirming a test fails.

- **Packed, reference-free storage** (`CellRow`, `PackedCell`).
  - A cell is 16 bytes with no GC references, down from about 40 with two
    `string?` fields.
  - Hyperlinks live in a per-line side array that exists only while needed.
  - Combining text is interned per buffer (`CombiningTable`), and lines store
    int IDs. A cluster seen before costs no allocation, a recycled line keeps
    its ID array, and the table compacts so ever-new clusters cannot grow it
    without bound. This took utf8 from 9.4 MB to under 0.1 MB allocated per
    MiB.
  - The public `Cell` and `TermColor` are unchanged, and every value round-trips
    losslessly.
- **Logically blank cells are never written.**
  - A line stores only its written range; everything outside it reads as the
    blank it was cleared with.
  - Recycling a scrollback line, the coldest memory in the buffer, is O(1).
  - Erasing to the end of a line is O(1) for left-aligned text.
  - Writes fill only the gap between the stored range and the new cells.
- **Bulk printing.**
  - **ASCII:** the parser hands whole printable-ASCII runs to
    `IVtDispatch.PrintAscii`. They are stored a row segment at a time, two
    64-bit (or, with AVX2, two 256-bit per four cells) stores per cell batch.
  - **UTF-8 runs:** well-formed UTF-8 runs go to `IVtDispatch.PrintRunes`.
    Runs of 3- or 4-byte sequences (CJK, emoji) are decoded four at a time
    from 16-byte `Vector128` loads, with the scalar decoder's validity rules
    (no overlong forms, surrogates or values past U+10FFFF); anything else
    falls back to the scalar decoder. On CJK this decodes about 3x faster.
  - **Grapheme joins:** precomputed tables for planes 0 and 1 rule out joins,
    so runes that cannot join are stored a segment at a time. That includes
    emoji after a cell without a trailing ZWJ. A zero-width rune with no join
    property (a combining accent, ZWJ) goes straight into the previous cell's
    combining text.
  - **Rows without wide glyphs:** a conservative per-row "may hold wide cells"
    flag means classifying and storing happen in one pass, with no clearing
    around wide glyphs. Every freshly recycled scrollback line qualifies. That
    pass ORs each rune into precomputed template bits and writes one 16-byte
    store per cell, with widths from a table where ambiguous widths are already
    resolved.
  - **Short writes:** the packed print templates are cached and rebuilt only
    when the attributes, protection or shell-integration state change, and a
    single printable byte between escape sequences is one store.
- **CSI fast path.** A complete plain CSI sequence is parsed in one pass,
  straight into the arrays the dispatcher receives, and the parser calls the
  engine directly instead of through `IVtDispatch`. Sequences with no private
  marker or intermediate byte skip the special cases and go straight to the
  switch on the final byte.
- **C0 fast path.** CR, LF and other controls in ground state go straight to
  `ExecuteC0` instead of through the string-state checks.
- **Scroll regions** rotate line references with `Array.Copy`, not one
  indexer call per line.

### What still separates builtin from Ghostty

Builtin now leads on every workload. The closest leads, `cursor-motion`,
`scroll-region` and `cjk` (1.09–1.12x), sit near the ±10% band of this
machine's noise, and `ascii-lines` and `utf8` still get `overlap` verdicts
from run-to-run spread. Timed stage by stage on CJK after round 5 (one engine
reused with history full, per scalar, all stages in one process):

| Stage | Cost |
| --- | --- |
| UTF-8 decode, block path | ~0.5–1.0 ns |
| Parser per-line work (CR/LF dispatch, run setup) | ~0.7–1 ns |
| Cell stores (two 16-byte cells per wide character) | ~2 ns |
| Buffer CR/LF, scrolling and line reuse | ~0.7 ns |

- **Parser per-line overhead** now costs about as much as the decode. Each
  CR and LF still goes through an interface call, and each line's run of
  characters pays its setup once.
- **Cell size.** Packed cells are 16 bytes against Ghostty's 8, so storing,
  clearing or copying a row moves twice the memory. Getting to 8 bytes means
  interning styles, which is a larger change.
- **What did not help.** Prefetching the scrollback lines about to be reused
  measured slower: with history full, steady-state throughput is the same at
  100 and 9,000 lines, so cold recycled lines are not a bottleneck.

**Variance.** Within isolated processes the ratio repeats to about ±10% on
this machine with 3 s samples. With 500 ms samples, runs straddle the CPU's
turbo drop and single workloads swing by 50% or more. In one long process
running every workload, ratios also drifted by up to 50%, because heap state
carried over between workloads. For that reason the script runs one process
per workload, and `-Baseline` only compares workloads with the same SHA-256
and gates on the ratio, not on absolute MB/s.

## Definition of "matches or beats Ghostty"

A claim needs every row below. A missing or failed row means the claim has
not been established.

1. `vt`: `faster` (or, for "matches", `overlap` with a median ratio ≥ 0.95)
   on every workload, with identical screens, on at least one x64 and one
   arm64 host.
2. The same corpora through upstream `ghostty-bench +terminal-stream` do not
   contradict layer 1. This rules out a P/Invoke artifact.
3. Scrolling allocation is bounded. Steady-state `scrolling` should allocate
   close to 0 B/MiB once history is full, as Ghostty's page reuse does.
4. On Linux/macOS, `time cat` and vtebench place DT at or above Ghostty, run
   under the conditions described in the end-to-end section.
5. Frame latency and dropped frames: measure these even though Ghostty does
   not publish them. Input latency (key to PTY echo to presented frame) is
   unmeasured on both sides, and a DT measurement would be new evidence rather
   than a comparison.

Run `scripts/Invoke-TerminalBenchmarks.ps1 -Baseline` on every engine change
so wins are kept, and record the commit, machine and `vt.json` with each
published number.

## Ghostty reference measurements

These notes were moved here from
[renderer benchmarks](../renderer-benchmarks.md) when the Ghostty engine was
removed.

This repository pinned Ghostty at the commit above (then recorded in
`native/ghostty/ghostty-upstream.json`). Build **that** source with Zig 0.16.0
using `zig build -Demit-bench -Doptimize=ReleaseFast` (on macOS add
`-Demit-macos-app=false`). With the pinned revision on Windows, build the
benchmark-only/lib-vt targets instead; the default full target failed to
link because of a duplicate `memset` symbol:

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
used by this project had no Ghostty graphics renderer.

## Renderer comparison against Ghostty (Linux/macOS)

Use the general procedure in
[renderer benchmarks](../renderer-benchmarks.md#full-renderer-comparison-linuxmacos).
For Ghostty specifically, instrument upstream Ghostty at `RenderState.update`
and `renderer/generic.zig:drawFrame`. Ghostty's native GUI is available on
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
The Windows offscreen raster and upstream `screen-clone` results measure
different work and cannot satisfy the presented-frame gate.
