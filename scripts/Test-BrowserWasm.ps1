#Requires -Version 7
param(
    [string]$Configuration = "Release",
    [string]$Output = "artifacts/browser-wasm"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

dotnet workload install wasm-tools --skip-manifest-update

$project = Join-Path $repoRoot "src/Devolutions.Terminal.Browser/Devolutions.Terminal.Browser.csproj"
dotnet publish $project -c $Configuration -o $Output
if ($LASTEXITCODE -ne 0) {
    throw "Browser WASM publish failed."
}

$wwwroot = Join-Path $Output "wwwroot"
if (-not (Test-Path $wwwroot)) {
    $wwwroot = Join-Path $repoRoot "src/Devolutions.Terminal.Browser/bin/$Configuration/net10.0-browser/publish/wwwroot"
}

$env:DTERM_BROWSER_E2E = "1"
$env:DTERM_BROWSER_WWWROOT = (Resolve-Path $wwwroot).Path
dotnet test (Join-Path $repoRoot "tests/Devolutions.Terminal.Browser.E2E") -c $Configuration --filter BrowserHostBootsAndRunsShellCommands
if ($LASTEXITCODE -ne 0) {
    throw "Browser WASM E2E tests failed."
}
