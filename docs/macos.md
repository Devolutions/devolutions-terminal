# macOS support

macOS is a first-class host for the managed app, Unix PTY transport, built-in
and Ghostty engines, and NativeAOT `.app` packaging. Global hotkeys, default
terminal registration, notarization, DMG, and Homebrew remain out of scope.

## What works

- Avalonia desktop host (`osx-arm64` / `osx-x64`)
- Local shells through `dt-pty-host` (`forkpty`, same framing as Linux)
- Selectable built-in and Ghostty engines (`libghostty-vt.dylib`)
- Settings at `~/Library/Application Support/Devolutions/Terminal/`
- Generated zsh/bash/fish/pwsh/sh profiles (`Devolutions.Terminal.macOS`)
- Hidden Windows inbox profiles that use `%SystemRoot%`
- Opening files and URIs with `open(1)`
- Notifications through `osascript` `display notification`
- `dterm:` URL scheme declared in `macos/Info.plist`
- NativeAOT `.app` + zip packaging on Darwin

## Not bundled yet

- Notarization / DMG / Homebrew cask
- Global hotkeys (broker / `dt -w` still work)
- Default-terminal registration

Ghostty dylibs and `dt-pty-host` are built on restore for `osx-arm64` /
`osx-x64` (macOS 13+). `dt-pty-host` is compiled with Apple clang so it can
link `libutil` from the SDK.

## Build on a Mac

```bash
dotnet test Devolutions.Terminal.slnx
dotnet publish src/Devolutions.Terminal -c Release -r osx-arm64 --self-contained
scripts/Build-MacOsPackage.sh osx-arm64 0.1.0 artifacts/packages
bash scripts/Test-MacOsPackage.sh osx-arm64 artifacts/packages/*.zip
bash scripts/Test-MacOsRuntime.sh artifacts/packages
```

`Build-MacOsPackage.sh` publishes NativeAOT unless `MACOS_PUBLISH_DIR` is set,
stages `Devolutions Terminal.app` with `macos/Info.plist`, generates
`DevolutionsTerminal.icns` from the hicolor PNGs, ad-hoc signs the bundle, and
writes a zip plus SHA-256 manifest.

```bash
open "artifacts/packages/Devolutions Terminal.app"
```

