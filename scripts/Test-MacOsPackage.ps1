#!/usr/bin/env pwsh
#Requires -Version 7
<#
    .SYNOPSIS
    Validates one or more macOS .app bundles or zips without launching the
    UI: required files, executable bits, Info.plist contents, architectures,
    Mach-O binaries, and absence of debug/private-key files.

    .PARAMETER Rid
    osx-arm64 or osx-x64.

    .PARAMETER Package
    One or more .app or .zip paths to validate.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('osx-arm64', 'osx-x64')]
    [string]$Rid,

    [Parameter(Mandatory, Position = 1, ValueFromRemainingArguments)]
    [string[]]$Package
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:LC_ALL = 'C'

$scriptDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $scriptDir
$metadataPath = Join-Path $repoRoot 'macos/package.env'

Import-Module (Join-Path $scriptDir 'MacOsPackagingCommon.psm1') -Force

$expectedArch = Get-MacOsExpectedArch -Rid $Rid
Assert-Darwin -Message 'macOS package validation requires Darwin.'
Assert-Command -Name 'ditto', 'file', 'lipo', 'plutil'

$metadata = Import-MacOsPackageEnv -Path $metadataPath

$work = Join-Path $repoRoot "artifacts/macos-package-validation/$Rid-$PID"
if (Test-Path -LiteralPath $work) {
    Remove-Item -LiteralPath $work -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $work | Out-Null

function Test-MacOsAppBundle {
    param(
        [Parameter(Mandatory)]
        [string]$App
    )

    $label = Split-Path -Leaf $App
    $macosDir = Join-Path $App 'Contents/MacOS'
    $plist = Join-Path $App 'Contents/Info.plist'
    $icns = Join-Path $App "Contents/Resources/$($metadata.ICON_NAME).icns"

    $requiredPaths = @(
        (Join-Path $macosDir $metadata.EXECUTABLE_NAME),
        (Join-Path $macosDir $metadata.CLI_NAME),
        (Join-Path $macosDir $metadata.PTY_HOST_NAME),
        (Join-Path $macosDir $metadata.GHOSTTY_LIBRARY),
        (Join-Path $macosDir 'libSkiaSharp.dylib'),
        (Join-Path $macosDir 'libHarfBuzzSharp.dylib'),
        (Join-Path $App 'Contents/Resources/THIRD-PARTY-NOTICES-GHOSTTY.txt'),
        (Join-Path $App 'Contents/Resources/THIRD-PARTY-NOTICES-NOTO-EMOJI.txt'),
        (Join-Path $App 'Contents/Resources/LICENSE'),
        $plist,
        $icns
    )
    foreach ($path in $requiredPaths) {
        if (-not (Test-Path -LiteralPath $path)) {
            $relative = $path.Substring($App.Length).TrimStart('/')
            throw "$label is missing $relative."
        }
    }

    foreach ($executable in @($metadata.EXECUTABLE_NAME, $metadata.CLI_NAME, $metadata.PTY_HOST_NAME)) {
        $executablePath = Join-Path $macosDir $executable
        $mode = [System.IO.File]::GetUnixFileMode($executablePath)
        if (($mode -band [System.IO.UnixFileMode]::UserExecute) -eq 0) {
            throw "$label $executable is not executable."
        }
    }

    Invoke-Native -FilePath plutil -ArgumentList '-lint', $plist | Out-Null
    $plistJson = & plutil -convert json -o - $plist
    if ($LASTEXITCODE -ne 0) {
        throw "$label Info.plist could not be converted to JSON."
    }
    $plistData = $plistJson | ConvertFrom-Json
    if ($plistData.CFBundleIdentifier -ne $metadata.APP_ID) {
        throw "$label CFBundleIdentifier does not match $($metadata.APP_ID)."
    }
    if ($plistData.CFBundleExecutable -ne $metadata.EXECUTABLE_NAME) {
        throw "$label CFBundleExecutable does not match $($metadata.EXECUTABLE_NAME)."
    }
    if ($plistData.CFBundlePackageType -ne 'APPL') {
        throw "$label CFBundlePackageType is not APPL."
    }
    if ($plistData.LSMinimumSystemVersion -ne $metadata.MACOS_DEPLOYMENT_TARGET) {
        throw "$label LSMinimumSystemVersion does not match $($metadata.MACOS_DEPLOYMENT_TARGET)."
    }
    if ($plistData.NSHighResolutionCapable -ne $true) {
        throw "$label NSHighResolutionCapable is not true."
    }
    if ($metadata.URL_SCHEME -notin $plistData.CFBundleURLTypes[0].CFBundleURLSchemes) {
        throw "$label does not declare the $($metadata.URL_SCHEME) URL scheme."
    }
    if ($plistData.CFBundleIconFile -ne $metadata.ICON_NAME) {
        throw "$label CFBundleIconFile does not match $($metadata.ICON_NAME)."
    }

    foreach ($binary in @(
        $metadata.EXECUTABLE_NAME, $metadata.CLI_NAME, $metadata.PTY_HOST_NAME, $metadata.GHOSTTY_LIBRARY,
        'libSkiaSharp.dylib', 'libHarfBuzzSharp.dylib'
    )) {
        $binaryPath = Join-Path $macosDir $binary
        $archs = (& lipo -archs $binaryPath)
        if (-not ($archs -split '\s+' -contains $expectedArch)) {
            throw "$label $binary has the wrong architecture (lipo: $archs)."
        }
        $kind = (& file -b $binaryPath)
        if ($kind -notmatch 'Mach-O') {
            throw "$label $binary is not Mach-O."
        }
    }

    $forbidden = Get-ChildItem -LiteralPath $App -Recurse -File -Include `
        '*.pdb', '*.dbg', '*.key', '*.pfx', '*.pem'
    if ($forbidden) {
        throw "$label contains debug or private-key files."
    }
    $dsyms = Get-ChildItem -LiteralPath $App -Recurse -Directory -Filter '*.dSYM'
    if ($dsyms) {
        throw "$label contains dSYM bundles."
    }
}

try {
    foreach ($package in $Package) {
        if (-not (Test-Path -LiteralPath $package)) {
            throw "Package not found: $package"
        }
        $resolvedPackage = (Resolve-Path -LiteralPath $package).ProviderPath

        switch -Wildcard ($resolvedPackage) {
            '*.zip' {
                $extract = Join-Path $work ([System.IO.Path]::GetFileNameWithoutExtension($resolvedPackage))
                New-Item -ItemType Directory -Force -Path $extract | Out-Null
                Invoke-Native -FilePath ditto -ArgumentList '-x', '-k', $resolvedPackage, $extract
                $app = Get-ChildItem -LiteralPath $extract -Recurse -Depth 1 -Directory -Filter '*.app' |
                    Select-Object -First 1 -ExpandProperty FullName
                if (-not $app) {
                    throw "$(Split-Path -Leaf $resolvedPackage) does not contain an app bundle."
                }
                Test-MacOsAppBundle -App $app
            }
            '*.app' {
                Test-MacOsAppBundle -App $resolvedPackage
            }
            default {
                throw "Unsupported macOS package: $resolvedPackage"
            }
        }
        Write-Host "Validated $(Split-Path -Leaf $resolvedPackage)"
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
