# Ghostty terminal engine

The Windows host always uses ConPTY for local processes. The optional Ghostty
mode replaces the VT parser/state engine, not the Windows pseudoconsole or the
Avalonia renderer.

Select it globally:

```json
{
  "experimental.terminalEngine": "ghostty"
}
```

Or override a profile:

```json
{
  "profiles": {
    "list": [
      {
        "name": "PowerShell",
        "experimental.terminalEngine": "ghostty"
      }
    ]
  }
}
```

`builtin` remains the default. New and restarted terminal sessions resolve the
setting; an already running session keeps its current engine.

## Native integration

`Devolutions.Terminal.Ghostty` uses source-generated `LibraryImport` calls and safe handles.
RID-specific `libghostty-vt` binaries are **built from source** on restore and
copied into JIT, NativeAOT, and MSIX outputs. The ABI type manifest is validated
at startup. See `native/ghostty/ghostty-upstream.json` and
`native/Restore-NativeLibraries.ps1`.

## Stack headroom

The engine's P/Invoke calls execute on whatever thread the host is using —
including the ConPTY read loop's thread-pool thread with the default ~1 MB stack
reserve. Full libghostty (renderer included) has overflowed a 1 MB stack in
other hosts; the VT-only `libghostty-vt` has a much smaller surface. A test
(`EngineSurvivesAdversarialCorpusOnSmallStackThread`) feeds an adversarial
corpus on a 256 KB-stack thread — 4x below the host default — to pin that
headroom. If the pinned upstream changes and that test fails, route engine calls
onto a dedicated thread with an explicit `maxStackSize`.

## Current rendering boundary

Ghostty owns VT parsing, modes, viewport state, resize/reflow, scrollback,
terminal replies, titles, and bells. Its render-state API is projected into the
shared immutable cell model consumed by the existing Skia renderer. This keeps
Avalonia tabs, panes, accessibility, clipboard, selection, search, and window
behavior common across engines.

The pinned C ABI exposes no Sixel, OSC 1337, or ConEmu image resources. The
engine excludes those image capability flags and emits deterministic unsupported
diagnostics instead of pretending to project graphics or silently switching the
selected engine.
