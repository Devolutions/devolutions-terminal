#!/usr/bin/env pwsh
#Requires -Version 7
<#
    .SYNOPSIS
    Reproduces the macOS bundle layout that previously made codesign treat an
    auxiliary executable as the enclosing app and reject an adjacent data file.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:LC_ALL = 'C'

$scriptDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $scriptDir

Import-Module (Join-Path $scriptDir 'MacOsPackagingCommon.psm1') -Force

Assert-Darwin -Message 'The macOS signing regression test requires Darwin.'
Assert-Command -Name 'codesign', 'cp'

$work = Join-Path ([System.IO.Path]::GetTempPath()) "macos-signing-test-$([guid]::NewGuid().ToString('N'))"
$appPath = Join-Path $work 'Signing Test.app'
$contents = Join-Path $appPath 'Contents'
$macosDir = Join-Path $contents 'MacOS'
$entitlements = Join-Path $work 'entitlements.plist'
$oldEntitlements = $env:MACOS_ENTITLEMENTS

try {
    New-Item -ItemType Directory -Force -Path $macosDir | Out-Null
    Invoke-Native -FilePath cp -ArgumentList '-p', '/usr/bin/true', (Join-Path $macosDir 'MainTool')
    Invoke-Native -FilePath cp -ArgumentList '-p', '/usr/bin/true', (Join-Path $macosDir 'HelperTool')
    Set-Content -LiteralPath (Join-Path $macosDir 'HelperTool.runtimeconfig.json') `
        -Value '{"runtimeOptions":{}}' -NoNewline -Encoding utf8

    @'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key>
  <string>MainTool</string>
  <key>CFBundleIdentifier</key>
  <string>com.devolutions.Terminal.SigningTest</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
</dict>
</plist>
'@ | Set-Content -LiteralPath (Join-Path $contents 'Info.plist') -NoNewline -Encoding utf8

    @'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict/>
</plist>
'@ | Set-Content -LiteralPath $entitlements -NoNewline -Encoding utf8

    $env:MACOS_ENTITLEMENTS = $entitlements
    & (Join-Path $scriptDir 'Sign-MacOsPackage.ps1') $appPath '-'
    if ($LASTEXITCODE -ne 0) {
        throw "Sign-MacOsPackage.ps1 failed with exit code $LASTEXITCODE."
    }

    Invoke-Native -FilePath codesign -ArgumentList '--verify', '--deep', '--strict', '--verbose=2', $appPath

    $isolatedHelper = Join-Path $work 'HelperTool'
    Invoke-Native -FilePath cp -ArgumentList '-p', (Join-Path $macosDir 'HelperTool'), $isolatedHelper
    Assert-MacOsCodeSignature -Path $isolatedHelper -RequireHardenedRuntime

    Write-Host 'macOS standalone auxiliary-code signing regression test passed.'
}
finally {
    $env:MACOS_ENTITLEMENTS = $oldEntitlements
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
