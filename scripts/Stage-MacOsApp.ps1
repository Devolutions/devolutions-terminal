#!/usr/bin/env pwsh
#Requires -Version 7
<#
    .SYNOPSIS
    Stages a Devolutions Terminal .app bundle from a NativeAOT publish
    directory: copies the published output, writes Info.plist with the
    version substituted in, generates the .icns app icon, copies license and
    third-party notices, and normalizes timestamps for reproducibility.

    .PARAMETER PublishDirectory
    The `dotnet publish` output directory for the target RID.

    .PARAMETER AppPath
    Destination path for the .app bundle (parent directories are created).

    .PARAMETER Version
    The package version (e.g. 0.1.0).

    .PARAMETER Rid
    osx-arm64 or osx-x64.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$PublishDirectory,

    [Parameter(Mandatory, Position = 1)]
    [string]$AppPath,

    [Parameter(Mandatory, Position = 2)]
    [string]$Version,

    [Parameter(Mandatory, Position = 3)]
    [ValidateSet('osx-arm64', 'osx-x64')]
    [string]$Rid
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:LC_ALL = 'C'

$scriptDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $scriptDir
$metadataPath = Join-Path $repoRoot 'macos/package.env'

Import-Module (Join-Path $scriptDir 'MacOsPackagingCommon.psm1') -Force

if (-not (Test-Path -LiteralPath $PublishDirectory -PathType Container)) {
    throw "Publish directory not found: $PublishDirectory"
}
$publishDir = (Resolve-Path -LiteralPath $PublishDirectory).ProviderPath

Assert-MacOsVersion -Version $Version
Assert-Command -Name 'sips', 'iconutil', 'plutil', 'xcrun'

$metadata = Import-MacOsPackageEnv -Path $metadataPath

$required = @(
    $metadata.EXECUTABLE_NAME,
    $metadata.CLI_NAME,
    $metadata.PTY_HOST_NAME,
    $metadata.GHOSTTY_LIBRARY,
    'libSkiaSharp.dylib',
    'libHarfBuzzSharp.dylib',
    'THIRD-PARTY-NOTICES-GHOSTTY.txt',
    'THIRD-PARTY-NOTICES-NOTO-EMOJI.txt'
)
foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDir $path) -PathType Leaf)) {
        throw "NativeAOT publish output is missing $path for $Rid."
    }
}

if (Test-Path -LiteralPath $AppPath) {
    Remove-Item -LiteralPath $AppPath -Recurse -Force
}
$contents = Join-Path $AppPath 'Contents'
$macosDir = Join-Path $contents 'MacOS'
$resources = Join-Path $contents 'Resources'
New-Item -ItemType Directory -Force -Path $macosDir, $resources | Out-Null

Invoke-Native -FilePath cp -ArgumentList '-a', "$publishDir/.", "$macosDir/"
Get-ChildItem -LiteralPath $macosDir -Recurse -Filter '*.pdb' | Remove-Item -Force
Get-ChildItem -LiteralPath $macosDir -Recurse -Filter '*.dbg' | Remove-Item -Force
Get-ChildItem -LiteralPath $macosDir -Recurse -Directory -Filter '*.dSYM' |
    Remove-Item -Recurse -Force
# NativeAOT compiles runtime configuration into the executable. Leaving this
# build-time sidecar in Contents/MacOS makes Developer ID bundle signing treat
# it as unsigned nested code.
Get-ChildItem -LiteralPath $macosDir -File -Filter '*.runtimeconfig.json' |
    Remove-Item -Force

$sourcePlist = Join-Path $repoRoot 'macos/Info.plist'
$destinationPlist = Join-Path $contents 'Info.plist'
$plistText = Get-Content -LiteralPath $sourcePlist -Raw -Encoding utf8
$plistText = $plistText.Replace('<string>0.1.0</string>', "<string>$Version</string>")
Set-Content -LiteralPath $destinationPlist -Value $plistText -NoNewline -Encoding utf8
Invoke-Native -FilePath plutil -ArgumentList '-lint', $destinationPlist | Out-Null

$iconWork = Join-Path ([System.IO.Path]::GetTempPath()) "devolutions-terminal-icon-$([guid]::NewGuid())"
$iconset = Join-Path $iconWork 'DevolutionsTerminal.iconset'
New-Item -ItemType Directory -Force -Path $iconset | Out-Null
try {
    $iconSource = Join-Path $repoRoot 'macos/DevolutionsTerminal.png'
    $sipsJobs = @(
        @{ Size = 16; Out = 'icon_16x16.png' },
        @{ Size = 32; Out = 'icon_16x16@2x.png' },
        @{ Size = 32; Out = 'icon_32x32.png' },
        @{ Size = 64; Out = 'icon_32x32@2x.png' },
        @{ Size = 128; Out = 'icon_128x128.png' },
        @{ Size = 256; Out = 'icon_128x128@2x.png' },
        @{ Size = 256; Out = 'icon_256x256.png' },
        @{ Size = 512; Out = 'icon_256x256@2x.png' },
        @{ Size = 512; Out = 'icon_512x512.png' },
        @{ Size = 1024; Out = 'icon_512x512@2x.png' }
    )
    foreach ($job in $sipsJobs) {
        Invoke-Native -FilePath sips -ArgumentList @(
            '-z', $job.Size, $job.Size,
            $iconSource,
            '--out', (Join-Path $iconset $job.Out)
        ) | Out-Null
    }
    Invoke-Native -FilePath iconutil -ArgumentList @(
        '-c', 'icns', $iconset, '-o', (Join-Path $resources "$($metadata.ICON_NAME).icns")
    )

    $partialPlist = Join-Path $iconWork 'app-icon-partial.plist'
    Invoke-Native -FilePath xcrun -ArgumentList @(
        'actool',
        (Join-Path $repoRoot 'macos/AppIcon.icon'),
        '--compile', $resources,
        '--app-icon', 'AppIcon',
        '--output-partial-info-plist', $partialPlist,
        '--platform', 'macosx',
        '--minimum-deployment-target', $metadata.MACOS_DEPLOYMENT_TARGET,
        '--errors',
        '--warnings'
    )
}
finally {
    Remove-Item -LiteralPath $iconWork -Recurse -Force -ErrorAction SilentlyContinue
}

Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $resources 'LICENSE') -Force
Move-Item -LiteralPath (Join-Path $macosDir 'THIRD-PARTY-NOTICES-GHOSTTY.txt') `
    -Destination (Join-Path $resources 'THIRD-PARTY-NOTICES-GHOSTTY.txt') -Force
Move-Item -LiteralPath (Join-Path $macosDir 'THIRD-PARTY-NOTICES-NOTO-EMOJI.txt') `
    -Destination (Join-Path $resources 'THIRD-PARTY-NOTICES-NOTO-EMOJI.txt') -Force
Invoke-Native -FilePath chmod -ArgumentList @(
    '0644',
    (Join-Path $resources 'LICENSE'),
    (Join-Path $resources 'THIRD-PARTY-NOTICES-GHOSTTY.txt'),
    (Join-Path $resources 'THIRD-PARTY-NOTICES-NOTO-EMOJI.txt')
)

Invoke-Native -FilePath chmod -ArgumentList @(
    '0755',
    (Join-Path $macosDir $metadata.EXECUTABLE_NAME),
    (Join-Path $macosDir $metadata.CLI_NAME),
    (Join-Path $macosDir $metadata.PTY_HOST_NAME)
)

$epoch = Get-MacOsSourceDateEpoch -RepoRoot $repoRoot
Set-MacOsReproducibleTimestamps -Path $AppPath -Epoch $epoch
