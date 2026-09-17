#!/usr/bin/env pwsh
#Requires -Version 7
<#
    .SYNOPSIS
    Builds a distributable .dmg for a (signed or ad-hoc-signed) Devolutions
    Terminal .app bundle: a compressed UDZO disk image with a Finder layout,
    branded background, app icon, and /Applications symlink for drag-to-install.
    If a signing identity is supplied, the disk image itself is codesigned as well.

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
Assert-Command -Name 'hdiutil', 'osascript', 'shasum'

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path -LiteralPath $OutputDir).ProviderPath
$base = "$($metadata.PACKAGE_NAME)-$Version-$Rid"
$dmgPath = Join-Path $OutputDir "$base.dmg"
if (Test-Path -LiteralPath $dmgPath) {
    Remove-Item -LiteralPath $dmgPath -Force
}

$work = Join-Path $repoRoot "artifacts/macos-dmg-staging/$Rid-$PID"
$writableDmgPath = Join-Path $work "$base-rw.dmg"
if (Test-Path -LiteralPath $work) {
    Remove-Item -LiteralPath $work -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $work | Out-Null

try {
    $background = Join-Path $repoRoot 'macos/InstallerBackground.png'
    if (-not (Test-Path -LiteralPath $background -PathType Leaf)) {
        throw "DMG background asset not found: $background"
    }

    # Computed for parity with the other reproducible-build gates; hdiutil
    # itself does not consume SOURCE_DATE_EPOCH.
    Get-MacOsSourceDateEpoch -RepoRoot $repoRoot | Out-Null

    Invoke-Native -FilePath hdiutil -ArgumentList @(
        'create',
        '-size', '32m',
        '-fs', 'HFS+',
        '-type', 'UDIF',
        '-volname', $metadata.DISPLAY_NAME,
        '-ov',
        $writableDmgPath
    )

    $mountPoint = Join-Path $work 'mount'
    New-Item -ItemType Directory -Force -Path $mountPoint | Out-Null
    Invoke-Native -FilePath hdiutil -ArgumentList @(
        'attach',
        '-readwrite',
        '-noverify',
        '-noautoopen',
        '-mountpoint', $mountPoint,
        $writableDmgPath
    )
    try {
        Invoke-Native -FilePath cp -ArgumentList '-a', $AppPath, (Join-Path $mountPoint $metadata.BUNDLE_NAME)
        Invoke-Native -FilePath ln -ArgumentList '-s', '/Applications', (Join-Path $mountPoint 'Applications')
        $backgroundDirectory = Join-Path $mountPoint '.background'
        New-Item -ItemType Directory -Force -Path $backgroundDirectory | Out-Null
        Copy-Item -LiteralPath $background -Destination (Join-Path $backgroundDirectory 'background.png')

        $finderScript = @"
tell application "Finder"
    tell disk "$($metadata.DISPLAY_NAME)"
        open
        set current view of container window to icon view
        set toolbar visible of container window to false
        set statusbar visible of container window to false
        set bounds of container window to {180, 160, 1100, 712}
        set viewOptions to the icon view options of container window
        set arrangement of viewOptions to not arranged
        set icon size of viewOptions to 96
        set background picture of viewOptions to (POSIX file "$backgroundDirectory/background.png" as alias)
        set position of item "$($metadata.BUNDLE_NAME)" to {250, 314}
        set position of item "Applications" to {670, 314}
        close
        open
        update without registering applications
        delay 2
    end tell
end tell
"@
        $finderScript | & osascript
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to configure the Finder layout for the disk image."
        }
    }
    finally {
        Invoke-Native -FilePath hdiutil -ArgumentList 'detach', $mountPoint
    }

    Invoke-Native -FilePath hdiutil -ArgumentList @(
        'convert', $writableDmgPath,
        '-format', 'UDZO',
        '-imagekey', 'zlib-level=9',
        '-ov',
        '-o', $dmgPath
    )

    if ($Identity) {
        Invoke-Native -FilePath codesign -ArgumentList '--force', '--timestamp', '--sign', $Identity, $dmgPath
        Assert-MacOsCodeSignature -Path $dmgPath -TeamIdentifier $metadata.APPLE_TEAM_ID -RequireTimestamp
    }

    $manifest = Get-Sha256Manifest -WorkingDirectory $OutputDir -Path @("$base.dmg")
    Set-Content -LiteralPath (Join-Path $OutputDir "$base.dmg.sha256") -Value $manifest -Encoding utf8

    Write-Host "Built $dmgPath"
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
