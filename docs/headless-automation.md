# Headless TUI automation

Devolutions Terminal can run a real local TUI in a ConPTY/PTY session without
opening or focusing a terminal window. The automation host owns PTY sessions
and feeds output through DT's terminal engine. An agent can inspect structured
screen rows, cell positions, and style runs; send text or terminal-mode-aware
keys; click terminal cells when the application enables mouse tracking; resize;
wait for visible text; and close sessions.
It can also render offscreen PNGs, including kitty graphics and sixel overlays,
wait for pixel changes, probe input-to-visible-change latency, and sample frame
pacing. None of these operations require desktop capture or a computer-use agent.

The host starts on demand and remains alive between CLI/MCP calls. It uses a
current-user-only local named pipe; it does not listen on a network port. The
feature is **headless, not a process sandbox**: launched applications have the
same filesystem and process permissions as the user running DT.

## CLI

Build and run the CLI from the repository:

```powershell
dotnet run --project src/Devolutions.Terminal.Cli -- automation start --command "pwsh -NoLogo -NoProfile -NoExit" --cols 100 --rows 30
```

The command returns a JSON session object containing its ID. Use that ID for
subsequent operations:

```powershell
dt automation observe <session-id>
dt automation text <session-id> "Write-Output automation-ok"
dt automation key <session-id> Enter
dt automation wait <session-id> automation-ok 10000
dt automation close <session-id>
dt automation stop
```

`start` accepts `--command`, optional `--cwd`, `--cols`, and `--rows`.
`observe` returns visible screen rows, terminal dimensions, cursor position,
title, working directory, process state, and terminal input modes. Each row has
text, sparse non-space cells (`column`, `text`, and display `width`), and
cell-style runs. It also reports retained visible image protocols and anchors,
the negotiated mouse/keyboard modes, and the last 32 terminal diagnostics.
Image dimensions are source dimensions when known, not necessarily displayed
dimensions. Columns are zero-based; a wide cell's width includes its
continuation column. Text and paste are separate operations; paste honors
bracketed-paste mode. `key` accepts terminal key names such as `Enter`,
`Escape`, `Up`, and `F1`; optional modifiers are `Shift`, `Control`, `Alt`, or
`Meta`. `click` takes zero-based cell coordinates and is rejected unless mouse
tracking is enabled. `wait` searches visible text for an exact, case-sensitive
substring and accepts a timeout up to five minutes. Trailing spaces are removed
from each row for the text search. Operations emit JSON on stdout and errors on
stderr.

Sessions persist while the host is running, including across independent CLI
invocations. On Linux and macOS, the on-demand host detaches from the caller's
terminal session. `dt automation stop` closes sessions and stops the host; use
`Ctrl+C` if running `dt automation host` manually.

## Graphics and responsiveness

These commands use the same Skia renderer and render planner as DT's desktop
control, with a CPU raster surface and the host's available fonts. The engine
and PTY receive the renderer's cell geometry, including terminal pixel-size
query responses. Rendering does not open an Avalonia window.

```powershell
# Save pixels locally; stdout contains frame metadata, not base64.
dt automation capture <session-id> frame.png

# Restrict observation to the remote desktop, excluding a clock/status bar.
$region = '{"column":0,"row":2,"columns":80,"rows":20}'
$frame = dt automation capture <session-id> desktop.png $region | ConvertFrom-Json
dt automation wait-change <session-id> $frame.hash 10000 $region

# Captures a pixel baseline, sends input, and times the first visible change.
dt automation probe <session-id> '{"type":"key","key":"Enter"}' 2000 $region

# Sample for 3 seconds, at a requested 16 ms interval.
dt automation measure <session-id> 3000 16 $region

# Drag and scroll without moving the OS pointer.
dt automation mouse <session-id> press 10 5 0
dt automation mouse <session-id> move 20 8 0
dt automation mouse <session-id> release 20 8 0
dt automation mouse <session-id> scroll-down 20 8
dt automation key-event <session-id> A release
```

Regions use zero-based terminal cells, not pixels, and must fit inside the grid.
A capture returns PNG pixel dimensions, cell dimensions, its region, revision,
SHA-256 pixel hash, and render duration. Hashes include image pixels and
dimensions, not the revision: protocol traffic that changes no pixels does not
count as a visual update. Cursor drawing is excluded by default to avoid false
positives; MCP capture can explicitly include it for inspection. Use the same
region and a cursor-free capture for `wait-change`. Resize can invalidate a
previously selected region.

`probe` accepts an input object with `type` set to `text`, `paste`, `key`,
`click`, or `mouse`. Text/paste require `text`; keys require `key`, with optional
`event` (`press`, `repeat`, `release`) and `modifiers`. Mouse input requires
`event`, `column`, `row`, and optionally `button` and `modifiers`. Click uses
`column`, `row`, and optional `button`. A probe reports whether pixels changed
and elapsed milliseconds measured on the host's monotonic clock. The timer
starts after the PTY connection has completed writing the input; baseline
rasterization is excluded. On timeout, the returned frame is the latest
available render. A timeout is an explicit `matched: false`, not a successful
responsiveness result.

Mouse events include press/release/move and scroll-up/down/left/right, with
Shift/Control/Alt modifiers. Drag state is retained between calls; motion is
rejected unless the application negotiated button-motion or all-motion
tracking. Coordinates remain terminal-cell coordinates, not pixel-precise
remote-desktop coordinates. Key release requires a keyboard protocol that
encodes events (kitty or Win32 input); unsupported events return errors.
Printable keys can accept an optional `keySymbol` Unicode scalar in key input,
`key-event`, and `key` commands. Provide it for layout-sensitive characters
when the named key alone cannot determine what the active keyboard layout
produces.

`measure` returns timestamped frame hashes, observed pixel changes per second,
render timing minimum/median/p95/maximum, intervals between sampled changes,
the longest unchanged period, received PTY bytes, and parser processing time.
Send input concurrently through another CLI invocation or MCP request to
exercise the application during sampling. Duration is 16..30000 ms and interval
16..1000 ms, no longer than the duration, with at most 600 samples including
the baseline and final sample.

Interpret results carefully:

- The sampler can miss intermediate frames; **observed changes/sec is not
  application FPS**, and PTY read batches are not frame boundaries.
- Rendering, snapshotting, hashing, and scheduling add overhead. Render timings
  cover planning, rasterization, and pixel hashing, not PNG encoding, IPC,
  GPU presentation, or a physical display. PNG encoding happens only on capture.
- Unchanged pixels can mean an idle desktop, not a stall. A changed pixel can
  be unrelated animation or local echo, not a completed remote action. Select
  a target region, inspect the PNG, and assert the expected outcome separately.
- A probe is **input-to-first-observed-pixel-change**, not proof of causal
  remote-desktop round-trip latency. True remote/server-to-display latency
  needs an identifiable remote response or instrumentation in the client/server.

## MCP server

Build or publish `src/Devolutions.Terminal.Automation.Mcp` separately and
configure the agent to launch `dt-automation-mcp` as a stdio MCP server:

```powershell
dotnet publish src\Devolutions.Terminal.Automation.Mcp -c Release
```

The MCP executable is not currently included in DT's desktop packages. The
server exposes `start`, `list`, `observe`, `send_text`, `paste`, `send_key`,
`click`, `resize`, `wait_for_text`, and `close` tools. It connects to the same
per-user automation host as the CLI and starts that host on demand.
The visual tools are `capture`, `wait_for_change`, `probe_response`, and
`measure`; `mouse` and `key_event` provide extended input. `capture` returns a
native MCP `image/png` content block plus frame metadata, so vision-capable
agents can inspect graphics directly rather than interpret base64 text.
Other tools return structured data. Regions and probe inputs are typed tool
arguments, not JSON strings as in the CLI.

The MCP server locates the `dt` CLI from `PATH` by default. Set
`DTERM_AUTOMATION_HOST` to the CLI executable path if it is not on `PATH`.
Example MCP server configuration (adjust executable paths to the published
location):

```json
{
  "mcpServers": {
    "devolutions-terminal": {
      "command": "C:\\Program Files\\Devolutions\\Terminal\\dt-automation-mcp.exe",
      "env": {
        "DTERM_AUTOMATION_HOST": "C:\\Program Files\\Devolutions\\Terminal\\dt.exe"
      }
    }
  }
}
```

## Scope and limitations

- Captures are terminal-local CPU-rendered PNGs, not desktop screenshots.
  Video recording, audio, physical display timing, and remote-server
  instrumentation are not provided.
- A host supports up to 16 sessions and 16 concurrent text/visual waits,
  probes, or measurements combined. A terminal
  grid may contain at most 100,000 cells. IPC messages are limited to
  1,048,576 characters except capture responses, which allow 24 MiB for PNG
  transport. A capture region may contain at most 4,194,304 pixels and its PNG
  at most 16 MiB. Oversized responses return explicit errors.
- Terminal cells do not carry generic widget semantics. Automation can query
  screen text and cell styles, but applications must expose their own semantic
  interface for higher-level controls.
- The host launches local commands with the current user's permissions.
  Current-user-only IPC prevents other local accounts from controlling it, but
  does not isolate a launched command from the user's files or processes.
- The CLI and MCP server do not move the OS pointer, send global keystrokes, or
  activate windows.
