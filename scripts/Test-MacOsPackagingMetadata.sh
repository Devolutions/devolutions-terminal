#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
metadata="$repo_root/macos/package.env"
plist="$repo_root/macos/Info.plist"

for script in \
    "$script_dir/Build-MacOsPackage.sh" \
    "$script_dir/Stage-MacOsApp.sh" \
    "$script_dir/Test-MacOsPackage.sh" \
    "$script_dir/Test-MacOsRuntime.sh"; do
    bash -n "$script"
done

# shellcheck source=../macos/package.env
source "$metadata"
test "$PACKAGE_NAME" = devolutions-terminal
test "$APP_ID" = com.devolutions.Terminal
test "$EXECUTABLE_NAME" = Devolutions.Terminal
test "$CLI_NAME" = dt
test "$PTY_HOST_NAME" = dt-pty-host
test "$GHOSTTY_LIBRARY" = libghostty-vt.dylib
test "$ICON_NAME" = DevolutionsTerminal
test "$MACOS_DEPLOYMENT_TARGET" = 13.0
test "$LICENSE_ID" = "MIT AND OFL-1.1"
test "$SBOM_LICENSE_ID" = "MIT AND OFL-1.1"
test "$LICENSE_ID" = "$SBOM_LICENSE_ID"
test "$URL_SCHEME" = dterm
test "$BUNDLE_NAME" = "Devolutions Terminal.app"
if grep -Eq '(^|_)VERSION=' "$metadata"; then
    echo "macOS package metadata must not duplicate the release version." >&2
    exit 1
fi

python3 - "$plist" "$APP_ID" "$EXECUTABLE_NAME" "$MACOS_DEPLOYMENT_TARGET" "$URL_SCHEME" <<'PY'
import pathlib
import sys
import xml.etree.ElementTree as ET

path, app_id, executable, minimum, scheme = sys.argv[1:]
root = ET.parse(path).getroot().find("dict")
children = list(root)
keys = {}
index = 0
while index < len(children):
    node = children[index]
    if node.tag != "key":
        index += 1
        continue
    value = children[index + 1]
    if value.tag == "string":
        keys[node.text] = value.text
    elif value.tag == "true":
        keys[node.text] = True
    index += 2

assert keys["CFBundleIdentifier"] == app_id
assert keys["CFBundleExecutable"] == executable
assert keys["CFBundlePackageType"] == "APPL"
assert keys["LSMinimumSystemVersion"] == minimum
assert keys["CFBundleIconFile"] == "DevolutionsTerminal"
assert keys["NSHighResolutionCapable"] is True
text = pathlib.Path(path).read_text(encoding="utf-8")
assert f"<string>{scheme}</string>" in text
PY

if "$script_dir/Build-MacOsPackage.sh" invalid-rid >/dev/null 2>&1; then
    echo "Builder accepted an invalid RID." >&2
    exit 1
fi
echo "macOS packaging scripts and canonical metadata validation passed."
