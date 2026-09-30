#!/usr/bin/env pwsh
#Requires -Version 7
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'MacOsPackagingCommon.psm1') -Force
Assert-Darwin
Assert-Command -Name 'codesign', 'cp', 'clang'
$metadata = Import-MacOsPackageEnv -Path (Join-Path (Split-Path -Parent $PSScriptRoot) 'macos/package.env')
$rid = if ((& uname -m) -eq 'arm64') { 'osx-arm64' } else { 'osx-x64' }
$work = Join-Path ([IO.Path]::GetTempPath()) "macos-nuget-signing-$([guid]::NewGuid().ToString('N'))"
$app = Join-Path $work 'Fixture.app'
$code = Join-Path $app 'Contents/MacOS'
$resources = Join-Path $app 'Contents/Resources'
try {
    New-Item -ItemType Directory -Path $code, $resources -Force | Out-Null
    $fixtureSource = Join-Path $work 'fixture.c'
    $fixtureBinary = Join-Path $work 'fixture'
    Set-Content $fixtureSource 'int main(void) { return 0; }' -NoNewline
    Invoke-Native -FilePath clang -ArgumentList '-arch', (Get-MacOsExpectedArch -Rid $rid), $fixtureSource, '-o', $fixtureBinary
    foreach ($name in @(
        $metadata.EXECUTABLE_NAME, $metadata.CLI_NAME, $metadata.PTY_HOST_NAME,
        $metadata.GHOSTTY_LIBRARY, 'libAvaloniaNative.dylib', 'libSkiaSharp.dylib', 'libHarfBuzzSharp.dylib'
    )) {
        Invoke-Native -FilePath cp -ArgumentList $fixtureBinary, (Join-Path $code $name)
    }
    @"
<?xml version="1.0" encoding="UTF-8"?>
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>$($metadata.EXECUTABLE_NAME)</string>
<key>CFBundleIdentifier</key><string>$($metadata.APP_ID)</string>
<key>CFBundlePackageType</key><string>APPL</string>
</dict></plist>
"@ | Set-Content (Join-Path $app 'Contents/Info.plist') -Encoding utf8
    foreach ($name in @('LICENSE', 'THIRD-PARTY-NOTICES-CONPTY.txt', 'THIRD-PARTY-NOTICES-GHOSTTY.txt', 'THIRD-PARTY-NOTICES-NOTO-EMOJI.txt')) {
        Set-Content (Join-Path $resources $name) "fixture license $name" -NoNewline
    }
    & (Join-Path $PSScriptRoot 'Sign-MacOsPackage.ps1') $app '-'
    $sourceHash = (Get-FileHash (Join-Path $code $metadata.EXECUTABLE_NAME)).Hash
    $payload = Join-Path $work 'payload'
    & (Join-Path $PSScriptRoot 'Stage-MacOsNuGetPayload.ps1') -AppPath $app -OutputDirectory $payload -Rid $rid -Identity '-'
    if ((Get-FileHash (Join-Path $code $metadata.EXECUTABLE_NAME)).Hash -cne $sourceHash) {
        throw 'Standalone signing mutated the source app.'
    }
    foreach ($notice in Get-ChildItem $resources -File) {
        if ((Get-FileHash (Join-Path $payload $notice.Name)).Hash -cne (Get-FileHash $notice.FullName).Hash) {
            throw "Standalone payload changed legal notice $($notice.Name)."
        }
    }
    $rejected = $false
    try {
        & (Join-Path $PSScriptRoot 'Test-MacOsNuGetPayload.ps1') -PayloadDirectory $payload -Rid $rid -RequireDeveloperId
    }
    catch {
        if ($_.Exception.Message -notmatch 'not signed by Apple team') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'A signed release accepted an ad-hoc NuGet payload.' }
    Write-Host 'StandaloneMacOsNuGetSigning_PreservesSourceAndLicensesAndRejectsAdHocRelease passed.'
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force
}
