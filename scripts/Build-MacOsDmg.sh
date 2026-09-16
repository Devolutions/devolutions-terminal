#!/usr/bin/env bash
# Builds a distributable .dmg for a (signed or ad-hoc-signed) Devolutions
# Terminal .app bundle: a compressed UDZO disk image containing the app and
# an /Applications symlink for drag-to-install. If a signing identity is
# supplied, the disk image itself is codesigned as well.
set -euo pipefail
export LC_ALL=C

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
metadata="$repo_root/macos/package.env"

if (($# < 4)); then
    echo "Usage: $0 <app-path> <version> <osx-arm64|osx-x64> <output-dir> [signing-identity]" >&2
    exit 64
fi
app_path="$1"
version="$2"
rid="$3"
output_dir="$4"
identity="${5:-}"

# shellcheck source=../macos/package.env
source "$metadata"

[[ "$(uname -s)" == "Darwin" ]] ||
    { echo "macOS disk images must be built on Darwin." >&2; exit 78; }
[[ -d "$app_path" ]] ||
    { echo "App bundle not found: $app_path" >&2; exit 66; }
[[ "$version" =~ ^[0-9][0-9A-Za-z.+~_-]*$ ]] ||
    { echo "Invalid macOS package version: $version" >&2; exit 64; }
case "$rid" in
    osx-arm64|osx-x64) ;;
    *) echo "Unsupported macOS RID: $rid" >&2; exit 64 ;;
esac
for command in hdiutil shasum; do
    command -v "$command" >/dev/null 2>&1 ||
        { echo "$command is required to build a macOS disk image." >&2; exit 69; }
done

mkdir -p "$output_dir"
output_dir="$(cd -- "$output_dir" && pwd)"
base="$PACKAGE_NAME-$version-$rid"
dmg_path="$output_dir/$base.dmg"
rm -f -- "$dmg_path"

work="$repo_root/artifacts/macos-dmg-staging/$rid-$$"
rm -rf -- "$work"
staging="$work/staging"
mkdir -p "$staging"
trap 'rm -rf -- "$work"' EXIT

cp -a "$app_path" "$staging/$BUNDLE_NAME"
ln -s /Applications "$staging/Applications"

source_date_epoch="${SOURCE_DATE_EPOCH:-}"
if [[ -z "$source_date_epoch" ]]; then
    source_date_epoch="$(git -C "$repo_root" log -1 --format=%ct 2>/dev/null || python3 -c 'import time; print(int(time.time()))')"
fi
[[ "$source_date_epoch" =~ ^[0-9]+$ ]] ||
    { echo "SOURCE_DATE_EPOCH must be a non-negative integer." >&2; exit 64; }

hdiutil create \
    -volname "$DISPLAY_NAME" \
    -srcfolder "$staging" \
    -fs HFS+ \
    -format UDZO \
    -imagekey zlib-level=9 \
    -ov \
    "$dmg_path"

if [[ -n "$identity" ]]; then
    codesign --force --timestamp --sign "$identity" "$dmg_path"
    codesign --verify --verbose=2 "$dmg_path"
fi

(
    cd "$output_dir"
    shasum -a 256 "$base.dmg" | sort >"$base.dmg.sha256"
)

echo "Built $dmg_path"
