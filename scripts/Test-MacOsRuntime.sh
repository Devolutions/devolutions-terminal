#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
metadata="$repo_root/macos/package.env"
package_dir="${1:-$repo_root/artifacts/packages}"

[[ "$(uname -s)" == "Darwin" ]] ||
    { echo "Native macOS runtime validation requires Darwin." >&2; exit 78; }
case "$(uname -m)" in
    arm64) rid="osx-arm64" ;;
    x86_64) rid="osx-x64" ;;
    *)
        echo "Unsupported macOS architecture: $(uname -m)" >&2
        exit 78
        ;;
esac
[[ -d "$package_dir" ]] ||
    { echo "macOS package directory not found: $package_dir" >&2; exit 66; }
package_dir="$(cd -- "$package_dir" && pwd)"

# shellcheck source=../macos/package.env
source "$metadata"
for command in ditto dotnet find; do
    command -v "$command" >/dev/null 2>&1 ||
        { echo "$command is required for native macOS runtime validation." >&2; exit 69; }
done

work="${WT_MACOS_TEST_ROOT:-$repo_root/artifacts/macos-runtime-$$}"
rm -rf -- "$work"
mkdir -p "$work/home"
trap 'rm -rf -- "$work"' EXIT
export HOME="$work/home"
unset WT_BASE_SETTINGS_PATH DTERM_SETTINGS_PATH WT_DOTNET_SETTINGS_PATH

one_artifact() {
    local pattern="$1"
    local matches=()
    while IFS= read -r path; do
        matches+=("$path")
    done < <(find "$package_dir" -maxdepth 1 -name "$pattern" -print | sort)
    if ((${#matches[@]} != 1)); then
        echo "Expected exactly one $pattern in $package_dir; found ${#matches[@]}." >&2
        exit 66
    fi
    printf '%s\n' "${matches[0]}"
}

zip_package="$(one_artifact "*-$rid.zip")"
bash "$script_dir/Test-MacOsPackage.sh" "$rid" "$zip_package"

extract="$work/extracted"
mkdir -p "$extract"
ditto -x -k "$zip_package" "$extract"
app="$(find "$extract" -maxdepth 2 -name '*.app' -print -quit)"
[[ -n "$app" ]] ||
    { echo "Extracted zip has no app bundle." >&2; exit 70; }
macos_dir="$app/Contents/MacOS"

smoke_dt() {
    local label="$1"
    "$macos_dir/$CLI_NAME" --help | grep -F "dt - Devolutions Terminal" >/dev/null
    local error="$work/parser-error"
    if "$macos_dir/$CLI_NAME" --macos-invalid-option >"$error" 2>&1; then
        echo "$label dt accepted an invalid parser option." >&2
        exit 1
    else
        local status=$?
        [[ "$status" == 2 ]] ||
            { echo "$label dt returned $status instead of 2 for invalid input." >&2; exit 1; }
    fi
    grep -F "Unknown command '--macos-invalid-option'." "$error" >/dev/null

    local helper_error="$work/pty-helper-error"
    if "$macos_dir/$PTY_HOST_NAME" >"$helper_error" 2>&1; then
        echo "$label dt-pty-host accepted missing launch arguments." >&2
        exit 1
    else
        local helper_status=$?
        [[ "$helper_status" == 64 ]] ||
            { echo "$label dt-pty-host returned $helper_status instead of 64 for missing arguments." >&2; exit 1; }
    fi
    grep -F "usage: dt-pty-host" "$helper_error" >/dev/null
    echo "NativeAOT dt startup and parser passed for $label."
}

smoke_dt "$(basename "$zip_package")"

run_tests() {
    local project="$1"
    local filter="$2"
    dotnet test "$repo_root/tests/$project/$project.csproj" \
        -c Release --nologo --verbosity minimal --filter "$filter"
}

run_packaged_native_tests() {
    local project="$1"
    local native_name="$2"
    local packaged_native="$3"
    local filter="$4"
    local project_file="$repo_root/tests/$project/$project.csproj"
    dotnet build "$project_file" -c Release --nologo --verbosity minimal
    local assembly
    assembly="$(find "$repo_root/tests/$project/bin/Release" -type f \
        -name "$project.dll" -print -quit)"
    [[ -n "$assembly" ]] ||
        { echo "Could not locate the $project test output." >&2; exit 70; }
    if [[ -x "$packaged_native" ]]; then
        install -m 0755 "$packaged_native" "$(dirname "$assembly")/$native_name"
    else
        install -m 0644 "$packaged_native" "$(dirname "$assembly")/$native_name"
    fi
    dotnet test "$project_file" -c Release --no-build --no-restore \
        --nologo --verbosity minimal --filter "$filter"
}

run_tests Devolutions.Terminal.Cli.Tests \
    'FullyQualifiedName~Devolutions.Terminal.Cli.Tests.CliParserTests'
run_tests Devolutions.Terminal.Core.Tests \
    'FullyQualifiedName~Devolutions.Terminal.Core.Tests.VtParserTests'
run_packaged_native_tests Devolutions.Terminal.Ghostty.Tests libghostty-vt.dylib \
    "$macos_dir/$GHOSTTY_LIBRARY" \
    'FullyQualifiedName~Devolutions.Terminal.Ghostty.Tests.GhosttyTerminalEngineTests'
run_packaged_native_tests Devolutions.Terminal.Connection.Tests dt-pty-host \
    "$macos_dir/$PTY_HOST_NAME" \
    'FullyQualifiedName~Devolutions.Terminal.Connection.Tests.LinuxPtyConnectionTests'
run_tests Devolutions.Terminal.Broker.Tests \
    'FullyQualifiedName~Devolutions.Terminal.Broker.Tests.BrokerTests.ConcurrentClientsAreServedBySinglePrimary'
run_tests Devolutions.Terminal.Settings.Tests \
    'FullyQualifiedName~Devolutions.Terminal.Settings.Tests.DynamicProfileGeneratorTests.MacOsShellsUseMacOsSourceAndZsh|FullyQualifiedName~Devolutions.Terminal.Settings.Tests.LinuxRuntimeEnvironmentTests'

echo "Native macOS non-UI runtime validation passed on $(uname -m)."
