[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string] $PackageDirectory,

    [ValidatePattern("^\d+\.\d+\.\d+$")]
    [string] $Version = "2026.3.0"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$packageSource = [IO.Path]::GetFullPath($PackageDirectory)
$expectedPackageNames = @(
    "Devolutions.Terminal.App.$Version.nupkg"
    "Devolutions.Terminal.App.any.$Version.nupkg"
    "Devolutions.Terminal.App.win-x64.$Version.nupkg"
    "Devolutions.Terminal.App.win-arm64.$Version.nupkg"
    "Devolutions.Terminal.App.linux-x64.$Version.nupkg"
    "Devolutions.Terminal.App.linux-arm64.$Version.nupkg"
    "Devolutions.Terminal.App.osx-x64.$Version.nupkg"
    "Devolutions.Terminal.App.osx-arm64.$Version.nupkg"
)
foreach ($packageName in $expectedPackageNames) {
    $packagePath = Join-Path $packageSource $packageName
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        throw "Expected NuGet package '$packagePath' was not found."
    }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("devolutions-terminal-nuget-" + [guid]::NewGuid())
$nuGetConfigPath = Join-Path $testRoot "NuGet.Config"
$toolDirectory = Join-Path $testRoot "tools"
$executableName = if ($IsWindows) { "dt.exe" } else { "dt" }
$toolPath = Join-Path $toolDirectory $executableName
$originalNugetPackages = $env:NUGET_PACKAGES

try {
    New-Item -ItemType Directory -Force -Path $testRoot, $toolDirectory | Out-Null
    $env:NUGET_PACKAGES = Join-Path $testRoot "packages"
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$packageSource" />
  </packageSources>
</configuration>
"@ | Set-Content -LiteralPath $nuGetConfigPath -Encoding utf8NoBOM

    & dotnet tool install Devolutions.Terminal.App `
        --version $Version `
        --tool-path $toolDirectory `
        --configfile $nuGetConfigPath `
        --no-cache
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet tool install failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path -LiteralPath $toolPath -PathType Leaf)) {
        throw "Installed dt command '$toolPath' was not found."
    }

    & $toolPath --version
    if ($LASTEXITCODE -ne 0) {
        throw "Installed dt command failed with exit code $LASTEXITCODE."
    }
}
finally {
    $env:NUGET_PACKAGES = $originalNugetPackages
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
