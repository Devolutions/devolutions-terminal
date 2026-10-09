# Focused, dependency-free contract tests. Signature/tool responses are simulated;
# real trust and timestamp verification must also be tested with signed artifacts.
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $IsWindows) {
    throw "Windows payload signature regression tests require Windows."
}

$helperPath = Join-Path $PSScriptRoot "Test-WindowsPayloadSignatures.ps1"
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("devolutions-terminal-signatures-" + [guid]::NewGuid())
$appPath = Join-Path $testRoot "Devolutions.Terminal.exe"
$aliasPath = Join-Path $testRoot "dt.exe"
$modulePath = Join-Path $testRoot "Devolutions.Iseberg.PowerShell.dll"
$libraryPath = Join-Path $testRoot "third-party.dll"
$hostPath = Join-Path $testRoot "runtimes\win-arm64\native\arm64\OpenConsole.exe"
$expectedPublisher = "CN=Fixture Publisher"
$testState = @{
    Publisher = $expectedPublisher
    SignatureOverrides = @{}
    SignaturePaths = [Collections.Generic.List[string]]::new()
    ToolCalls = [Collections.Generic.List[object]]::new()
    ToolExitCode = 0
}

function Get-AuthenticodeSignature {
    param([string] $LiteralPath)

    $testState.SignaturePaths.Add($LiteralPath)
    $subject = if ([IO.Path]::GetFileName($LiteralPath) -in @("Devolutions.Terminal.exe", "dt.exe", "Devolutions.Iseberg.PowerShell.dll")) {
        $testState.Publisher
    } else {
        "CN=Third-party Publisher"
    }
    $signature = [pscustomobject]@{
        Status = "Valid"
        StatusMessage = "Simulated signature result."
        SignerCertificate = [pscustomobject]@{ Subject = $subject }
        TimeStamperCertificate = [pscustomobject]@{ Subject = "CN=Timestamp Publisher" }
    }
    if ($testState.SignatureOverrides.ContainsKey($LiteralPath)) {
        foreach ($key in $testState.SignatureOverrides[$LiteralPath].Keys) {
            $signature.$key = $testState.SignatureOverrides[$LiteralPath][$key]
        }
    }
    return $signature
}

function winapp {
    $testState.ToolCalls.Add(@($args))
    Set-Variable -Name LASTEXITCODE -Value $testState.ToolExitCode -Scope 1
    "Simulated SignTool result."
}

function Assert-Rejected {
    param(
        [scriptblock] $Action,
        [string] $MessagePattern
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notmatch $MessagePattern) {
            throw "Unexpected failure: $($_.Exception.Message); expected pattern '$MessagePattern'."
        }
        return
    }
    throw "Validation unexpectedly succeeded; expected pattern '$MessagePattern'."
}

function Test-Case {
    param(
        [string] $Name,
        [scriptblock] $Action
    )

    $testState.SignatureOverrides = @{}
    $testState.SignaturePaths.Clear()
    $testState.ToolCalls.Clear()
    $testState.ToolExitCode = 0
    & $Action
    Write-Host "PASS: $Name"
}

try {
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($hostPath)) -Force | Out-Null
    foreach ($path in @($appPath, $aliasPath, $modulePath, $libraryPath, $hostPath)) {
        [IO.File]::WriteAllBytes($path, [byte[]]@())
    }

    Test-Case "TrustedTimestampedRecursivePayload" {
        & $helperPath -PayloadDirectory $testRoot -RequireIsebergModule -ExpectedPublisher $expectedPublisher
        if ($testState.ToolCalls.Count -ne 5 -or $testState.SignaturePaths.Count -ne 5) {
            throw "All five binaries, including the Iseberg module, nested host and third-party DLL, must be checked."
        }
        foreach ($path in @($appPath, $aliasPath, $modulePath, $libraryPath, $hostPath)) {
            if ($path -notin $testState.SignaturePaths) {
                throw "Missing Authenticode check for '$path'."
            }
            $calls = @($testState.ToolCalls | Where-Object { $_[-1] -eq $path })
            if ($calls.Count -ne 1 -or ($calls[0] -join "|") -cne "tool|signtool|verify|/pa|/all|/v|/tw|$path") {
                throw "Expected exactly one strict SignTool verification for '$path'."
            }
        }
    }
    Test-Case "IsebergModuleIncludedInReleaseSigningInput" {
        $toolPath = Join-Path $testRoot "psign-fixture.ps1"
        @'
$index = [Array]::IndexOf($args, '--input-file-list')
if ($index -lt 0 -or $index -ge $args.Count - 1) {
    throw 'Signing invocation omitted its input file list.'
}
Get-Content -LiteralPath $args[$index + 1] |
    Set-Content -LiteralPath (Join-Path $PSScriptRoot 'psign-input.txt')
Set-Variable -Name LASTEXITCODE -Value 0 -Scope 1
'@ | Set-Content -LiteralPath $toolPath -Encoding utf8
        & (Join-Path $PSScriptRoot "Sign-Packages.ps1") -BinaryDirectory $testRoot -Version "1.2.3.4" `
            -ArtifactSigningEndpoint "https://fixture.invalid" -ArtifactSigningAccountName "fixture" `
            -ArtifactSigningProfileName "fixture" -ArtifactSigningAccessToken "fixture-token" -PsignTool $toolPath
        $signedPaths = @(Get-Content -LiteralPath (Join-Path $testRoot "psign-input.txt"))
        $expectedPaths = @($appPath, $aliasPath, $modulePath, $libraryPath, $hostPath)
        if ($signedPaths.Count -ne $expectedPaths.Count -or
            @(Compare-Object -ReferenceObject $expectedPaths -DifferenceObject $signedPaths).Count -ne 0) {
            throw "Release signing must include every payload binary, including the root Iseberg module."
        }
    }
    foreach ($path in @($appPath, $aliasPath, $modulePath, $hostPath)) {
        Test-Case "UnsignedBinaryRejected-$([IO.Path]::GetFileName($path))" {
            $testState.SignatureOverrides[$path] = @{ Status = "NotSigned"; SignerCertificate = $null }
            Assert-Rejected { & $helperPath -PayloadDirectory $testRoot } (
                [regex]::Escape($path) + ".*Authenticode status 'NotSigned'"
            )
        }
    }
    Test-Case "UntrustedRootRejected" {
        $testState.SignatureOverrides[$appPath] = @{ Status = "NotTrusted" }
        Assert-Rejected { & $helperPath -PayloadDirectory $testRoot } "Authenticode status 'NotTrusted'"
    }
    foreach ($path in @($aliasPath, $modulePath)) {
        Test-Case "MissingTimestampRejected-$([IO.Path]::GetFileName($path))" {
            $testState.SignatureOverrides[$path] = @{ TimeStamperCertificate = $null }
            Assert-Rejected { & $helperPath -PayloadDirectory $testRoot } (
                [regex]::Escape($path) + ".*timestamp certificate is missing"
            )
        }
        Test-Case "PublisherMismatchRejected-$([IO.Path]::GetFileName($path))" {
            $testState.SignatureOverrides[$path] = @{
                SignerCertificate = [pscustomobject]@{ Subject = "CN=Wrong Publisher" }
            }
            Assert-Rejected { & $helperPath -PayloadDirectory $testRoot -ExpectedPublisher $expectedPublisher } (
                [regex]::Escape($path) + ".*expected exact publisher"
            )
        }
    }
    Test-Case "PublisherMatchIsCaseSensitive" {
        Assert-Rejected { & $helperPath -PayloadDirectory $testRoot -ExpectedPublisher $expectedPublisher.ToLowerInvariant() } (
            "expected exact publisher"
        )
    }
    Test-Case "SignToolFailurePropagates" {
        $PSNativeCommandUseErrorActionPreference = $true
        $testState.ToolExitCode = 23
        Assert-Rejected { & $helperPath -PayloadDirectory $testRoot } "SignTool signature validation failed.*exit code 23"
    }
    foreach ($path in @($appPath, $aliasPath)) {
        Test-Case "MissingAppRejected-$([IO.Path]::GetFileName($path))" {
            Remove-Item -LiteralPath $path
            try {
                Assert-Rejected { & $helperPath -PayloadDirectory $testRoot } (
                    "Required Windows app executable '" + [regex]::Escape($path) + "' was not found"
                )
            }
            finally {
                [IO.File]::WriteAllBytes($path, [byte[]]@())
            }
        }
    }
    Test-Case "MissingRequiredIsebergModuleRejected" {
        Remove-Item -LiteralPath $modulePath
        try {
            Assert-Rejected { & $helperPath -PayloadDirectory $testRoot -RequireIsebergModule } (
                "Required Iseberg module '" + [regex]::Escape($modulePath) + "' was not found"
            )
        }
        finally {
            [IO.File]::WriteAllBytes($modulePath, [byte[]]@())
        }
    }
    Test-Case "TerminalOnlyPayloadDoesNotRequireIsebergModule" {
        Remove-Item -LiteralPath $modulePath
        try {
            & $helperPath -PayloadDirectory $testRoot -ExpectedPublisher $expectedPublisher
            if ($testState.SignaturePaths.Count -ne 4 -or $testState.ToolCalls.Count -ne 4) {
                throw "All four terminal-only binaries must still be checked."
            }
        }
        finally {
            [IO.File]::WriteAllBytes($modulePath, [byte[]]@())
        }
    }
    Write-Host "All 17 Windows payload signature regression cases passed."
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
