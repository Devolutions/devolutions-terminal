# Devolutions Terminal

A cross-platform terminal emulator implemented in C# on **.NET 10**, published
with **NativeAOT**, and rendered with **Avalonia 12** / Skia:

- **ConPTY** on Windows and a real **forkpty** transport on Linux and macOS
- Azure Cloud Shell for remote Azure sessions
- selectable built-in or **Ghostty** VT engine
- Windows Terminal-compatible `settings.json`, actions, keybindings, and `dt` CLI

This is the [Devolutions Terminal](https://github.com/Devolutions/devolutions-terminal)
source tree. Projects, namespaces, and the GUI host use `Devolutions.Terminal.*`.
The CLI executable is `dt`.

## Build and run

```powershell
dotnet test Devolutions.Terminal.slnx
dotnet run --project src/Devolutions.Terminal
```

## NativeAOT publish

```powershell
dotnet publish src/Devolutions.Terminal -c Release -r win-x64 --self-contained
```

The native executable is written to
`src/Devolutions.Terminal/bin/Release/net10.0/win-x64/publish/Devolutions.Terminal.exe`.

macOS NativeAOT app bundles (Darwin only):

```bash
scripts/Build-MacOsPackage.sh osx-arm64 0.1.0 artifacts/packages
bash scripts/Test-MacOsPackage.sh osx-arm64 artifacts/packages/*.zip
```

Linux x64 and ARM64 NativeAOT packages are built on Linux with:

```bash
scripts/Build-LinuxPackage.sh linux-x64 0.1.0 artifacts/packages all
scripts/Build-LinuxPackage.sh linux-arm64 0.1.0 artifacts/packages all
bash scripts/Test-LinuxPackage.sh linux-x64 artifacts/packages/*-linux-x64.*
```

The builder emits `.tar.gz`, `.deb`, `.rpm`, `.AppImage`, and `.sha256` files.
Every format consumes the same normalized `/opt/devolutions-terminal` payload
and `/usr` desktop integration layout. `SOURCE_DATE_EPOCH` controls package
timestamps.

After extracting a tarball at the filesystem root:

```bash
sudo /opt/devolutions-terminal/linux/Install-LinuxDesktopIntegration.sh install
/opt/devolutions-terminal/linux/Install-LinuxDesktopIntegration.sh register-protocol
/opt/devolutions-terminal/linux/Install-LinuxDesktopIntegration.sh set-default-terminal
```

## MSIX packages

```powershell
.\src\Devolutions.Terminal.Package\Scripts\Build-Packages.ps1
```

Development signing, trust, install, validation, and uninstall commands are in
[`src/Devolutions.Terminal.Package/README.md`](src/Devolutions.Terminal.Package/README.md).

## Settings and engines

Settings are stored at `%LOCALAPPDATA%\Devolutions\Terminal\settings.json` on
Windows, under `$XDG_CONFIG_HOME/devolutions-terminal` on Linux (usual
`~/.config` fallback), and at
`~/Library/Application Support/Devolutions/Terminal/` on macOS.
Set `WT_BASE_SETTINGS_PATH` to use a directory for `settings.json` and
`state.json` (same contract as Devolutions' Windows Terminal distribution).
Set `DTERM_SETTINGS_PATH` (or `WT_DOTNET_SETTINGS_PATH`) to load a specific
settings file. On Windows, `WT_PARENT_WINDOW_HANDLE` embeds the window as a
child of that HWND. `alwaysShowTabs: false` hides the tab row when only one
tab is open.

Set `"experimental.terminalEngine": "ghostty"` to use the pinned
`libghostty-vt` engine globally. A profile can override it with `"builtin"` or
`"ghostty"`. ConPTY remains the Windows process transport for both engines.

The compiled-XAML settings editor is documented in
[docs/settings-editor.md](docs/settings-editor.md).

## Architecture

```
Devolutions.Terminal.Core         VT parser + text buffer + terminal engine
Devolutions.Terminal.Ghostty      NativeAOT-safe libghostty-vt engine adapter
Devolutions.Terminal.Render       Immutable plans + HarfBuzz/Skia glyph renderer
Devolutions.Terminal.Connection   ConPTY + Linux PTY + Azure Cloud Shell
Devolutions.Terminal.Settings     Layered Windows Terminal-compatible JSON settings
Devolutions.Terminal.Control      Avalonia TermControl renderer
Devolutions.Terminal.App   Tabs, title bar, panes, actions, window behavior
Devolutions.Terminal       NativeAOT executable and composition root
```

The measured remaining parity contract is in
[docs/parity-status.md](docs/parity-status.md).
Architecture decisions are recorded in [docs/decisions](docs/decisions).
Renderer contracts are documented in [docs/renderer.md](docs/renderer.md).
Control, clipboard, IME, and accessibility contracts are documented in
[docs/control-accessibility.md](docs/control-accessibility.md).
Advanced VT protocols are documented in
[docs/advanced-vt-protocols.md](docs/advanced-vt-protocols.md).
Azure Cloud Shell is documented in [docs/azure-cloud-shell.md](docs/azure-cloud-shell.md).
Build and release gates are documented in [docs/release.md](docs/release.md).

## Compatibility inventory

### Safety and compatibility settings

Large or multi-line pastes requiring confirmation are cancelled unless explicitly
approved. The application prompts asynchronously; closing the prompt cancels the
paste. Embedded controls without a confirmation handler also cancel warned pastes.

`warning.confirmOnClose` applies to user-initiated window, tab, pane, and bulk
close actions. `never` skips confirmation; `always` confirms closing any running
session; `automatic` confirms when an action closes more than one running session
(the legacy `confirmCloseAllTabs` behavior). Already-exited sessions and automatic
process-exit cleanup never prompt.

PTY input is queued in order off the UI thread, with limits of 256 pending writes
and 4 MiB (including framing on Unix). Overflow rejects the entire new write and
reports an error rather than blocking or silently dropping input. Async writes
complete after transport delivery; caller cancellation skips writes not yet
started. Cancelling an in-flight write terminates its session because input may
have been partially delivered and Unix framing cannot safely resume. Closing a
blocked Unix session has a one-second grace period before host termination;
undelivered input is reported.

The editor disables options that are currently retained only for settings-file
compatibility: `compatibility.textMeasurement`, `compatibility.ambiguousWidth`,
`experimental.detectURLs`, and `disableAnimations`. The terminal engine determines
text measurement and character widths. Plain-text URL detection is not implemented;
explicit OSC 8 hyperlinks remain supported. Window/pane animation effects are not
configurable through `disableAnimations`.

Broker retries share active requests and retain completed responses for at least
five seconds after completion. Admission is bounded at 128 active requests and
1024 total retained requests. When full, new requests receive an explicit
unavailable response without executing their action; existing retries still join
their original operation.

The port tracks Windows Terminal settings, actions, VT dispatch, command line,
and settings-page surfaces in
[`compat/windows-terminal.json`](compat/windows-terminal.json). Tests use that
checked-in snapshot. Regenerating it requires a separate Microsoft Windows
Terminal C++ checkout:

```powershell
dotnet run --project tools/Devolutions.Terminal.PortInventory -- <windows-terminal-checkout> compat/windows-terminal.json
```
