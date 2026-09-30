#!/usr/bin/env pwsh
#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [Parameter(Mandatory)][ValidateSet('osx-arm64', 'osx-x64')][string]$Rid,
    [switch]$RequireDeveloperId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'MacOsPackagingCommon.psm1') -Force
Assert-Darwin
Assert-Command -Name 'codesign', 'file', 'lipo'
$metadata = Import-MacOsPackageEnv -Path (Join-Path (Split-Path -Parent $PSScriptRoot) 'macos/package.env')
$expectedArch = Get-MacOsExpectedArch -Rid $Rid
$requirements = @{ RequireHardenedRuntime = $true }
if ($RequireDeveloperId) {
    $requirements.TeamIdentifier = $metadata.APPLE_TEAM_ID
    $requirements.RequireTimestamp = $true
}
$binaries = @(
    $metadata.EXECUTABLE_NAME, $metadata.CLI_NAME, $metadata.PTY_HOST_NAME,
    $metadata.GHOSTTY_LIBRARY, 'libAvaloniaNative.dylib', 'libSkiaSharp.dylib', 'libHarfBuzzSharp.dylib'
)
foreach ($name in $binaries) {
    $path = Join-Path $PayloadDirectory $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "NuGet payload is missing $name." }
    $kind = & file -b $path
    if ($LASTEXITCODE -ne 0 -or $kind -notmatch 'Mach-O') { throw "$name is not Mach-O code." }
    $architectures = & lipo -archs $path
    if ($LASTEXITCODE -ne 0 -or $architectures -split '\s+' -notcontains $expectedArch) {
        throw "$name does not support $Rid."
    }
    Assert-MacOsCodeSignature -Path $path @requirements
    if ($name -in @($metadata.EXECUTABLE_NAME, $metadata.CLI_NAME)) {
        $entitlementText = (& codesign --display --entitlements - $path 2>$null) -join "`n"
        if ($LASTEXITCODE -ne 0) { throw "Unable to inspect $name entitlements." }
        [xml]$entitlements = $entitlementText
        foreach ($key in @(
            'com.apple.security.cs.allow-jit',
            'com.apple.security.cs.allow-unsigned-executable-memory',
            'com.apple.security.cs.disable-library-validation'
        )) {
            $node = $entitlements.SelectSingleNode("/plist/dict/key[text()='$key']")
            if ($null -eq $node -or $node.NextSibling.LocalName -ne 'true') {
                throw "$name is missing required runtime entitlement $key."
            }
        }
    }
}
foreach ($name in @('LICENSE', 'THIRD-PARTY-NOTICES-CONPTY.txt', 'THIRD-PARTY-NOTICES-GHOSTTY.txt', 'THIRD-PARTY-NOTICES-NOTO-EMOJI.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PayloadDirectory $name) -PathType Leaf)) {
        throw "NuGet payload is missing $name."
    }
}
$unexpected = @(Get-ChildItem -LiteralPath $PayloadDirectory -Recurse -Force |
    Where-Object { $_.PSIsContainer -or $_.Name -match '\.(pdb|dbg|dSYM|exe|runtimeconfig\.json)$' })
if ($unexpected.Count -gt 0) { throw "Unexpected files/directories in the standalone macOS NuGet payload: $($unexpected.Name -join ', ')" }
Write-Host "macOS $Rid standalone NuGet payload signatures and layout passed."
