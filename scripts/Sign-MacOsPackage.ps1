#!/usr/bin/env pwsh
#Requires -Version 7
<#
    .SYNOPSIS
    Codesigns a Devolutions Terminal .app bundle with a real Developer ID
    identity and the Hardened Runtime. Auxiliary Mach-O binaries are copied
    outside the enclosing app, signed as standalone code, and copied back
    before the bundle itself is signed. This prevents codesign from treating
    an auxiliary executable as the app bundle and requiring data siblings in
    Contents/MacOS (for example dt.runtimeconfig.json) to carry signatures.
    The main executable is signed only through the final bundle-level
    codesign invocation, which also applies its entitlements.

    .PARAMETER AppPath
    Path to the .app bundle to sign.

    .PARAMETER Identity
    The codesign signing identity (e.g. "Developer ID Application: ...").
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$AppPath,

    [Parameter(Mandatory, Position = 1)]
    [string]$Identity
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:LC_ALL = 'C'

$scriptDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $scriptDir
$entitlements = $env:MACOS_ENTITLEMENTS
if (-not $entitlements) {
    $entitlements = Join-Path $repoRoot 'macos/entitlements.plist'
}

Import-Module (Join-Path $scriptDir 'MacOsPackagingCommon.psm1') -Force
$metadata = Import-MacOsPackageEnv -Path (Join-Path $repoRoot 'macos/package.env')

Assert-Darwin -Message 'Codesigning macOS packages requires Darwin.'
if (-not (Test-Path -LiteralPath $AppPath -PathType Container)) {
    throw "App bundle not found: $AppPath"
}
if (-not (Test-Path -LiteralPath $entitlements -PathType Leaf)) {
    throw "Entitlements file not found: $entitlements"
}
Assert-Command -Name 'codesign', 'file', '/usr/libexec/PlistBuddy'

function Test-MachO {
    param([Parameter(Mandatory)][string]$Path)
    $kind = & file -b $Path 2>$null
    return ($kind -match 'Mach-O')
}

# Resolve the bundle's main executable so it can be excluded from per-file
# signing below; signing it standalone makes codesign fail with
# "code object is not signed at all / In subcomponent: ..." because it then
# treats every Contents/MacOS sibling (like dt.runtimeconfig.json) as
# nested code that must already be signed.
$contents = Join-Path $AppPath 'Contents'
$infoPlist = Join-Path $contents 'Info.plist'
$mainExecutableName = & /usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' $infoPlist
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($mainExecutableName)) {
    throw "Unable to read CFBundleExecutable from $infoPlist."
}
$mainExecutable = Join-Path $contents 'MacOS' $mainExecutableName

$timestampArguments = if ($Identity -eq '-') { @() } else { @('--timestamp') }
$signatureRequirements = @{
    RequireHardenedRuntime = $true
}
if ($Identity -ne '-') {
    $signatureRequirements.TeamIdentifier = $metadata.APPLE_TEAM_ID
    $signatureRequirements.RequireTimestamp = $true
}

# codesign resolves any executable under Contents/MacOS back to its enclosing
# app bundle. Sign auxiliary code from an isolated path to force standalone
# Mach-O semantics, then copy the signed bytes back into the bundle.
$signingWork = Join-Path (Split-Path -Parent $AppPath) ".codesign-$PID"
New-Item -ItemType Directory -Force -Path $signingWork | Out-Null

$paths = Get-ChildItem -LiteralPath $contents -Recurse -File | ForEach-Object { $_.FullName }
$paths = [string[]]$paths
[Array]::Sort($paths, [StringComparer]::Ordinal)
[Array]::Reverse($paths)

try {
    foreach ($path in $paths) {
        if ($path -eq $mainExecutable -or -not (Test-MachO -Path $path)) {
            continue
        }

        $isolatedDirectory = Join-Path $signingWork ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $isolatedDirectory | Out-Null
        $isolatedPath = Join-Path $isolatedDirectory (Split-Path -Leaf $path)
        Invoke-Native -FilePath cp -ArgumentList '-p', $path, $isolatedPath
        $codesignArguments = @(
            '--force', '--options', 'runtime'
        ) + $timestampArguments + @(
            '--sign', $Identity, $isolatedPath
        )
        Invoke-Native -FilePath codesign -ArgumentList $codesignArguments
        Assert-MacOsCodeSignature -Path $isolatedPath @signatureRequirements
        Invoke-Native -FilePath cp -ArgumentList '-p', $isolatedPath, $path
    }

    $codesignArguments = @(
        '--force', '--options', 'runtime'
    ) + $timestampArguments + @(
        '--entitlements', $entitlements,
        '--sign', $Identity, $AppPath
    )
    Invoke-Native -FilePath codesign -ArgumentList $codesignArguments

    Invoke-Native -FilePath codesign -ArgumentList '--verify', '--deep', '--strict', '--verbose=2', $AppPath
    Assert-MacOsCodeSignature -Path $AppPath @signatureRequirements
    Write-Host "Signed $AppPath with identity: $Identity"
}
finally {
    Remove-Item -LiteralPath $signingWork -Recurse -Force -ErrorAction SilentlyContinue
}
