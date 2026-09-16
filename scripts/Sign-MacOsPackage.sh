#!/usr/bin/env bash
# Codesigns a Devolutions Terminal .app bundle with a real Developer ID
# identity and the Hardened Runtime. Every embedded Mach-O binary is signed
# individually (innermost first) before the bundle itself is signed, which is
# Apple's recommended alternative to the deprecated `codesign --deep` flag.
set -euo pipefail
export LC_ALL=C

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
entitlements="${MACOS_ENTITLEMENTS:-$repo_root/macos/entitlements.plist}"

if (($# != 2)); then
    echo "Usage: $0 <app-path> <signing-identity>" >&2
    exit 64
fi
app_path="$1"
identity="$2"

[[ "$(uname -s)" == "Darwin" ]] ||
    { echo "Codesigning macOS packages requires Darwin." >&2; exit 78; }
[[ -d "$app_path" ]] ||
    { echo "App bundle not found: $app_path" >&2; exit 66; }
[[ -f "$entitlements" ]] ||
    { echo "Entitlements file not found: $entitlements" >&2; exit 66; }
for command in codesign file; do
    command -v "$command" >/dev/null 2>&1 ||
        { echo "$command is required to codesign macOS packages." >&2; exit 69; }
done

is_macho() {
    file -b "$1" 2>/dev/null | grep -qE 'Mach-O'
}

# Sign nested binaries deepest-first so the outer app signature is computed
# last, over already-signed contents.
while IFS= read -r -d '' binary; do
    is_macho "$binary" || continue
    codesign --force --options runtime --timestamp \
        --sign "$identity" "$binary"
done < <(find "$app_path/Contents" -type f -print0 | sort -z -r)

codesign --force --options runtime --timestamp \
    --entitlements "$entitlements" \
    --sign "$identity" "$app_path"

codesign --verify --deep --strict --verbose=2 "$app_path"
echo "Signed $app_path with identity: $identity"
