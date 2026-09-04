#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
metadata="$repo_root/macos/package.env"

if (($# != 4)); then
    echo "Usage: $0 <publish-directory> <app-path> <version> <osx-arm64|osx-x64>" >&2
    exit 64
fi

publish_dir="$(cd -- "$1" && pwd)"
app_path="$2"
version="$3"
rid="$4"

# shellcheck source=../macos/package.env
source "$metadata"

[[ "$version" =~ ^[0-9][0-9A-Za-z.+~_-]*$ ]] ||
    { echo "Invalid macOS package version: $version" >&2; exit 64; }
case "$rid" in
    osx-arm64|osx-x64) ;;
    *) echo "Unsupported macOS RID: $rid" >&2; exit 64 ;;
esac
command -v python3 >/dev/null 2>&1 ||
    { echo "python3 is required to stage the macOS app bundle." >&2; exit 69; }
command -v sips >/dev/null 2>&1 ||
    { echo "sips is required to generate the macOS app icon." >&2; exit 69; }
command -v iconutil >/dev/null 2>&1 ||
    { echo "iconutil is required to generate the macOS app icon." >&2; exit 69; }
command -v plutil >/dev/null 2>&1 ||
    { echo "plutil is required to validate Info.plist." >&2; exit 69; }

required=(
    "$EXECUTABLE_NAME"
    "$CLI_NAME"
    "$PTY_HOST_NAME"
    "$GHOSTTY_LIBRARY"
    libSkiaSharp.dylib
    libHarfBuzzSharp.dylib
    THIRD-PARTY-NOTICES-GHOSTTY.txt
    THIRD-PARTY-NOTICES-NOTO-EMOJI.txt
)
for path in "${required[@]}"; do
    [[ -f "$publish_dir/$path" ]] ||
        { echo "NativeAOT publish output is missing $path for $rid." >&2; exit 70; }
done

rm -rf -- "$app_path"
contents="$app_path/Contents"
macos_dir="$contents/MacOS"
resources="$contents/Resources"
install -d "$macos_dir" "$resources"
cp -a "$publish_dir/." "$macos_dir/"
find "$macos_dir" -name '*.pdb' -delete
find "$macos_dir" -name '*.dbg' -delete
find "$macos_dir" -name '*.dSYM' -prune -exec rm -rf {} +

python3 - "$repo_root/macos/Info.plist" "$contents/Info.plist" "$version" <<'PY'
import pathlib
import sys

source, destination, version = sys.argv[1:]
text = pathlib.Path(source).read_text(encoding="utf-8")
text = text.replace(
    "<string>0.1.0</string>",
    f"<string>{version}</string>",
    2,
)
pathlib.Path(destination).write_text(text, encoding="utf-8")
PY
plutil -lint "$contents/Info.plist" >/dev/null

icon_work="$(mktemp -d "${TMPDIR:-/tmp}/devolutions-terminal-icon.XXXXXX")"
iconset="$icon_work/DevolutionsTerminal.iconset"
mkdir -p "$iconset"
icons="$repo_root/linux/icons"
sips -z 16 16 "$icons/$APP_ID-16.png" --out "$iconset/icon_16x16.png" >/dev/null
sips -z 32 32 "$icons/$APP_ID-32.png" --out "$iconset/icon_16x16@2x.png" >/dev/null
sips -z 32 32 "$icons/$APP_ID-32.png" --out "$iconset/icon_32x32.png" >/dev/null
sips -z 64 64 "$icons/$APP_ID-64.png" --out "$iconset/icon_32x32@2x.png" >/dev/null
sips -z 128 128 "$icons/$APP_ID-256.png" --out "$iconset/icon_128x128.png" >/dev/null
sips -z 256 256 "$icons/$APP_ID-256.png" --out "$iconset/icon_128x128@2x.png" >/dev/null
sips -z 256 256 "$icons/$APP_ID-256.png" --out "$iconset/icon_256x256.png" >/dev/null
sips -z 512 512 "$icons/$APP_ID-256.png" --out "$iconset/icon_256x256@2x.png" >/dev/null
sips -z 512 512 "$icons/$APP_ID-256.png" --out "$iconset/icon_512x512.png" >/dev/null
sips -z 1024 1024 "$icons/$APP_ID-256.png" --out "$iconset/icon_512x512@2x.png" >/dev/null
iconutil -c icns "$iconset" -o "$resources/$ICON_NAME.icns"
rm -rf -- "$icon_work"

install -m 0644 "$repo_root/LICENSE" "$resources/LICENSE"
install -m 0644 "$macos_dir/THIRD-PARTY-NOTICES-GHOSTTY.txt" \
    "$resources/THIRD-PARTY-NOTICES-GHOSTTY.txt"
install -m 0644 "$macos_dir/THIRD-PARTY-NOTICES-NOTO-EMOJI.txt" \
    "$resources/THIRD-PARTY-NOTICES-NOTO-EMOJI.txt"

chmod 0755 "$macos_dir/$EXECUTABLE_NAME" "$macos_dir/$CLI_NAME" "$macos_dir/$PTY_HOST_NAME"

source_date_epoch="${SOURCE_DATE_EPOCH:-}"
if [[ -z "$source_date_epoch" ]]; then
    source_date_epoch="$(git -C "$repo_root" log -1 --format=%ct 2>/dev/null || python3 -c 'import time; print(int(time.time()))')"
fi
[[ "$source_date_epoch" =~ ^[0-9]+$ ]] ||
    { echo "SOURCE_DATE_EPOCH must be a non-negative integer." >&2; exit 64; }
export SOURCE_DATE_EPOCH="$source_date_epoch"
python3 - "$app_path" "$source_date_epoch" <<'PY'
import os
import sys
from pathlib import Path

root = Path(sys.argv[1])
epoch = int(sys.argv[2])
for path in sorted(root.rglob("*"), key=lambda item: len(item.parts), reverse=True):
    os.utime(path, (epoch, epoch), follow_symlinks=False)
os.utime(root, (epoch, epoch), follow_symlinks=False)
PY
