#!/usr/bin/env pwsh
#Requires -Version 7
<#
    .SYNOPSIS
    Turns an ad-hoc-signed macOS package (produced by Build-MacOsPackage.ps1)
    into the final release artifacts: a zip and a .dmg, both codesigned with
    a real Developer ID identity and notarized/stapled when an identity is
    given. Without an identity, it simply repackages the zip and adds a
    (necessarily unsigned/unnotarized) .dmg, for dry runs or forked-PR builds
    without access to signing secrets.

    .PARAMETER Rid
    osx-arm64 or osx-x64.

    .PARAMETER Version
    The package version (e.g. 0.1.0).

    .PARAMETER InputDir
    Directory containing the unsigned zip produced by Build-MacOsPackage.ps1.

    .PARAMETER OutputDir
    Directory to write the final .app, zip, .dmg, and SHA-256 manifest to.

    .PARAMETER Identity
    Optional Developer ID codesign identity; when supplied, packages are
    signed and notarized.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('osx-arm64', 'osx-x64')]
    [string]$Rid,

    [Parameter(Mandatory, Position = 1)]
    [string]$Version,

    [Parameter(Mandatory, Position = 2)]
    [string]$InputDir,

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

Assert-Darwin -Message 'macOS release packaging must run on Darwin.'
if (-not (Test-Path -LiteralPath $InputDir -PathType Container)) {
    throw "Input directory not found: $InputDir"
}
Assert-Command -Name 'ditto', 'shasum'

$base = "$($metadata.PACKAGE_NAME)-$Version-$Rid"
$inputZip = Join-Path $InputDir "$base.zip"
if (-not (Test-Path -LiteralPath $inputZip -PathType Leaf)) {
    throw "Unsigned package not found: $inputZip"
}

$work = Join-Path $repoRoot "artifacts/macos-release-staging/$Rid-$PID"
if (Test-Path -LiteralPath $work) {
    Remove-Item -LiteralPath $work -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $work | Out-Null

try {
    Invoke-Native -FilePath ditto -ArgumentList '-x', '-k', $inputZip, $work
    $appPath = Get-ChildItem -LiteralPath $work -Directory -Filter '*.app' |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $appPath) {
        throw "$inputZip does not contain an app bundle."
    }

    if ($Identity) {
        & (Join-Path $scriptDir 'Sign-MacOsPackage.ps1') $appPath $Identity
        if ($LASTEXITCODE -ne 0) {
            throw "Sign-MacOsPackage.ps1 failed with exit code $LASTEXITCODE."
        }
        & (Join-Path $scriptDir 'Notarize-MacOsPackage.ps1') $appPath
        if ($LASTEXITCODE -ne 0) {
            throw "Notarize-MacOsPackage.ps1 failed with exit code $LASTEXITCODE."
        }
    }

    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
    $OutputDir = (Resolve-Path -LiteralPath $OutputDir).ProviderPath

    $appOutput = Join-Path $OutputDir $metadata.BUNDLE_NAME
    if (Test-Path -LiteralPath $appOutput) {
        Remove-Item -LiteralPath $appOutput -Recurse -Force
    }
    Invoke-Native -FilePath cp -ArgumentList '-a', $appPath, $appOutput

    $archive = Join-Path $OutputDir "$base.zip"
    if (Test-Path -LiteralPath $archive) {
        Remove-Item -LiteralPath $archive -Force
    }
    Invoke-Native -FilePath ditto -ArgumentList '-c', '-k', '--keepParent', '--norsrc', '--noextattr', '--noacl', `
        $appOutput, $archive

    $dmgArgs = @($appOutput, $Version, $Rid, $OutputDir)
    if ($Identity) {
        $dmgArgs += $Identity
    }
    & (Join-Path $scriptDir 'Build-MacOsDmg.ps1') @dmgArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Build-MacOsDmg.ps1 failed with exit code $LASTEXITCODE."
    }

    $dmgPath = Join-Path $OutputDir "$base.dmg"
    if ($Identity) {
        & (Join-Path $scriptDir 'Notarize-MacOsPackage.ps1') $dmgPath
        if ($LASTEXITCODE -ne 0) {
            throw "Notarize-MacOsPackage.ps1 failed with exit code $LASTEXITCODE."
        }
    }

    $manifestPaths = @(
        "$base.zip",
        "$base.dmg",
        "$($metadata.BUNDLE_NAME)/Contents/MacOS/$($metadata.EXECUTABLE_NAME)",
        "$($metadata.BUNDLE_NAME)/Contents/MacOS/$($metadata.CLI_NAME)",
        "$($metadata.BUNDLE_NAME)/Contents/MacOS/$($metadata.PTY_HOST_NAME)",
        "$($metadata.BUNDLE_NAME)/Contents/MacOS/$($metadata.GHOSTTY_LIBRARY)"
    )
    $manifest = Get-Sha256Manifest -WorkingDirectory $OutputDir -Path $manifestPaths
    Set-Content -LiteralPath (Join-Path $OutputDir "$base.sha256") -Value $manifest -Encoding utf8
    Remove-Item -LiteralPath (Join-Path $OutputDir "$base.dmg.sha256") -Force -ErrorAction SilentlyContinue

    if ($Identity) {
        Write-Host "Released signed and notarized $archive and $dmgPath"
    }
    else {
        Write-Host "Released unsigned $archive and $dmgPath (no signing identity provided)"
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
