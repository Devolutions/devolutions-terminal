# Browser (WebAssembly) host

Devolutions Terminal can run as a static Avalonia WebAssembly app. This is a
**minimal, usable** port of the terminal surface — not a full desktop clone.

## What works

- Avalonia 12 Fluent UI in the browser (`ISingleViewApplicationLifetime`)
- `TermControl` + Skia renderer + the built-in VT engine
- An in-process `dt-wasm` shell (`BrowserShellConnection`) with `help`, `echo`,
  `ls`, `cat`, `clear`, `date`, `uname`, `color`, `history`, and `exit`
- A loopback host bridge that renders a real ConPTY / Unix PTY shell in
  `TermControl` when the page is served by `Devolutions.Terminal.Browser.Host`
- A `/dt` control channel that lists settings profiles and opens each launch
  as its own tab with a dedicated WebSocket transport
- Headless TermControl tests plus an optional Playwright pass against the
  published `wwwroot`

## What cannot work in the browser

| Desktop capability | Why it is out of scope |
| --- | --- |
| ConPTY / `forkpty` inside the page | No process or PTY APIs in the WASM sandbox. A loopback host process can own the PTY and bridge it over WebSocket. |
| Ghostty (`libghostty-vt`) | Native library, not compiled to WASM |
| `dt` CLI, broker, multi-window | Separate OS processes and named endpoints |
| Package identity, toasts, jump lists, global hotkeys | Windows / desktop shell |
| Native file dialogs, unrestricted filesystem | Browser sandbox (MEMFS only) |
| Azure Cloud Shell | Possible later (HTTP + WebSocket), not in this host |

Clipboard, IME, and accessibility are whatever the browser + Avalonia.Browser
backend. Performance and download size are worse than NativeAOT desktop.

## Run locally

Requires the `wasm-tools` workload matching `global.json`:

```powershell
dotnet workload install wasm-tools
dotnet run --project src/Devolutions.Terminal.Browser
```

Publish a static site:

```powershell
dotnet publish src/Devolutions.Terminal.Browser -c Release -o artifacts/browser-wasm
```

Serve `artifacts/browser-wasm/wwwroot` (or the SDK `publish/wwwroot`) with any
static file server. MIME type `application/wasm` is required for `.wasm`.
The host disables WASM asset fingerprinting so `wwwroot/main.js` can import
`_framework/dotnet.js` by a stable name. A plain static server has no
`/pty/health` bridge, so the page stays on `dt-wasm`.

## Real host shell

`Devolutions.Terminal.Browser.Host` binds `http://127.0.0.1` only, serves the
published site, and exposes the dt settings catalog over a WebSocket control
channel at `/dt`. Launching a profile opens a tab whose bytes travel on their
own `/pty/{sessionId}` socket. The transport scheme follows the page: `wss`
when the page is HTTPS, `ws` on this HTTP loopback host. The browser sends a
profile id, never a command line. The host does not send command lines or
starting directories back to the page. Profiles with `elevate` are listed and
cannot be launched. Open `http://127.0.0.1:<port>/` — not a LAN
address.

```powershell
dotnet run --project src/Devolutions.Terminal.Browser.Host -- --wwwroot artifacts/browser-wasm/wwwroot
```

On Linux and macOS, building the loopback host also builds and copies the
`dt-pty-host` helper needed to launch local shells.

This is a local development bridge, not remote access. `GET /pty/health`
returns `X-Dterm-Host: pty` and `X-Dterm-Control: /dt`. Azure Cloud Shell
profiles are listed and cannot be launched here. A page with no control
channel stays on the single `dt-wasm` or legacy `/pty` shell. On Windows the
host spaces ConPTY input writes: a raw/no-echo reader such as `sudo` drops a
burst, so a quickly typed password would otherwise be truncated. A paste
remains one write. Modifier key-downs are not forwarded: ConPTY would write
them as NUL and reject a shifted password. Ctrl+C is sent as ETX so the
prompt can be cancelled.

## Tests

Headless engine/control coverage always runs:

```powershell
dotnet test tests/Devolutions.Terminal.Connection.Tests --filter BrowserShell
dotnet test tests/Devolutions.Terminal.Control.Tests --filter TermControlBrowserShell
```

`RawModeBurstReachesWslReader` covers the ConPTY input pace. It skips when no
WSL distribution is installed.

End-to-end against Chromium (installs `wasm-tools` browsers on demand):

```powershell
scripts/Test-BrowserWasm.ps1
```

Set `DTERM_BROWSER_E2E=1` to opt the Playwright test in. Leave it unset so
`dotnet test Devolutions.Terminal.slnx` stays free of `wasm-tools`.
