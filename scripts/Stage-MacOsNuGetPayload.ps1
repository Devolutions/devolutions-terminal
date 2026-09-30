#!/usr/bin/env pwsh
#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidateSet('osx-arm64', 'osx-x64')][string]$Rid,
    [Parameter(Mandatory)][string]$Identity
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'MacOsPackagingCommon.psm1') -Force
Assert-Darwin
Assert-Command -Name 'codesign', 'cp', 'chmod'
$repoRoot = Split-Path -Parent $PSScriptRoot
$metadata = Import-MacOsPackageEnv -Path (Join-Path $repoRoot 'macos/package.env')
$entitlements = if ($env:MACOS_ENTITLEMENTS) { $env:MACOS_ENTITLEMENTS } else {
    Join-Path $repoRoot 'macos/entitlements.plist'
}
$requirements = @{ RequireHardenedRuntime = $true }
if ($Identity -ne '-') {
    $requirements.TeamIdentifier = $metadata.APPLE_TEAM_ID
    $requirements.RequireTimestamp = $true
    Assert-MacOsCodeSignature -Path $AppPath @requirements
}
Invoke-Native -FilePath codesign -ArgumentList '--verify', '--deep', '--strict', $AppPath
if (Test-Path -LiteralPath $OutputDirectory) {
    throw "NuGet payload output directory already exists: $OutputDirectory"
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

# NuGet's public contract is a flat native layout, not an .app. Bundle-level
# Info.plist/resource seals cannot follow an executable out of its bundle.
$binaries = @(
    $metadata.EXECUTABLE_NAME, $metadata.CLI_NAME, $metadata.PTY_HOST_NAME,
    $metadata.GHOSTTY_LIBRARY, 'libAvaloniaNative.dylib', 'libSkiaSharp.dylib', 'libHarfBuzzSharp.dylib'
)
foreach ($name in $binaries) {
    $source = Join-Path $AppPath "Contents/MacOS/$name"
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "App bundle is missing $name."
    }
    $destination = Join-Path $OutputDirectory $name
    Invoke-Native -FilePath cp -ArgumentList '-p', $source, $destination
    $arguments = @('--force', '--options', 'runtime')
    if ($Identity -ne '-') { $arguments += '--timestamp' }
    if ($name -in @($metadata.EXECUTABLE_NAME, $metadata.CLI_NAME)) {
        $arguments += @('--entitlements', $entitlements)
    }
    $arguments += @('--sign', $Identity, $destination)
    Invoke-Native -FilePath codesign -ArgumentList $arguments
    Assert-MacOsCodeSignature -Path $destination @requirements
}
$resources = Join-Path $AppPath 'Contents/Resources'
Copy-Item -LiteralPath (Join-Path $resources 'LICENSE') -Destination $OutputDirectory
Get-ChildItem -LiteralPath $resources -File -Filter 'THIRD-PARTY-NOTICES*.txt' |
    Copy-Item -Destination $OutputDirectory
Invoke-Native -FilePath chmod -ArgumentList @(
    '0755', (Join-Path $OutputDirectory $metadata.EXECUTABLE_NAME),
    (Join-Path $OutputDirectory $metadata.CLI_NAME), (Join-Path $OutputDirectory $metadata.PTY_HOST_NAME)
)
$testArguments = @{ PayloadDirectory = $OutputDirectory; Rid = $Rid }
if ($Identity -ne '-') { $testArguments.RequireDeveloperId = $true }
& (Join-Path $PSScriptRoot 'Test-MacOsNuGetPayload.ps1') @testArguments
