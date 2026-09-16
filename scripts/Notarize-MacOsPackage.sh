#!/usr/bin/env bash
# Submits a macOS .app bundle or .dmg to Apple's notary service and staples
# the resulting ticket. Authentication uses an App Store Connect API key
# (no Apple ID password or 2FA prompts), supplied via:
#   APPLE_NOTARIZATION_API_KEY_ID     Key ID (e.g. "2X9R4HXF34")
#   APPLE_NOTARIZATION_API_ISSUER_ID  Issuer ID (UUID)
#   APPLE_NOTARIZATION_API_KEY_P8     Base64-encoded contents of the .p8 key
set -euo pipefail
export LC_ALL=C

if (($# != 1)); then
    echo "Usage: $0 <app-or-dmg-path>" >&2
    exit 64
fi
target="$1"

[[ "$(uname -s)" == "Darwin" ]] ||
    { echo "Notarization requires Darwin." >&2; exit 78; }
[[ -e "$target" ]] ||
    { echo "Path not found: $target" >&2; exit 66; }
for command in xcrun ditto; do
    command -v "$command" >/dev/null 2>&1 ||
        { echo "$command is required to notarize macOS packages." >&2; exit 69; }
done

key_id="${APPLE_NOTARIZATION_API_KEY_ID:?APPLE_NOTARIZATION_API_KEY_ID is required}"
issuer_id="${APPLE_NOTARIZATION_API_ISSUER_ID:?APPLE_NOTARIZATION_API_ISSUER_ID is required}"
key_b64="${APPLE_NOTARIZATION_API_KEY_P8:?APPLE_NOTARIZATION_API_KEY_P8 is required}"

work="$(mktemp -d "${TMPDIR:-/tmp}/devolutions-terminal-notarize.XXXXXX")"
trap 'rm -rf -- "$work"' EXIT
key_path="$work/AuthKey_$key_id.p8"
(umask 077; printf '%s' "$key_b64" | base64 --decode >"$key_path")

submission_path="$target"
case "$target" in
    *.app)
        submission_path="$work/$(basename "${target%.app}")-notarize.zip"
        ditto -c -k --keepParent --norsrc --noextattr --noacl "$target" "$submission_path"
        ;;
esac

xcrun notarytool submit "$submission_path" \
    --key "$key_path" \
    --key-id "$key_id" \
    --issuer "$issuer_id" \
    --wait \
    --timeout 30m

xcrun stapler staple "$target"
xcrun stapler validate "$target"
echo "Notarized and stapled $target"
