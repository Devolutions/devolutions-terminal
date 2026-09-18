#!/usr/bin/env pwsh
#Requires -Version 7
<#
    .SYNOPSIS
    Validates the macOS packaging PowerShell scripts (syntax) and the
    canonical macos/package.env + macos/Info.plist metadata they rely on.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:LC_ALL = 'C'

$scriptDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $scriptDir
$metadataPath = Join-Path $repoRoot 'macos/package.env'
$plistPath = Join-Path $repoRoot 'macos/Info.plist'

Import-Module (Join-Path $scriptDir 'MacOsPackagingCommon.psm1') -Force

$scripts = @(
    'Build-MacOsPackage.ps1',
    'Stage-MacOsApp.ps1',
    'Test-MacOsPackage.ps1',
    'Test-MacOsRuntime.ps1',
    'Test-MacOsCodeSigning.ps1',
    'Sign-MacOsPackage.ps1',
    'Build-MacOsDmg.ps1',
    'Notarize-MacOsPackage.ps1',
    'Release-MacOsPackage.ps1'
) | ForEach-Object { Join-Path $scriptDir $_ }

foreach ($script in $scripts) {
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) {
        $details = ($parseErrors | ForEach-Object { $_.ToString() }) -join "`n"
        throw "Syntax errors in ${script}:`n$details"
    }
}

$metadata = Import-MacOsPackageEnv -Path $metadataPath

function Assert-Equal {
    param(
        [Parameter(Mandatory)][string]$Actual,
        [Parameter(Mandatory)][string]$Expected,
        [Parameter(Mandatory)][string]$Name
    )
    if ($Actual -ne $Expected) {
        throw "$Name mismatch: expected '$Expected', got '$Actual'."
    }
}

Assert-Equal -Actual $metadata.PACKAGE_NAME -Expected 'devolutions-terminal' -Name 'PACKAGE_NAME'
Assert-Equal -Actual $metadata.APP_ID -Expected 'com.devolutions.Terminal' -Name 'APP_ID'
Assert-Equal -Actual $metadata.APPLE_TEAM_ID -Expected 'N592S9ASDB' -Name 'APPLE_TEAM_ID'
Assert-Equal -Actual $metadata.EXECUTABLE_NAME -Expected 'Devolutions.Terminal' -Name 'EXECUTABLE_NAME'
Assert-Equal -Actual $metadata.CLI_NAME -Expected 'dt' -Name 'CLI_NAME'
Assert-Equal -Actual $metadata.PTY_HOST_NAME -Expected 'dt-pty-host' -Name 'PTY_HOST_NAME'
Assert-Equal -Actual $metadata.GHOSTTY_LIBRARY -Expected 'libghostty-vt.dylib' -Name 'GHOSTTY_LIBRARY'
Assert-Equal -Actual $metadata.ICON_NAME -Expected 'DevolutionsTerminal' -Name 'ICON_NAME'
Assert-Equal -Actual $metadata.MACOS_DEPLOYMENT_TARGET -Expected '13.0' -Name 'MACOS_DEPLOYMENT_TARGET'
Assert-Equal -Actual $metadata.LICENSE_ID -Expected 'MIT AND OFL-1.1' -Name 'LICENSE_ID'
Assert-Equal -Actual $metadata.SBOM_LICENSE_ID -Expected 'MIT AND OFL-1.1' -Name 'SBOM_LICENSE_ID'
Assert-Equal -Actual $metadata.LICENSE_ID -Expected $metadata.SBOM_LICENSE_ID -Name 'LICENSE_ID/SBOM_LICENSE_ID'
Assert-Equal -Actual $metadata.URL_SCHEME -Expected 'dterm' -Name 'URL_SCHEME'
Assert-Equal -Actual $metadata.BUNDLE_NAME -Expected 'Devolutions Terminal.app' -Name 'BUNDLE_NAME'

if (Select-String -LiteralPath $metadataPath -Pattern '(^|_)VERSION=' -Quiet) {
    throw "macOS package metadata must not duplicate the release version."
}

Assert-Command -Name 'plutil'
$plistJson = & plutil -convert json -o - $plistPath
if ($LASTEXITCODE -ne 0) {
    throw "plutil failed to parse $plistPath."
}
$plist = $plistJson | ConvertFrom-Json

if ($plist.CFBundleIdentifier -ne $metadata.APP_ID) {
    throw "Info.plist CFBundleIdentifier does not match APP_ID."
}
if ($plist.CFBundleExecutable -ne $metadata.EXECUTABLE_NAME) {
    throw "Info.plist CFBundleExecutable does not match EXECUTABLE_NAME."
}
if ($plist.CFBundlePackageType -ne 'APPL') {
    throw "Info.plist CFBundlePackageType must be APPL."
}
if ($plist.LSMinimumSystemVersion -ne $metadata.MACOS_DEPLOYMENT_TARGET) {
    throw "Info.plist LSMinimumSystemVersion does not match MACOS_DEPLOYMENT_TARGET."
}
if ($plist.CFBundleIconFile -ne 'DevolutionsTerminal') {
    throw "Info.plist CFBundleIconFile must be DevolutionsTerminal."
}
if ($plist.CFBundleIconName -ne 'AppIcon') {
    throw "Info.plist CFBundleIconName must be AppIcon."
}
if ($plist.NSHighResolutionCapable -ne $true) {
    throw "Info.plist NSHighResolutionCapable must be true."
}
$schemes = @()
foreach ($urlType in $plist.CFBundleURLTypes) {
    $schemes += $urlType.CFBundleURLSchemes
}
if ($metadata.URL_SCHEME -notin $schemes) {
    throw "Info.plist CFBundleURLTypes does not declare the '$($metadata.URL_SCHEME)' scheme."
}

$builder = Join-Path $scriptDir 'Build-MacOsPackage.ps1'
$builderFailed = $false
try {
    $null = & $builder 'invalid-rid' 2>$null
    if ($LASTEXITCODE -ne 0) {
        $builderFailed = $true
    }
}
catch {
    $builderFailed = $true
}
if (-not $builderFailed) {
    throw "Builder accepted an invalid RID."
}

function Assert-EqualLong {
    param(
        [Parameter(Mandatory)][long]$Actual,
        [Parameter(Mandatory)][long]$Expected,
        [Parameter(Mandatory)][string]$Name
    )
    if ($Actual -ne $Expected) {
        throw "$Name mismatch: expected $Expected, got $Actual."
    }
}

Assert-EqualLong -Actual (Get-MacOsWritableDmgSizeMegabytes -SourceBytes 0) -Expected 64 -Name 'empty DMG size'
Assert-EqualLong -Actual (Get-MacOsWritableDmgSizeMegabytes -SourceBytes 20MB) -Expected 64 -Name 'small bundle DMG size'
Assert-EqualLong -Actual (Get-MacOsWritableDmgSizeMegabytes -SourceBytes 32MB) -Expected 64 -Name 'legacy 32m bundle DMG size'
Assert-EqualLong -Actual (Get-MacOsWritableDmgSizeMegabytes -SourceBytes (80MB + 1)) -Expected 113 -Name '80 MiB+1 bundle DMG size'
Assert-EqualLong -Actual (Get-MacOsWritableDmgSizeMegabytes -SourceBytes 200MB) -Expected 250 -Name '200 MiB bundle DMG size'

$background = Join-Path $repoRoot 'macos/InstallerBackground.png'
Assert-EqualLong -Actual (Get-MacOsTreeByteSize -Path $background) -Expected (Get-Item -LiteralPath $background).Length -Name 'background tree size'

Write-Host "macOS packaging scripts and canonical metadata validation passed."
