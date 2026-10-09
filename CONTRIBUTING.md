# Contributing

This repository is Devolutions Terminal. Run commands from the repository root.

## Build and test

```powershell
dotnet test Devolutions.Terminal.slnx
dotnet run --project src/Devolutions.Terminal
```

Browser WASM (requires `wasm-tools`):

```powershell
scripts/Test-BrowserWasm.ps1
dotnet run --project src/Devolutions.Terminal.Browser
dotnet run --project src/Devolutions.Terminal.Browser.Host -- --wwwroot artifacts/browser-wasm/wwwroot
```

The host binds loopback only. `/dt` lists settings profiles and launches each
as a tab on `/pty/{id}` (`ws` on this HTTP host, `wss` when the page is HTTPS).
The browser never supplies a command line, and the host does not send one back.
Elevated profiles are listed and refused.

Warnings are errors. Keep the default NativeAOT desktop publish green.
Iseberg is enabled by default; its engine and regression suite require separately
installed PowerShell 7.4.6+ within 7.4.x, 7.5.x or 7.6.x with its bundled runtime.
See [Iseberg](docs/iseberg.md) for the
subprocess/module boundary.

## Desktop publish

```powershell
dotnet publish src/Devolutions.Terminal -c Release -r win-x64 --self-contained
```

The default desktop and release packages use NativeAOT and include Iseberg.
Only the private PowerShell bridge is managed: it is imported by the installed
`pwsh` subprocess, never loaded into DT. Use `-p:EnablePowerShellIse=false`
consistently across build/test/publish commands for a terminal-only configuration.
The CLI remains NativeAOT in both variants.

Linux packages:

```bash
scripts/Build-LinuxPackage.sh linux-x64 2026.3.0 artifacts/packages all
```

Windows MSIX:

```powershell
.\src\Devolutions.Terminal.Package\Scripts\Build-Packages.ps1
```

Details are in [docs/release.md](docs/release.md).

## Compatibility inventory

[`compat/windows-terminal.json`](compat/windows-terminal.json) is the checked-in
Windows Terminal surface snapshot. Regenerating it requires a separate
[microsoft/terminal](https://github.com/microsoft/terminal) C++ checkout:

```powershell
dotnet run --project tools/Devolutions.Terminal.PortInventory -- <windows-terminal-checkout> compat/windows-terminal.json
```

Do not point that tool at this repository.

## Native helpers

- Linux/macOS PTY host: `native/linux-pty` (`dt-pty-host.c`; Zig `cc` on Linux, Apple clang on macOS)
- Ghostty VT library: `native/ghostty` (Zig build of pinned Ghostty)
- Windows Explorer/toast helpers: `native/windows-shell` (MSVC, gitignored `bin/`)

`dotnet build` restores Ghostty and `dt-pty-host` for the host RID (Zig is
downloaded into `artifacts/tools` on first use). See
[`native/ghostty/README.md`](native/ghostty/README.md). Pass
`-p:SkipNativeRestore=true` to skip.

## macOS

See [docs/macos.md](docs/macos.md). On a Mac:

```bash
dotnet test Devolutions.Terminal.slnx
dotnet test Devolutions.Terminal.slnx
pwsh scripts/Build-MacOsPackage.ps1 osx-arm64 2026.3.0 artifacts/packages
```

`dotnet build` restores Ghostty and `dt-pty-host` for `osx-arm64` / `osx-x64`.
The PTY host is compiled with Apple clang against the macOS 13 SDK. App-bundle
packaging is Darwin-only; release builds are signed and notarized with a
Developer ID identity (see [docs/macos.md](docs/macos.md) and
[docs/release.md](docs/release.md)). Homebrew is not included.
