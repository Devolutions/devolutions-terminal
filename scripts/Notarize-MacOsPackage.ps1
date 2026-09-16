#!/usr/bin/env pwsh
#Requires -Version 7
<#
    .SYNOPSIS
    Submits a macOS .app bundle or .dmg to Apple's notary service and
    staples the resulting ticket. Authentication uses the Devolutions release
    Apple ID and its app-specific password from APPLE_BOT_PASSWORD.

    .PARAMETER TargetPath
    Path to the .app bundle or .dmg to notarize and staple.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$TargetPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:LC_ALL = 'C'

Import-Module (Join-Path $PSScriptRoot 'MacOsPackagingCommon.psm1') -Force

Assert-Darwin -Message 'Notarization requires Darwin.'
if (-not (Test-Path -LiteralPath $TargetPath)) {
    throw "Path not found: $TargetPath"
}
Assert-Command -Name 'xcrun'

if ([string]::IsNullOrWhiteSpace($env:APPLE_BOT_PASSWORD)) {
    throw 'APPLE_BOT_PASSWORD is required.'
}

$profile = 'DEVOLUTIONS_TERMINAL'
Invoke-Native -FilePath xcrun -ArgumentList @(
    'notarytool', 'store-credentials', $profile,
    '--apple-id', 'bot@devolutions.net',
    '--team-id', 'N592S9ASDB',
    '--password', $env:APPLE_BOT_PASSWORD
)

Invoke-Native -FilePath xcrun -ArgumentList @(
    'notarytool', 'submit', $TargetPath,
    '--keychain-profile', $profile,
    '--wait',
    '--timeout', '30m'
)

Invoke-Native -FilePath xcrun -ArgumentList 'stapler', 'staple', $TargetPath
Invoke-Native -FilePath xcrun -ArgumentList 'stapler', 'validate', $TargetPath
Write-Host "Notarized and stapled $TargetPath"
