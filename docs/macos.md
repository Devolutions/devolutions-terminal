# macOS support

macOS is a first-class host for the managed app, Unix PTY transport, built-in
and Ghostty engines, and NativeAOT `.app` packaging (including signed,
notarized `.dmg` release artifacts). Global hotkeys, default terminal
registration, and Homebrew remain out of scope.

## What works

- Avalonia desktop host (`osx-arm64` / `osx-x64`)
- Local shells through `dt-pty-host` (`forkpty`, same framing as Linux)
- Selectable built-in and Ghostty engines (`libghostty-vt.dylib`)
- Settings at `~/Library/Application Support/Devolutions/Terminal/`
- Native Command-key defaults for clipboard, tabs, windows, search, settings,
  selection, font sizing, full screen, and quit actions
- Native macOS application and window menus, Hide, and Dock reopen after the
  last window is closed
- Login-shell commandlines (`/bin/zsh -l`) so Dock launches pick up `~/.zprofile`
  and Homebrew `PATH`
- `TERM_PROGRAM=Devolutions.Terminal` and extra Homebrew/MacPorts `PATH` entries
  on the PTY host
- Command-click hyperlinks, Command-scroll zoom, Option-as-Meta, Kitty Super
- `public.html` / `public.rtf` clipboard formats, Menlo/Apple Color Emoji
  fallbacks, and file-drop path pasting
- Generated zsh/bash/fish/pwsh/sh profiles (`Devolutions.Terminal.macOS`)
- Hidden Windows inbox profiles that use `%SystemRoot%`
- Opening files and URIs with `open(1)`
- Notifications through `osascript` `display notification`
- `dterm:` URL scheme declared in `macos/Info.plist` (argv and Apple Event
  protocol activation)
- NativeAOT `.app` + zip packaging on Darwin
- Signed, notarized `.dmg` release artifacts via
  `scripts/Release-MacOsPackage.ps1` (CI only; ad-hoc unsigned zip/dmg locally
  or without Apple signing secrets)

## Not bundled yet

- Homebrew cask
- Global hotkeys (broker / `dt -w` still work)
- Default-terminal registration

Ghostty dylibs and `dt-pty-host` are built on restore for `osx-arm64` /
`osx-x64` (macOS 13+). `dt-pty-host` is compiled with Apple clang so it can
link `libutil` from the SDK.

## Build on a Mac

```bash
dotnet test Devolutions.Terminal.slnx
dotnet publish src/Devolutions.Terminal -c Release -r osx-arm64 --self-contained
pwsh scripts/Build-MacOsPackage.ps1 osx-arm64 2026.3.0 artifacts/packages
pwsh scripts/Test-MacOsPackage.ps1 osx-arm64 artifacts/packages/*.zip
pwsh scripts/Test-MacOsRuntime.ps1 artifacts/packages
```

`Build-MacOsPackage.ps1` publishes NativeAOT unless `MACOS_PUBLISH_DIR` is set,
stages `Devolutions Terminal.app` with `macos/Info.plist`, generates
`DevolutionsTerminal.icns` from the original WT Distro vector artwork in
`macos/DevolutionsTerminal.svg`. Its librsvg-rendered 1024px transparent master,
`macos/DevolutionsTerminal.png`, is committed so packaging does not depend on
an additional SVG renderer. Xcode compiles that same master through
`macos/AppIcon.icon` for the native macOS 26 appearance system; the `.icns`
remains the fallback on earlier releases. The script then ad-hoc signs the
bundle and writes a zip plus SHA-256 manifest.

```bash
open "artifacts/packages/Devolutions Terminal.app"
```

Release DMGs use a Finder window layout with a branded installation background,
the app, and an Applications folder shortcut. The writable disk image is sized
from the app bundle (plus HFS+/Finder headroom) rather than a fixed volume
size. The background is maintained as `macos/InstallerBackground.svg` and
committed as `macos/InstallerBackground.png` for packaging.
