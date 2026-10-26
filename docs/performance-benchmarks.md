# Performance benchmarks vs Ghostty

This page explains how Ghostty backs up its "fastest terminal" positioning,
what its own benchmarks do and do not measure, and how DT benchmarks its
**builtin** engine and renderer against it. The goal is a claim that holds up
under review: "with the default engine, DT matches or beats Ghostty at
workload X, measured this way, on this machine."

For renderer-only details (caches, pixel verification, historical numbers)
see [renderer benchmarks](renderer-benchmarks.md).

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
pwsh scripts/Invoke-TerminalBenchmarks.ps1 -Baseline artifacts/bench/<previous>/vt.json   # fails if builtin/ghostty-vt drops >15% on identical inputs
pwsh scripts/Invoke-TerminalBenchmarks.ps1 -RequireBeatGhostty                             # fails unless builtin wins every vt workload
pwsh scripts/Invoke-TerminalBenchmarks.ps1 -GhosttyBench <path>\ghostty-bench.exe          # also replays the corpora through upstream +terminal-stream
```

`-GhosttyBench` times whole processes, as Ghostty does, so its figures include
process startup. It cross-checks the in-process `ghostty-vt` target; it does
not replace it. Build `ghostty-bench` as described in
[renderer benchmarks](renderer-benchmarks.md#ghostty-reference-measurements).

### End-to-end (layer 5) on Linux/macOS

Ghostty's only cross-terminal numbers come from `time cat` and vtebench. To
reproduce them, run on one native Linux or macOS machine, with both apps in
Release and matching font, size, grid and scrollback:

- `time cat` of a large ASCII file and of a large CJK file, 10 runs each.
- vtebench's 12 workloads, run inside each terminal. vtebench measures how fast
  the terminal drains the PTY (backpressure), not paint time.

Never run both terminals at the same time; alternate their order and report
the spread. The checklist in
[renderer benchmarks](renderer-benchmarks.md#full-renderer-comparison-linuxmacos)
lists the full set of gates.

## Current results

Setup for these numbers:

- One Windows 11 x64 workstation (24 logical CPUs, AVX2), .NET 10.0.13 (JIT,
  tiered compilation off), and the pinned `libghostty-vt` (ReleaseFast).
- 120x30 grid, 16 KiB chunks, 8 MiB per workload, 7 timed runs plus 1 warmup.
- Each workload ran in its own process, via:

  ```powershell
  pwsh scripts/Invoke-TerminalBenchmarks.ps1 -Suite vt,render
  ```

This machine's absolute speed drifted by up to 2.5x between invocations
(Ghostty itself moved from ~540 to ~1,600 MB/s on `light-cells`). So read the
**builtin / ghostty-vt ratio**, which is measured within each run, and treat
anything within about ±10% of 1.0 as a tie. Throughput is the median MB/s.

### Ingest: builtin engine vs raw `libghostty-vt`

| Workload | builtin | ghostty-vt | ghostty (DT) | ratio | verdict | builtin alloc / MiB | screens |
| --- | ---: | ---: | ---: | ---: | --- | ---: | --- |
| ascii | 1,144 | 922 | 411 | **1.24x** | overlap | 31 KB | identical |
| ascii-lines | 879 | 741 | 361 | **1.19x** | **faster** | 40 KB | identical |
| light-cells | 2,024 | 1,647 | 60 | **1.23x** | **faster** | 0 | identical |
| scrolling | 42.5 | 31.9 | 29.5 | **1.33x** | overlap | 0.7 MB | identical |
| dense-cells | 207 | 144 | 53 | **1.44x** | **faster** | 0 | identical |
| styled | 78.4 | 26.9 | 24.8 | **2.91x** | **faster** | 5.4 MB | 1 row differs |
| osc | 124 | 27.0 | 25.1 | **4.59x** | **faster** | 6.9 MB | identical |
| mixed | 667 | 362 | 227 | **1.84x** | **faster** | 54 KB | identical |
| scroll-region | 641 | 669 | 56 | 0.96x | overlap | 0 | identical |
| cursor-motion | 152 | 168 | 94 | 0.90x | slower | 33 KB | identical |
| emoji | 588 | 736 | 267 | 0.80x | overlap | 61 KB | identical |
| utf8 | 61.3 | 79.6 | 70.1 | 0.77x | slower | 8.7 MB | identical |
| cjk | 458 | 596 | 299 | 0.77x | slower | 78 KB | identical |

`ghostty (DT)` skips projection without `--frame-kb`. The one-row difference in
`styled` is a behavior difference, not a measurement artifact: builtin drops a
combining mark that follows a space, and Ghostty keeps it.

How builtin got here, as the median builtin/ghostty-vt ratio per round (each
round measured against its own in-run Ghostty control):

| Workload | `dc1cd26` | round 1 | round 2 (packed cells) | round 3 | round 4 |
| --- | ---: | ---: | ---: | ---: | ---: |
| ascii | 0.009 | 0.20 | 0.55 | 1.21 | 1.24 |
| ascii-lines | 0.009 | 0.20 | 0.50 | 1.13 | 1.19 |
| light-cells | 0.009 | 0.32 | 0.48 | 1.20 | 1.23 |
| scrolling | 0.015 | 0.14 | 0.40 | 1.11 | 1.33 |
| dense-cells | 0.42 | 0.63 | 0.62 | 1.57 | 1.44 |
| styled | 0.30 | 1.33 | 1.14 | 2.82 | 2.91 |
| osc | 0.29 | 2.60 | 4.52 | 4.57 | 4.59 |
| mixed | 0.03 | 0.39 | 0.45 | 1.56 | 1.84 |
| scroll-region | 0.018 | 0.22 | 0.34 | 0.90 | 0.96 |
| cursor-motion | 0.11 | 0.23 | 0.17 | 0.94 | 0.90 |
| utf8 | 0.10 | 0.36 | 0.27 | 0.57 | 0.77 |
| cjk | — | — | — | 0.53 | 0.77 |
| emoji | — | — | — | 0.50 | 0.80 |

The `-Baseline` gate flagged real regressions twice:

- **Round 2:** utf8 and cursor-motion.
- **Round 4:** dense-cells and cursor-motion, from caching packed templates on
  the short-ASCII path.

Each was confirmed with an interleaved A/B build and fixed before moving on.
Moves of about ±10% between rounds (dense-cells and cursor-motion from round 3
to round 4, for example) are within this machine's noise.

### DT's Ghostty mode vs raw `libghostty-vt`, with frames

These numbers use `--frame-kb 256`, so a projection is taken every 256 KiB.

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

`render`, 120x30, median p50 / p95:

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
`PackedCellStorageTests`). Each new branch was also checked by deliberately
breaking it and confirming a test fails.

- **Packed, reference-free storage** (`CellRow`, `PackedCell`).
  - A cell is 16 bytes with no GC references, down from about 40 with two
    `string?` fields.
  - Combining text and hyperlinks live in per-line side arrays that exist only
    while needed and are freed when the line is recycled.
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
  - **UTF-8 runs:** well-formed UTF-8 runs (2–4 byte sequences decoded inline)
    go to `IVtDispatch.PrintRunes`.
  - **Grapheme joins:** precomputed tables for planes 0 and 1 rule out joins,
    so runes that cannot join are stored a segment at a time. That includes
    emoji after a cell without a trailing ZWJ.
  - **Rows without wide glyphs:** a conservative per-row "may hold wide cells"
    flag means classifying and storing happen in one pass, with no clearing
    around wide glyphs. Every freshly recycled scrollback line qualifies.
- **CSI fast path.** A complete plain CSI sequence is parsed in one pass, then
  goes through the same `DispatchCsi` as the byte path. Parameters are copied
  into reused arrays.
- **C0 fast path.** CR, LF and other controls in ground state go straight to
  `ExecuteC0` instead of through the string-state checks.
- **Scroll regions** rotate line references with `Array.Copy`, not one
  indexer call per line.

### What still separates builtin from Ghostty

- **Non-ASCII text** (`cjk`, `emoji`, `utf8`: about 0.8x). On CJK, per scalar:

  | Stage | Cost |
  | --- | --- |
  | Builtin: decode | ~2.0 ns |
  | Builtin: storage (two 16-byte cells per wide character) | ~3.6 ns |
  | Builtin: dispatch | ~0.6 ns |
  | Ghostty: everything | ~5 ns |

  What was tried and measured:
  - **Runtime SIMD transcoder.** Swapping the scalar decoder for the runtime's
    SIMD `Utf8.ToUtf16` (plus a UTF-16-to-rune pass) was *slower* at these run
    lengths, and was reverted.
  - **Unchecked scalar decoding.** A `ref`-based decoder with unchecked `Rune`
    construction measured the same as the indexed one.

  Closing the rest needs either a purpose-built SIMD UTF-8 decoder or 8-byte
  cells.
- **cursor-motion and scroll-region** are ties within noise (0.90–0.96x). Per
  escape sequence, builtin still pays for an interface call into the engine
  and that call's own checks before each short write.
- **Cell size.** Packed cells are 16 bytes against Ghostty's 8, so a cleared
  or copied row moves twice the memory. Getting to 8 bytes means interning
  styles, which is a larger change.

**Variance.** Within isolated processes the ratio repeats to about ±10% on
this machine. In one long process running every workload, ratios drifted by
up to 50%, because heap state carried over between workloads. For that reason
the script runs one process per workload, and `-Baseline` only compares
workloads with the same SHA-256 and gates on the ratio, not on absolute MB/s.

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
