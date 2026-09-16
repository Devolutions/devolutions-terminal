#!/usr/bin/env pwsh
#Requires -Version 7
<#
    .SYNOPSIS
    Submits a macOS .app bundle or .dmg to Apple's notary service and
    staples the resulting ticket. Authentication uses an App Store Connect
    API key (no Apple ID password or 2FA prompts), supplied via:
      APPLE_NOTARIZATION_API_KEY_ID     Key ID (e.g. "2X9R4HXF34")
      APPLE_NOTARIZATION_API_ISSUER_ID  Issuer ID (UUID)
      APPLE_NOTARIZATION_API_KEY_P8     Base64-encoded contents of the .p8 key

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
Assert-Command -Name 'xcrun', 'ditto'

foreach ($name in @('APPLE_NOTARIZATION_API_KEY_ID', 'APPLE_NOTARIZATION_API_ISSUER_ID', 'APPLE_NOTARIZATION_API_KEY_P8')) {
    $value = [System.Environment]::GetEnvironmentVariable($name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "$name is required"
    }
}
$keyId = $env:APPLE_NOTARIZATION_API_KEY_ID
$issuerId = $env:APPLE_NOTARIZATION_API_ISSUER_ID
$keyBase64 = $env:APPLE_NOTARIZATION_API_KEY_P8

$work = Join-Path ([System.IO.Path]::GetTempPath()) "devolutions-terminal-notarize-$([guid]::NewGuid())"
New-Item -ItemType Directory -Force -Path $work | Out-Null
try {
    $keyPath = Join-Path $work "AuthKey_$keyId.p8"
    $keyBytes = [Convert]::FromBase64String($keyBase64)
    [System.IO.File]::WriteAllBytes($keyPath, $keyBytes)
    [System.IO.File]::SetUnixFileMode($keyPath, [System.IO.UnixFileMode]'UserRead, UserWrite')

    $submissionPath = $TargetPath
    if ($TargetPath -like '*.app') {
        $baseName = [System.IO.Path]::GetFileNameWithoutExtension($TargetPath)
        $submissionPath = Join-Path $work "$baseName-notarize.zip"
        Invoke-Native -FilePath ditto -ArgumentList '-c', '-k', '--keepParent', '--norsrc', '--noextattr', '--noacl', `
            $TargetPath, $submissionPath
    }

    Invoke-Native -FilePath xcrun -ArgumentList @(
        'notarytool', 'submit', $submissionPath,
        '--key', $keyPath,
        '--key-id', $keyId,
        '--issuer', $issuerId,
        '--wait',
        '--timeout', '30m'
    )

    Invoke-Native -FilePath xcrun -ArgumentList 'stapler', 'staple', $TargetPath
    Invoke-Native -FilePath xcrun -ArgumentList 'stapler', 'validate', $TargetPath
    Write-Host "Notarized and stapled $TargetPath"
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
