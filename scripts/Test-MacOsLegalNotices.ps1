#!/usr/bin/env pwsh
#Requires -Version 7
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'MacOsPackagingCommon.psm1') -Force
$work = Join-Path ([IO.Path]::GetTempPath()) "macos-notices-test-$([guid]::NewGuid().ToString('N'))"

try {
    foreach ($rid in @('osx-x64', 'osx-arm64')) {
        $contents = Join-Path $work "$rid/Notice Test.app/Contents"
        $macos = Join-Path $contents 'MacOS'
        $resources = Join-Path $contents 'Resources'
        New-Item -ItemType Directory -Path $macos, $resources -Force | Out-Null
        $codePath = Join-Path $macos 'dt'
        [IO.File]::WriteAllBytes($codePath, [byte[]](0xCF, 0xFA, 0xED, 0xFE))
        $codeHash = (Get-FileHash -LiteralPath $codePath).Hash
        $notices = @(
            'THIRD-PARTY-NOTICES-GHOSTTY.txt',
            'THIRD-PARTY-NOTICES-NOTO-EMOJI.txt',
            'THIRD-PARTY-NOTICES-CONPTY.txt',
            'THIRD-PARTY-NOTICES-FUTURE-DEPENDENCY.txt'
        )
        foreach ($notice in $notices) {
            [IO.File]::WriteAllText((Join-Path $macos $notice), "License content for $notice")
        }

        Move-MacOsLegalNotices -MacOsDirectory $macos -ResourcesDirectory $resources
        foreach ($notice in $notices) {
            $resourcePath = Join-Path $resources $notice
            if (-not (Test-Path -LiteralPath $resourcePath -PathType Leaf) -or
                [IO.File]::ReadAllText($resourcePath) -cne "License content for $notice") {
                throw "$rid did not preserve the exact contents of $notice in Resources."
            }
            if (Test-Path -LiteralPath (Join-Path $macos $notice)) {
                throw "$rid left $notice in the code-only MacOS directory."
            }
        }
        if (@(Get-ChildItem -LiteralPath $resources -File).Count -ne $notices.Count -or
            @(Get-ChildItem -LiteralPath $macos -File).Count -ne 1 -or
            (Get-FileHash -LiteralPath $codePath).Hash -cne $codeHash) {
            throw "$rid notice relocation changed the code or produced an unexpected layout."
        }
        Write-Host "Move-MacOsLegalNotices_AllPublishedNoticesPreserved ($rid) passed."

        Move-MacOsLegalNotices -MacOsDirectory $macos -ResourcesDirectory $resources
        if (@(Get-ChildItem -LiteralPath $resources -File).Count -ne $notices.Count -or
            (Get-FileHash -LiteralPath $codePath).Hash -cne $codeHash) {
            throw "$rid notice-free relocation changed the existing layout."
        }
        Write-Host "Move-MacOsLegalNotices_NoNoticesLeavesCodeUntouched ($rid) passed."
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force
}
