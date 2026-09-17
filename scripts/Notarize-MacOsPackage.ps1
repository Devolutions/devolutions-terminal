#!/usr/bin/env pwsh
#Requires -Version 7
<#
    .SYNOPSIS
    Submits a macOS .app bundle or .dmg to Apple's notary service, staples
    the resulting ticket, and verifies Gatekeeper acceptance. App bundles
    are submitted in a temporary ZIP because notarytool accepts archives
    and disk images, not raw directory bundles.

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
Assert-Command -Name 'ditto', 'spctl', 'xcrun'

if ([string]::IsNullOrWhiteSpace($env:APPLE_BOT_PASSWORD)) {
    throw 'APPLE_BOT_PASSWORD is required.'
}

$metadata = Import-MacOsPackageEnv -Path (Join-Path (Split-Path -Parent $PSScriptRoot) 'macos/package.env')
$resolvedTarget = (Resolve-Path -LiteralPath $TargetPath).ProviderPath
$target = Get-Item -LiteralPath $resolvedTarget
$submissionPath = $resolvedTarget
$temporaryArchive = $null

if ($target.PSIsContainer) {
    if (-not $target.Name.EndsWith('.app', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsupported notarization directory: $resolvedTarget"
    }
    $temporaryArchive = Join-Path ([System.IO.Path]::GetTempPath()) "notary-$([guid]::NewGuid().ToString('N')).zip"
    Invoke-Native -FilePath ditto -ArgumentList @(
        '-c', '-k', '--keepParent', '--norsrc', '--noextattr', '--noacl',
        $resolvedTarget, $temporaryArchive
    )
    $submissionPath = $temporaryArchive
}
elseif ($target.Extension -ne '.dmg') {
    throw "Unsupported notarization file: $resolvedTarget"
}

$credentials = @(
    '--apple-id', 'bot@devolutions.net',
    '--team-id', $metadata.APPLE_TEAM_ID,
    '--password', $env:APPLE_BOT_PASSWORD
)

try {
    $notaryOutput = & xcrun notarytool submit $submissionPath @credentials `
        --wait --timeout 30m --output-format json
    $notaryExitCode = $LASTEXITCODE
    $notaryJson = $notaryOutput -join "`n"
    if ($notaryJson) {
        Write-Host $notaryJson
    }

    $notaryResult = $null
    try {
        $notaryResult = $notaryJson | ConvertFrom-Json
    }
    catch {
        if ($notaryExitCode -eq 0) {
            throw "notarytool returned invalid JSON: $notaryJson"
        }
    }

    $status = if ($null -ne $notaryResult -and $notaryResult.PSObject.Properties['status']) {
        [string]$notaryResult.status
    }
    else {
        'unknown'
    }
    $submissionId = if ($null -ne $notaryResult -and $notaryResult.PSObject.Properties['id']) {
        [string]$notaryResult.id
    }
    else {
        $null
    }

    if ($notaryExitCode -ne 0 -or $status -ne 'Accepted') {
        if (-not [string]::IsNullOrWhiteSpace($submissionId)) {
            & xcrun notarytool log $submissionId @credentials
        }
        throw "Notarization failed for $resolvedTarget (exit code $notaryExitCode, status $status)."
    }

    Invoke-Native -FilePath xcrun -ArgumentList 'stapler', 'staple', $resolvedTarget
    Invoke-Native -FilePath xcrun -ArgumentList 'stapler', 'validate', $resolvedTarget

    if ($target.PSIsContainer) {
        Invoke-Native -FilePath spctl -ArgumentList '--assess', '--type', 'execute', '--verbose=4', $resolvedTarget
    }
    else {
        Invoke-Native -FilePath spctl -ArgumentList @(
            '--assess', '--type', 'open', '--context', 'context:primary-signature',
            '--verbose=4', $resolvedTarget
        )
    }

    Write-Host "Notarized, stapled, and assessed $resolvedTarget"
}
finally {
    if ($temporaryArchive) {
        Remove-Item -LiteralPath $temporaryArchive -Force -ErrorAction SilentlyContinue
    }
}
