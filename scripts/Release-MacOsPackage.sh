#!/usr/bin/env bash
# Turns an ad-hoc-signed macOS package (produced by Build-MacOsPackage.sh)
# into the final release artifacts: a zip and a .dmg, both codesigned with a
# real Developer ID identity and notarized/stapled when an identity is given.
# Without an identity, it simply repackages the zip and adds a (necessarily
# unsigned/unnotarized) .dmg, for dry runs or forked-PR builds without access
# to signing secrets.
set -euo pipefail
export LC_ALL=C

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
metadata="$repo_root/macos/package.env"

if (($# < 4)); then
    echo "Usage: $0 <osx-arm64|osx-x64> <version> <input-dir> <output-dir> [signing-identity]" >&2
    exit 64
fi
rid="$1"
version="$2"
input_dir="$3"
output_dir="$4"
identity="${5:-}"

# shellcheck source=../macos/package.env
source "$metadata"

case "$rid" in
    osx-arm64|osx-x64) ;;
    *) echo "Unsupported macOS RID: $rid" >&2; exit 64 ;;
esac
[[ "$(uname -s)" == "Darwin" ]] ||
    { echo "macOS release packaging must run on Darwin." >&2; exit 78; }
[[ -d "$input_dir" ]] ||
    { echo "Input directory not found: $input_dir" >&2; exit 66; }
for command in ditto shasum; do
    command -v "$command" >/dev/null 2>&1 ||
        { echo "$command is required to package macOS releases." >&2; exit 69; }
done

base="$PACKAGE_NAME-$version-$rid"
input_zip="$input_dir/$base.zip"
[[ -f "$input_zip" ]] ||
    { echo "Unsigned package not found: $input_zip" >&2; exit 66; }

work="$repo_root/artifacts/macos-release-staging/$rid-$$"
rm -rf -- "$work"
mkdir -p "$work"
trap 'rm -rf -- "$work"' EXIT

ditto -x -k "$input_zip" "$work"
app_path="$(find "$work" -maxdepth 1 -name '*.app' -print -quit)"
[[ -n "$app_path" ]] ||
    { echo "$input_zip does not contain an app bundle." >&2; exit 70; }

if [[ -n "$identity" ]]; then
    bash "$script_dir/Sign-MacOsPackage.sh" "$app_path" "$identity"
    bash "$script_dir/Notarize-MacOsPackage.sh" "$app_path"
fi

mkdir -p "$output_dir"
output_dir="$(cd -- "$output_dir" && pwd)"

app_output="$output_dir/$BUNDLE_NAME"
rm -rf -- "$app_output"
cp -a "$app_path" "$app_output"

archive="$output_dir/$base.zip"
rm -f -- "$archive"
ditto -c -k --keepParent --norsrc --noextattr --noacl "$app_output" "$archive"

bash "$script_dir/Build-MacOsDmg.sh" "$app_output" "$version" "$rid" "$output_dir" "$identity"
if [[ -n "$identity" ]]; then
    bash "$script_dir/Notarize-MacOsPackage.sh" "$output_dir/$base.dmg"
fi

(
    cd "$output_dir"
    shasum -a 256 "$base.zip" "$base.dmg" "$BUNDLE_NAME/Contents/MacOS/$EXECUTABLE_NAME" \
        "$BUNDLE_NAME/Contents/MacOS/$CLI_NAME" \
        "$BUNDLE_NAME/Contents/MacOS/$PTY_HOST_NAME" \
        "$BUNDLE_NAME/Contents/MacOS/$GHOSTTY_LIBRARY" |
        sort >"$base.sha256"
    rm -f -- "$base.dmg.sha256"
)

if [[ -n "$identity" ]]; then
    echo "Released signed and notarized $archive and $output_dir/$base.dmg"
else
    echo "Released unsigned $archive and $output_dir/$base.dmg (no signing identity provided)"
fi
