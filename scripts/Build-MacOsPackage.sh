#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
project="$repo_root/src/Devolutions.Terminal/Devolutions.Terminal.csproj"
metadata="$repo_root/macos/package.env"

rid="${1:-osx-arm64}"
version="${2:-0.1.0}"
output_dir="${3:-$repo_root/artifacts/packages}"

# shellcheck source=../macos/package.env
source "$metadata"
case "$rid" in
    osx-arm64) expected_arch="arm64" ;;
    osx-x64) expected_arch="x86_64" ;;
    *)
        echo "Unsupported macOS RID: $rid" >&2
        exit 64
        ;;
esac
[[ "$version" =~ ^[0-9][0-9A-Za-z.+~_-]*$ ]] ||
    { echo "Invalid macOS package version: $version" >&2; exit 64; }
[[ "$(uname -s)" == "Darwin" ]] ||
    { echo "macOS packages must be built on Darwin." >&2; exit 78; }
for command in file lipo python3 codesign ditto shasum; do
    command -v "$command" >/dev/null 2>&1 ||
        { echo "$command is required to build macOS packages." >&2; exit 69; }
done

source_date_epoch="${SOURCE_DATE_EPOCH:-}"
if [[ -z "$source_date_epoch" ]]; then
    source_date_epoch="$(git -C "$repo_root" log -1 --format=%ct 2>/dev/null || python3 -c 'import time; print(int(time.time()))')"
fi
[[ "$source_date_epoch" =~ ^[0-9]+$ ]] ||
    { echo "SOURCE_DATE_EPOCH must be a non-negative integer." >&2; exit 64; }
export SOURCE_DATE_EPOCH="$source_date_epoch"

work="$repo_root/artifacts/macos-package-staging/$rid-$$"
publish_dir="$work/publish"
app_path="$work/$BUNDLE_NAME"
rm -rf -- "$work"
mkdir -p "$publish_dir"
trap 'rm -rf -- "$work"' EXIT

if [[ -n "${MACOS_PUBLISH_DIR:-}" ]]; then
    [[ -d "$MACOS_PUBLISH_DIR" ]] ||
        { echo "MACOS_PUBLISH_DIR does not exist: $MACOS_PUBLISH_DIR" >&2; exit 66; }
    cp -a "$MACOS_PUBLISH_DIR/." "$publish_dir/"
else
    command -v dotnet >/dev/null 2>&1 ||
        { echo "dotnet is required to publish the application; install the SDK selected by global.json or set MACOS_PUBLISH_DIR." >&2; exit 69; }
    dotnet publish "$project" \
        -c Release \
        -r "$rid" \
        --self-contained true \
        -o "$publish_dir" \
        -p:DebugSymbols=false \
        -p:DebugType=None \
        -p:NativeDebugSymbols=false \
        --verbosity minimal
fi

find "$publish_dir" -type f \( -name '*.dbg' -o -name '*.pdb' \) -delete
for artifact in "$EXECUTABLE_NAME" "$CLI_NAME" "$PTY_HOST_NAME" "$GHOSTTY_LIBRARY" \
    libSkiaSharp.dylib libHarfBuzzSharp.dylib; do
    [[ -f "$publish_dir/$artifact" ]] ||
        { echo "Publish output is missing $artifact." >&2; exit 70; }
    archs="$(lipo -archs "$publish_dir/$artifact" 2>/dev/null || true)"
    [[ "$archs" == *"$expected_arch"* ]] ||
        { echo "$artifact has the wrong architecture for $rid (lipo: ${archs:-unknown})." >&2; exit 70; }
done

bash "$script_dir/Stage-MacOsApp.sh" "$publish_dir" "$app_path" "$version" "$rid"
codesign --force --deep --sign - "$app_path"

mkdir -p "$output_dir"
output_dir="$(cd -- "$output_dir" && pwd)"
base="$PACKAGE_NAME-$version-$rid"
app_output="$output_dir/$BUNDLE_NAME"
rm -rf -- "$app_output"
cp -a "$app_path" "$app_output"

archive="$output_dir/$base.zip"
rm -f -- "$archive"
ditto -c -k --keepParent --norsrc --noextattr --noacl "$app_output" "$archive"
(
    cd "$output_dir"
    shasum -a 256 "$base.zip" "$BUNDLE_NAME/Contents/MacOS/$EXECUTABLE_NAME" \
        "$BUNDLE_NAME/Contents/MacOS/$CLI_NAME" \
        "$BUNDLE_NAME/Contents/MacOS/$PTY_HOST_NAME" \
        "$BUNDLE_NAME/Contents/MacOS/$GHOSTTY_LIBRARY" |
        sort >"$base.sha256"
)

echo "Built $archive"
