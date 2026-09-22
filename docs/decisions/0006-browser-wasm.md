# ADR 0006: Ship a WASM browser host with an in-process shell

## Status

Accepted.

## Context

The desktop host is NativeAOT Avalonia with ConPTY / Unix PTY and an optional
native Ghostty engine. None of those transports exist in the browser sandbox.
Avalonia 12 can still render the same `TermControl` via `Avalonia.Browser`.

## Decision

- Keep a dedicated `Devolutions.Terminal.Browser` WebAssembly host, not a
  second lifetime inside the NativeAOT executable.
- Reuse `Devolutions.Terminal.Control` / Core / Render / Connection.
- Add `BrowserShellConnection` as the fallback transport under
  `OperatingSystem.IsBrowser()`.
- Optionally pair the page with `Devolutions.Terminal.Browser.Host`, a
  loopback-only process that owns ConPTY / Unix PTY. A `/dt` control socket
  lists settings profiles and launches each as a tab on its own `/pty/{id}`
  WebSocket (`ws` or `wss` matching the page). The browser cannot choose the
  command line.
- Force the built-in VT engine in the browser (no Ghostty).
- Do not include the WASM project in the default `dotnet test` solution graph
  so contributors without `wasm-tools` stay unblocked.
- Prove the stack with in-process connection tests, headless TermControl
  tests, and an opt-in Playwright pass against the published static site.

## Consequences

- The page itself is still not a local shell host. A real shell requires the
  loopback companion process.
- Azure Cloud Shell could be added later because it is HTTP/WebSocket only.
- WASM publish remains a separate CI job from NativeAOT.
