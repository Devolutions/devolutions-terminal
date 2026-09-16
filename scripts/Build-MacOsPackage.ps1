#!/usr/bin/env pwsh
#Requires -Version 7
<#
    .SYNOPSIS
    Publishes (or reuses a MACOS_PUBLISH_DIR), stages, ad-hoc signs, and
    zips a Devolutions Terminal .app bundle for one macOS RID.

    .PARAMETER Rid
    osx-arm64 or osx-x64.

    .PARAMETER Version
    The package version (e.g. 0.1.0).

    .PARAMETER OutputDir
    Directory to write the .app bundle, zip, and SHA-256 manifest to.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('osx-arm64', 'osx-x64')]
    [string]$Rid = 'osx-arm64',

    [Parameter(Position = 1)]
    [string]$Version = '0.1.0',

    [Parameter(Position = 2)]
    [string]$OutputDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:LC_ALL = 'C'

$scriptDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $scriptDir
$project = Join-Path $repoRoot 'src/Devolutions.Terminal/Devolutions.Terminal.csproj'
$metadataPath = Join-Path $repoRoot 'macos/package.env'
if (-not $OutputDir) {
    $OutputDir = Join-Path $repoRoot 'artifacts/packages'
}

Import-Module (Join-Path $scriptDir 'MacOsPackagingCommon.psm1') -Force

Assert-Darwin -Message 'macOS packages must be built on Darwin.'
$expectedArch = Get-MacOsExpectedArch -Rid $Rid
Assert-MacOsVersion -Version $Version
Assert-Command -Name 'file', 'lipo', 'codesign', 'ditto', 'shasum'

$metadata = Import-MacOsPackageEnv -Path $metadataPath
$epoch = Get-MacOsSourceDateEpoch -RepoRoot $repoRoot

$work = Join-Path $repoRoot "artifacts/macos-package-staging/$Rid-$PID"
$publishDir = Join-Path $work 'publish'
$appPath = Join-Path $work $metadata.BUNDLE_NAME
if (Test-Path -LiteralPath $work) {
    Remove-Item -LiteralPath $work -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

try {
    $macosPublishDir = $env:MACOS_PUBLISH_DIR
    if ($macosPublishDir) {
        if (-not (Test-Path -LiteralPath $macosPublishDir -PathType Container)) {
            throw "MACOS_PUBLISH_DIR does not exist: $macosPublishDir"
        }
        Invoke-Native -FilePath cp -ArgumentList '-a', "$macosPublishDir/.", "$publishDir/"
    }
    else {
        Assert-Command -Name 'dotnet'
        Invoke-Native -FilePath dotnet -ArgumentList @(
            'publish', $project,
            '-c', 'Release',
            '-r', $Rid,
            '--self-contained', 'true',
            '-o', $publishDir,
            "-p:VersionPrefix=$Version",
            '-p:DebugSymbols=false',
            '-p:DebugType=None',
            '-p:NativeDebugSymbols=false',
            '--verbosity', 'minimal'
        )
    }

    Get-ChildItem -LiteralPath $publishDir -Recurse -File -Include '*.dbg', '*.pdb' |
        Remove-Item -Force

    $artifacts = @(
        $metadata.EXECUTABLE_NAME, $metadata.CLI_NAME, $metadata.PTY_HOST_NAME, $metadata.GHOSTTY_LIBRARY,
        'libSkiaSharp.dylib', 'libHarfBuzzSharp.dylib'
    )
    foreach ($artifact in $artifacts) {
        $artifactPath = Join-Path $publishDir $artifact
        if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
            throw "Publish output is missing $artifact."
        }
        $archs = (& lipo -archs $artifactPath 2>$null)
        if (-not $archs -or -not ($archs -split '\s+' -contains $expectedArch)) {
            throw "$artifact has the wrong architecture for $Rid (lipo: $(if ($archs) { $archs } else { 'unknown'}))."
        }
    }

    & (Join-Path $scriptDir 'Stage-MacOsApp.ps1') $publishDir $appPath $Version $Rid
    if ($LASTEXITCODE -ne 0) {
        throw "Stage-MacOsApp.ps1 failed with exit code $LASTEXITCODE."
    }
    Invoke-Native -FilePath codesign -ArgumentList '--force', '--deep', '--sign', '-', $appPath

    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
    $OutputDir = (Resolve-Path -LiteralPath $OutputDir).ProviderPath
    $base = "$($metadata.PACKAGE_NAME)-$Version-$Rid"
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

    $manifestPaths = @(
        "$base.zip",
        "$($metadata.BUNDLE_NAME)/Contents/MacOS/$($metadata.EXECUTABLE_NAME)",
        "$($metadata.BUNDLE_NAME)/Contents/MacOS/$($metadata.CLI_NAME)",
        "$($metadata.BUNDLE_NAME)/Contents/MacOS/$($metadata.PTY_HOST_NAME)",
        "$($metadata.BUNDLE_NAME)/Contents/MacOS/$($metadata.GHOSTTY_LIBRARY)"
    )
    $manifest = Get-Sha256Manifest -WorkingDirectory $OutputDir -Path $manifestPaths
    Set-Content -LiteralPath (Join-Path $OutputDir "$base.sha256") -Value $manifest -Encoding utf8

    Write-Host "Built $archive"
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
