# Terminal renderer

`Devolutions.Terminal.Render` converts detached `TerminalSnapshot` instances into immutable
row/run plans and paints those plans directly to Avalonia's Skia canvas.

## Pipeline

1. `TerminalRenderPlanner` resolves color, faint/inverse state, hyperlink
   boundaries, and terminal text clusters with their original cell coordinates.
2. `SkiaTerminalRenderer` selects the configured face, then Cascadia Mono,
   Consolas, Segoe UI Emoji, and finally Skia's platform fallback.
3. HarfBuzz shapes each cell cluster with its complete same-style run as context.
   Contextual scripts, combining text, and emoji ZWJ sequences retain shaping
   context, while every cluster origin remains fixed to the terminal grid.
4. `TerminalSkiaDrawOperation` leases Avalonia's active canvas and draws cached
   Skia text blobs, decorations, overlays, and the cursor.

The renderer handles normal/bold/italic faces with synthetic bold or skew only
when the selected family lacks the requested face. It supports faint,
underline, strikethrough, hyperlink underline, CJK wide cells, color emoji,
common Powerline separators, and all Windows Terminal cursor shapes.

Windows Terminal font sizes are point sizes, not raw Avalonia DIPs. The renderer
uses the same 96/72 point-to-DIP conversion and nearest-pixel cell metric
rounding as Atlas. The packaged Cascadia Mono regular and italic faces remain
terminal-only resources; Fluent application chrome uses the platform Segoe UI
family.

## Cache and invalidation

The glyph cache is an LRU with a configurable hard capacity (4096 entries by
default). Keys include text content, style, font size, and render scale. Cache
entries own their `SKTextBlob`; eviction and renderer disposal release them
deterministically. Typeface and paint resources are renderer-owned and are not
shared with Avalonia's graphics device.

`Resize` clears shaped resources when render scale changes. `Invalidate` clears
them after a font or graphics-resource change. No GPU surface or canvas is held
across draw calls, so Avalonia device loss or backend recreation cannot leave a
stale device object in the renderer.

`TerminalFrameDiffer.GetDirtyRows` is the renderer-neutral dirty-region
contract. The control retains the previous frame and computes changed text and
cursor rows for compositor integration. Dynamic selection, search, and hovered
hyperlink ranges are separate overlays so they do not invalidate glyph entries.

## Output-pump coalescing

The ConPTY read loop raises one engine invalidation per 16 KiB read. `TermControl`
collapses queued invalidations into a single trailing UI-thread drain
(`DrainEngineInvalidation`): a producer that finds the pending flag already set
adds no new dispatcher post, and a drain that observes fresh output re-queues
itself. The engine feed itself stays on the PTY thread so cursor-position report
replies (CSI 6n) return before the application prints more. The drain is what
fires the accessibility, scroll-mark, viewport, and IME-cursor notifications, so
listener fan-out runs once per burst instead of once per chunk.

The cursor blink timer is damage-gated: a tick only invalidates when it can
change pixels — the pane must be focused (unfocused panes draw a static cursor),
the engine must report a blinking, visible cursor (DECSET 12/25), and the
control must be effectively visible. Losing focus out of a mid-blink dark phase
repaints once so the static cursor is solid.

`tools/Devolutions.Terminal.Bench` measures this path end to end
(`bench control --mb 8`): a deterministic 8 MiB corpus is fed in 16 KiB chunks
through a fake connection with dispatcher drains paced at 60 Hz, reporting
engine invalidations vs posts vs drains alongside MB/s (medians over runs).
Coalescing takes an 8 MiB burst from 514 drains to roughly one per frame.

## Performance contract

Steady-state paint reuses Skia paints, cached text blobs, Powerline paths, and a
cached shaping delegate. Indexed loops avoid interface-enumerator allocations.
The immutable frame is the allocation boundary: changing terminal content
allocates a new snapshot/plan, while repainting an unchanged frame does not
allocate per cell.

Headless tests cover cache bounds and reuse, Unicode shaping, overlay ordering,
cursor geometry, scale invalidation, dirty rows, and warm-render allocation.
