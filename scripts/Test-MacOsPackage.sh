#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
metadata="$repo_root/macos/package.env"

if (($# < 2)); then
    echo "Usage: $0 <osx-arm64|osx-x64> <app-or-zip> [app-or-zip ...]" >&2
    exit 64
fi
rid="$1"
shift

# shellcheck source=../macos/package.env
source "$metadata"
case "$rid" in
    osx-arm64) expected_arch="arm64" ;;
    osx-x64) expected_arch="x86_64" ;;
    *) echo "Unsupported macOS RID: $rid" >&2; exit 64 ;;
esac
[[ "$(uname -s)" == "Darwin" ]] ||
    { echo "macOS package validation requires Darwin." >&2; exit 78; }
for command in ditto file lipo plutil python3; do
    command -v "$command" >/dev/null 2>&1 ||
        { echo "$command is required for macOS package validation." >&2; exit 69; }
done

work="$repo_root/artifacts/macos-package-validation/$rid-$$"
rm -rf -- "$work"
mkdir -p "$work"
trap 'rm -rf -- "$work"' EXIT

validate_app() {
    local app="$1"
    local label
    label="$(basename "$app")"
    local macos_dir="$app/Contents/MacOS"
    local plist="$app/Contents/Info.plist"
    local icns="$app/Contents/Resources/$ICON_NAME.icns"

    for path in \
        "$macos_dir/$EXECUTABLE_NAME" "$macos_dir/$CLI_NAME" "$macos_dir/$PTY_HOST_NAME" \
        "$macos_dir/$GHOSTTY_LIBRARY" "$macos_dir/libSkiaSharp.dylib" \
        "$macos_dir/libHarfBuzzSharp.dylib" \
        "$macos_dir/THIRD-PARTY-NOTICES-GHOSTTY.txt" \
        "$macos_dir/THIRD-PARTY-NOTICES-NOTO-EMOJI.txt" \
        "$app/Contents/Resources/LICENSE" "$plist" "$icns"; do
        [[ -e "$path" ]] ||
            { echo "$label is missing ${path#"$app/"}." >&2; exit 1; }
    done
    [[ -x "$macos_dir/$EXECUTABLE_NAME" ]] ||
        { echo "$label $EXECUTABLE_NAME is not executable." >&2; exit 1; }
    [[ -x "$macos_dir/$CLI_NAME" ]] ||
        { echo "$label $CLI_NAME is not executable." >&2; exit 1; }
    [[ -x "$macos_dir/$PTY_HOST_NAME" ]] ||
        { echo "$label $PTY_HOST_NAME is not executable." >&2; exit 1; }

    plutil -lint "$plist" >/dev/null
    python3 - "$plist" "$APP_ID" "$EXECUTABLE_NAME" "$MACOS_DEPLOYMENT_TARGET" "$URL_SCHEME" <<'PY'
import plistlib
import sys

path, app_id, executable, minimum, scheme = sys.argv[1:]
with open(path, "rb") as handle:
    plist = plistlib.load(handle)
assert plist["CFBundleIdentifier"] == app_id
assert plist["CFBundleExecutable"] == executable
assert plist["CFBundlePackageType"] == "APPL"
assert plist["LSMinimumSystemVersion"] == minimum
assert plist["NSHighResolutionCapable"] is True
assert scheme in plist["CFBundleURLTypes"][0]["CFBundleURLSchemes"]
assert plist["CFBundleIconFile"] == "DevolutionsTerminal"
PY

    for binary in "$EXECUTABLE_NAME" "$CLI_NAME" "$PTY_HOST_NAME" "$GHOSTTY_LIBRARY" \
        libSkiaSharp.dylib libHarfBuzzSharp.dylib; do
        archs="$(lipo -archs "$macos_dir/$binary")"
        [[ "$archs" == *"$expected_arch"* ]] ||
            { echo "$label $binary has the wrong architecture (lipo: $archs)." >&2; exit 1; }
        file -b "$macos_dir/$binary" | grep -E 'Mach-O' >/dev/null ||
            { echo "$label $binary is not Mach-O." >&2; exit 1; }
    done

    if find "$app" -type f \( -name '*.pdb' -o -name '*.dbg' -o -name '*.key' \
        -o -name '*.pfx' -o -name '*.pem' \) | grep -q .; then
        echo "$label contains debug or private-key files." >&2
        exit 1
    fi
    if find "$app" -name '*.dSYM' | grep -q .; then
        echo "$label contains dSYM bundles." >&2
        exit 1
    fi
}

for package in "$@"; do
    [[ -e "$package" ]] ||
        { echo "Package not found: $package" >&2; exit 66; }
    package="$(cd -- "$(dirname -- "$package")" && pwd)/$(basename -- "$package")"
    case "$package" in
        *.zip)
            extract="$work/$(basename "$package" .zip)"
            mkdir -p "$extract"
            ditto -x -k "$package" "$extract"
            app="$(find "$extract" -maxdepth 2 -name '*.app' -print -quit)"
            [[ -n "$app" ]] ||
                { echo "$(basename "$package") does not contain an app bundle." >&2; exit 1; }
            validate_app "$app"
            ;;
        *.app)
            validate_app "$package"
            ;;
        *)
            echo "Unsupported macOS package: $package" >&2
            exit 64
            ;;
    esac
    echo "Validated $(basename "$package")"
done
