#!/usr/bin/env pwsh
#Requires -Version 7
<#
    .SYNOPSIS
    Codesigns a Devolutions Terminal .app bundle with a real Developer ID
    identity and the Hardened Runtime. Every embedded Mach-O binary except
    the bundle's main executable is signed individually (innermost first)
    before the bundle itself is signed, which is Apple's recommended
    alternative to the deprecated `codesign --deep` flag. The main
    executable must NOT be signed as a standalone file: doing so makes
    codesign operate in bundle context and demand that every sibling file
    in Contents/MacOS (e.g. dt.runtimeconfig.json) already carry a code
    signature, failing with "code object is not signed at all / In
    subcomponent: ...". Signing the bundle at the end signs the main
    executable and applies the entitlements to it.

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

# Sign nested binaries deepest-first so the outer app signature is computed
# last, over already-signed contents. Bytewise-reverse-sorted paths (like
# `sort -z -r` under LC_ALL=C) reliably put deeper, longer paths first.
$paths = Get-ChildItem -LiteralPath $contents -Recurse -File | ForEach-Object { $_.FullName }
$paths = [string[]]$paths
[Array]::Sort($paths, [StringComparer]::Ordinal)
[Array]::Reverse($paths)
foreach ($path in $paths) {
    if ($path -eq $mainExecutable) {
        continue
    }
    if (-not (Test-MachO -Path $path)) {
        continue
    }
    Invoke-Native -FilePath codesign -ArgumentList @(
        '--force', '--options', 'runtime', '--timestamp', '--sign', $Identity, $path
    )
}

Invoke-Native -FilePath codesign -ArgumentList @(
    '--force', '--options', 'runtime', '--timestamp',
    '--entitlements', $entitlements,
    '--sign', $Identity, $AppPath
)

Invoke-Native -FilePath codesign -ArgumentList '--verify', '--deep', '--strict', '--verbose=2', $AppPath
Write-Host "Signed $AppPath with identity: $Identity"
