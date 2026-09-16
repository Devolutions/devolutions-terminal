#!/usr/bin/env pwsh
#Requires -Version 7
<#
    .SYNOPSIS
    Runs native, non-UI runtime gates for the host macOS architecture: a
    packaged `dt`/`dt-pty-host` smoke test plus targeted unit tests against
    the packaged Ghostty and PTY-host native libraries.

    .PARAMETER PackageDir
    Directory containing the packaged zip(s) (defaults to artifacts/packages).
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$PackageDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:LC_ALL = 'C'

$scriptDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $scriptDir
$metadataPath = Join-Path $repoRoot 'macos/package.env'
if (-not $PackageDir) {
    $PackageDir = Join-Path $repoRoot 'artifacts/packages'
}

Import-Module (Join-Path $scriptDir 'MacOsPackagingCommon.psm1') -Force

Assert-Darwin -Message 'Native macOS runtime validation requires Darwin.'
$machine = (& uname -m)
$rid = switch ($machine) {
    'arm64' { 'osx-arm64' }
    'x86_64' { 'osx-x64' }
    default { throw "Unsupported macOS architecture: $machine" }
}
if (-not (Test-Path -LiteralPath $PackageDir -PathType Container)) {
    throw "macOS package directory not found: $PackageDir"
}
$PackageDir = (Resolve-Path -LiteralPath $PackageDir).ProviderPath

$metadata = Import-MacOsPackageEnv -Path $metadataPath
Assert-Command -Name 'ditto', 'dotnet'

$work = $env:WT_MACOS_TEST_ROOT
if (-not $work) {
    $work = Join-Path $repoRoot "artifacts/macos-runtime-$PID"
}
if (Test-Path -LiteralPath $work) {
    Remove-Item -LiteralPath $work -Recurse -Force
}
New-Item -ItemType Directory -Force -Path (Join-Path $work 'home') | Out-Null

$previousHome = $env:HOME
$env:HOME = Join-Path $work 'home'
Remove-Item Env:\WT_BASE_SETTINGS_PATH, Env:\DTERM_SETTINGS_PATH, Env:\WT_DOTNET_SETTINGS_PATH -ErrorAction SilentlyContinue

try {
    function Get-OneArtifact {
        param([Parameter(Mandatory)][string]$Pattern)
        $foundFiles = @(Get-ChildItem -LiteralPath $PackageDir -Filter $Pattern -File |
            Sort-Object -Property Name)
        if ($foundFiles.Count -ne 1) {
            throw "Expected exactly one $Pattern in $PackageDir; found $($foundFiles.Count)."
        }
        return $foundFiles[0].FullName
    }

    $zipPackage = Get-OneArtifact -Pattern "*-$rid.zip"
    & (Join-Path $scriptDir 'Test-MacOsPackage.ps1') $rid $zipPackage
    if ($LASTEXITCODE -ne 0) {
        throw "Test-MacOsPackage.ps1 failed with exit code $LASTEXITCODE."
    }

    $extract = Join-Path $work 'extracted'
    New-Item -ItemType Directory -Force -Path $extract | Out-Null
    Invoke-Native -FilePath ditto -ArgumentList '-x', '-k', $zipPackage, $extract
    $app = Get-ChildItem -LiteralPath $extract -Recurse -Depth 1 -Directory -Filter '*.app' |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $app) {
        throw 'Extracted zip has no app bundle.'
    }
    $macosDir = Join-Path $app 'Contents/MacOS'

    function Invoke-DtSmokeTest {
        param([Parameter(Mandatory)][string]$Label)

        $cli = Join-Path $macosDir $metadata.CLI_NAME
        $help = (& $cli --help) -join "`n"
        if ($LASTEXITCODE -ne 0 -or ($help -notmatch 'dt - Devolutions Terminal')) {
            throw "$Label dt --help did not report the expected banner."
        }

        $errorPath = Join-Path $work 'parser-error'
        & $cli --macos-invalid-option *> $errorPath
        $status = $LASTEXITCODE
        if ($status -eq 0) {
            throw "$Label dt accepted an invalid parser option."
        }
        if ($status -ne 2) {
            throw "$Label dt returned $status instead of 2 for invalid input."
        }
        if (-not (Select-String -LiteralPath $errorPath -Pattern "Unknown command '--macos-invalid-option'." -SimpleMatch -Quiet)) {
            throw "$Label dt did not report the expected parser error."
        }

        $ptyHost = Join-Path $macosDir $metadata.PTY_HOST_NAME
        $helperErrorPath = Join-Path $work 'pty-helper-error'
        & $ptyHost *> $helperErrorPath
        $helperStatus = $LASTEXITCODE
        if ($helperStatus -eq 0) {
            throw "$Label dt-pty-host accepted missing launch arguments."
        }
        if ($helperStatus -ne 64) {
            throw "$Label dt-pty-host returned $helperStatus instead of 64 for missing arguments."
        }
        if (-not (Select-String -LiteralPath $helperErrorPath -Pattern 'usage: dt-pty-host' -SimpleMatch -Quiet)) {
            throw "$Label dt-pty-host did not report the expected usage error."
        }

        Write-Host "NativeAOT dt startup and parser passed for $Label."
    }

    Invoke-DtSmokeTest -Label (Split-Path -Leaf $zipPackage)

    function Invoke-DotnetTest {
        param(
            [Parameter(Mandatory)][string]$Project,
            [Parameter(Mandatory)][string]$Filter
        )
        Invoke-Native -FilePath dotnet -ArgumentList @(
            'test', (Join-Path $repoRoot "tests/$Project/$Project.csproj"),
            '-c', 'Release', '--nologo', '--verbosity', 'minimal', '--filter', $Filter
        )
    }

    function Invoke-PackagedNativeTest {
        param(
            [Parameter(Mandatory)][string]$Project,
            [Parameter(Mandatory)][string]$NativeName,
            [Parameter(Mandatory)][string]$PackagedNative,
            [Parameter(Mandatory)][string]$Filter
        )
        $projectFile = Join-Path $repoRoot "tests/$Project/$Project.csproj"
        Invoke-Native -FilePath dotnet -ArgumentList @(
            'build', $projectFile, '-c', 'Release', '--nologo', '--verbosity', 'minimal'
        )
        $assembly = Get-ChildItem -LiteralPath (Join-Path $repoRoot "tests/$Project/bin/Release") `
            -Recurse -File -Filter "$Project.dll" | Select-Object -First 1
        if (-not $assembly) {
            throw "Could not locate the $Project test output."
        }
        $destination = Join-Path $assembly.DirectoryName $NativeName
        Copy-Item -LiteralPath $PackagedNative -Destination $destination -Force
        $sourceMode = [System.IO.File]::GetUnixFileMode($PackagedNative)
        $mode = if (($sourceMode -band [System.IO.UnixFileMode]::UserExecute) -ne 0) {
            [System.IO.UnixFileMode]'UserRead, UserWrite, UserExecute, GroupRead, GroupExecute, OtherRead, OtherExecute'
        }
        else {
            [System.IO.UnixFileMode]'UserRead, UserWrite, GroupRead, OtherRead'
        }
        [System.IO.File]::SetUnixFileMode($destination, $mode)
        Invoke-Native -FilePath dotnet -ArgumentList @(
            'test', $projectFile, '-c', 'Release', '--no-build', '--no-restore',
            '--nologo', '--verbosity', 'minimal', '--filter', $Filter
        )
    }

    Invoke-DotnetTest -Project 'Devolutions.Terminal.Cli.Tests' `
        -Filter 'FullyQualifiedName~Devolutions.Terminal.Cli.Tests.CliParserTests'
    Invoke-DotnetTest -Project 'Devolutions.Terminal.Core.Tests' `
        -Filter 'FullyQualifiedName~Devolutions.Terminal.Core.Tests.VtParserTests'
    Invoke-PackagedNativeTest -Project 'Devolutions.Terminal.Ghostty.Tests' -NativeName 'libghostty-vt.dylib' `
        -PackagedNative (Join-Path $macosDir $metadata.GHOSTTY_LIBRARY) `
        -Filter 'FullyQualifiedName~Devolutions.Terminal.Ghostty.Tests.GhosttyTerminalEngineTests'
    Invoke-PackagedNativeTest -Project 'Devolutions.Terminal.Connection.Tests' -NativeName 'dt-pty-host' `
        -PackagedNative (Join-Path $macosDir $metadata.PTY_HOST_NAME) `
        -Filter 'FullyQualifiedName~Devolutions.Terminal.Connection.Tests.LinuxPtyConnectionTests'
    Invoke-DotnetTest -Project 'Devolutions.Terminal.Broker.Tests' `
        -Filter 'FullyQualifiedName~Devolutions.Terminal.Broker.Tests.BrokerTests.ConcurrentClientsAreServedBySinglePrimary'
    Invoke-DotnetTest -Project 'Devolutions.Terminal.Settings.Tests' `
        -Filter 'FullyQualifiedName~Devolutions.Terminal.Settings.Tests.DynamicProfileGeneratorTests.MacOsShellsUseMacOsSourceAndZsh|FullyQualifiedName~Devolutions.Terminal.Settings.Tests.LinuxRuntimeEnvironmentTests'

    Write-Host "Native macOS non-UI runtime validation passed on $machine."
}
finally {
    $env:HOME = $previousHome
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
