#!/usr/bin/env pwsh
#Requires -Version 7
<#
    .SYNOPSIS
    Builds a distributable .dmg for a (signed or ad-hoc-signed) Devolutions
    Terminal .app bundle: a compressed UDZO disk image containing the app
    and an /Applications symlink for drag-to-install. If a signing identity
    is supplied, the disk image itself is codesigned as well.

    .PARAMETER AppPath
    Path to the .app bundle to package.

    .PARAMETER Version
    The package version (e.g. 0.1.0).

    .PARAMETER Rid
    osx-arm64 or osx-x64.

    .PARAMETER OutputDir
    Directory to write the .dmg and SHA-256 manifest to.

    .PARAMETER Identity
    Optional codesign signing identity to sign the disk image with.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$AppPath,

    [Parameter(Mandatory, Position = 1)]
    [string]$Version,

    [Parameter(Mandatory, Position = 2)]
    [ValidateSet('osx-arm64', 'osx-x64')]
    [string]$Rid,

    [Parameter(Mandatory, Position = 3)]
    [string]$OutputDir,

    [Parameter(Position = 4)]
    [string]$Identity
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:LC_ALL = 'C'

$scriptDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $scriptDir
$metadataPath = Join-Path $repoRoot 'macos/package.env'

Import-Module (Join-Path $scriptDir 'MacOsPackagingCommon.psm1') -Force

$metadata = Import-MacOsPackageEnv -Path $metadataPath

Assert-Darwin -Message 'macOS disk images must be built on Darwin.'
if (-not (Test-Path -LiteralPath $AppPath -PathType Container)) {
    throw "App bundle not found: $AppPath"
}
Assert-MacOsVersion -Version $Version
Assert-Command -Name 'hdiutil', 'shasum'

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path -LiteralPath $OutputDir).ProviderPath
$base = "$($metadata.PACKAGE_NAME)-$Version-$Rid"
$dmgPath = Join-Path $OutputDir "$base.dmg"
if (Test-Path -LiteralPath $dmgPath) {
    Remove-Item -LiteralPath $dmgPath -Force
}

$work = Join-Path $repoRoot "artifacts/macos-dmg-staging/$Rid-$PID"
$staging = Join-Path $work 'staging'
if (Test-Path -LiteralPath $work) {
    Remove-Item -LiteralPath $work -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $staging | Out-Null

try {
    Invoke-Native -FilePath cp -ArgumentList '-a', $AppPath, (Join-Path $staging $metadata.BUNDLE_NAME)
    Invoke-Native -FilePath ln -ArgumentList '-s', '/Applications', (Join-Path $staging 'Applications')

    # Computed for parity with the other reproducible-build gates; hdiutil
    # itself does not consume SOURCE_DATE_EPOCH.
    Get-MacOsSourceDateEpoch -RepoRoot $repoRoot | Out-Null

    Invoke-Native -FilePath hdiutil -ArgumentList @(
        'create',
        '-volname', $metadata.DISPLAY_NAME,
        '-srcfolder', $staging,
        '-fs', 'HFS+',
        '-format', 'UDZO',
        '-imagekey', 'zlib-level=9',
        '-ov',
        $dmgPath
    )

    if ($Identity) {
        Invoke-Native -FilePath codesign -ArgumentList '--force', '--timestamp', '--sign', $Identity, $dmgPath
        Invoke-Native -FilePath codesign -ArgumentList '--verify', '--verbose=2', $dmgPath
    }

    $manifest = Get-Sha256Manifest -WorkingDirectory $OutputDir -Path @("$base.dmg")
    Set-Content -LiteralPath (Join-Path $OutputDir "$base.dmg.sha256") -Value $manifest -Encoding utf8

    Write-Host "Built $dmgPath"
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
