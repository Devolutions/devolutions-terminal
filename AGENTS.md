# AGENTS.md

Guidance for AI coding agents (and humans skimming for conventions) working in
this repository.

## Repository

Devolutions Terminal — an Avalonia-based cross-platform terminal, built with
.NET (NativeAOT), targeting Windows, Linux, and macOS. Commands below assume
the repository root as the working directory.

## Build and test

See [CONTRIBUTING.md](CONTRIBUTING.md) and [docs/release.md](docs/release.md)
for full build, packaging, and release details. In short:

```powershell
dotnet restore Devolutions.Terminal.slnx
dotnet build Devolutions.Terminal.slnx -c Debug
dotnet test Devolutions.Terminal.slnx -c Release
```

Warnings are treated as errors for production projects; keep NativeAOT
publish green.

## Scripting language preference

Prefer **PowerShell 7 (`pwsh`)** for new automation and CI scripts, over Bash
or Python. PowerShell 7 is cross-platform (Windows, Linux, macOS) and is
already the primary scripting language for Windows packaging in this repo
(`src/Devolutions.Terminal.Package/Scripts/*.ps1`,
`scripts/Test-PublishedApp.ps1`).

- Write new scripts as `.ps1` and invoke them with `pwsh` (not `powershell.exe`),
  so they run identically in CI (Windows/Linux/macOS runners) and locally.
- The macOS packaging scripts (`scripts/*MacOs*.ps1`) have been rewritten to
  PowerShell and are the canonical implementation; do not reintroduce Bash or
  Python for them. Linux packaging scripts (`scripts/*Linux*.sh`,
  `scripts/*.py`) have not been converted yet — that's a known follow-up.
  Prefer PowerShell for any new script, and migrate an existing Bash/Python
  script to PowerShell when a change already requires substantial rework of it.
- Bash is still acceptable where a step is inherently platform-specific and
  wraps a Unix-only tool (e.g. `codesign`, `hdiutil`, `iconutil`, `sips` for
  macOS packaging) — the surrounding orchestration script itself can still be
  PowerShell, calling out to `bash`/native tools as needed.

## Native helpers

- Linux/macOS PTY host: `native/linux-pty` (`dt-pty-host.c`)
- Ghostty VT library: `native/ghostty`
- Windows Explorer/toast helpers: `native/windows-shell`

`dotnet build` restores these for the host RID automatically
(`-p:SkipNativeRestore=true` to skip).

## Platform packaging entry points

- Windows: `src/Devolutions.Terminal.Package/Scripts/Build-Packages.ps1` (MSIX/MSI)
- Linux: `scripts/Build-LinuxPackage.sh` (tar/DEB/RPM/AppImage)
- macOS: `scripts/Build-MacOsPackage.ps1`, `scripts/Release-MacOsPackage.ps1`
  (app bundle, zip, signed/notarized `.dmg`) — see [docs/macos.md](docs/macos.md)
