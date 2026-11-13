#Requires -Version 7
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workflow = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../.github/workflows/build-terminal.yml') -Raw

function Get-WorkflowRunBlock {
    param([string] $Section, [string] $StepName)

    $lines = $Section -split '\r?\n'
    $step = [Array]::IndexOf($lines, "      - name: $StepName")
    if ($step -lt 0) { throw "Workflow step '$StepName' was not found." }
    for ($index = $step + 1; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -eq '        run: |') { break }
        if ($lines[$index].StartsWith('      - name:')) { throw "Step '$StepName' has no script." }
    }
    $body = [Collections.Generic.List[string]]::new()
    for ($index++; $index -lt $lines.Count; $index++) {
        $line = $lines[$index]
        if ($line.Length -eq 0) { $body.Add(''); continue }
        if (-not $line.StartsWith('          ')) { break }
        $body.Add($line.Substring(10))
    }
    if ($body.Count -eq 0) { throw "Step '$StepName' has no script." }
    $body -join "`n"
}

$metadataScript = [scriptblock]::Create(
    (Get-WorkflowRunBlock -Section $workflow -StepName 'Resolve tag, versions, and signing environment'))
$macOsSection = ($workflow -split '(?m)^  macos-sign:\r?$')[1] -split '(?m)^  linux-packages:\r?$' | Select-Object -First 1
$macOsScript = Get-WorkflowRunBlock -Section $macOsSection -StepName 'Resolve signing mode'
$macOsNugetSection = ($workflow -split '(?m)^  nuget-macos:\r?$')[1] -split '(?m)^  msi:\r?$' | Select-Object -First 1
if (-not $macOsNugetSection.Contains('REQUIRE_SIGNATURE: ${{ needs.release-metadata.outputs.sign_macos }}')) {
    throw 'Packaged macOS NuGet verification must enforce the same production signing policy.'
}

$appleSecrets = @('APPLE_APP_DEV_ID_APP_CERTIFICATE', 'APPLE_APP_DEV_ID_APP_CERTIFICATE_PASSWORD', 'APPLE_BOT_PASSWORD')
$environmentNames = @(
    'REQUESTED_VERSION', 'DRY_RUN', 'SIGN_DRY_RUN', 'REQUESTED_ENVIRONMENT',
    'GITHUB_REF_TYPE', 'GITHUB_REF_NAME', 'GITHUB_EVENT_NAME', 'GITHUB_RUN_NUMBER', 'GITHUB_OUTPUT'
) + $appleSecrets
$originalEnvironment = @{}
foreach ($name in $environmentNames) {
    $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
$outputPath = Join-Path ([IO.Path]::GetTempPath()) "terminal-signing-policy-$([guid]::NewGuid().ToString('N')).txt"

function Get-StepOutputs {
    $outputs = @{}
    foreach ($line in Get-Content -LiteralPath $outputPath) {
        $parts = $line -split '=', 2
        $outputs[$parts[0]] = $parts[1]
    }
    $outputs
}

try {
    $env:GITHUB_OUTPUT = $outputPath
    $env:GITHUB_RUN_NUMBER = '123'
    foreach ($case in @(
        @{ Name = 'ProductionTagRequiresMacOsSigning'; Event = 'push'; RefType = 'tag'; RefName = 'v2026.3.0'; Environment = 'auto'; Dry = 'false'; SignDry = 'false'; ExpectedEnvironment = 'publish-prod'; Windows = 'true'; MacOs = 'true' },
        @{ Name = 'ProductionDispatchRequiresMacOsSigning'; Event = 'workflow_dispatch'; RefType = 'branch'; RefName = 'master'; Environment = 'prod'; Dry = 'false'; SignDry = 'false'; ExpectedEnvironment = 'publish-prod'; Windows = 'true'; MacOs = 'true' },
        @{ Name = 'AutoMasterDispatchRequiresMacOsSigning'; Event = 'workflow_dispatch'; RefType = 'branch'; RefName = 'master'; Environment = 'auto'; Dry = 'false'; SignDry = 'false'; ExpectedEnvironment = 'publish-prod'; Windows = 'true'; MacOs = 'true' },
        @{ Name = 'ProductionSignedDryRunRequiresMacOsSigning'; Event = 'workflow_dispatch'; RefType = 'branch'; RefName = 'feature'; Environment = 'prod'; Dry = 'true'; SignDry = 'true'; ExpectedEnvironment = 'publish-prod'; Windows = 'true'; MacOs = 'true' },
        @{ Name = 'UnsignedDryRunSkipsMacOsSigning'; Event = 'workflow_dispatch'; RefType = 'branch'; RefName = 'master'; Environment = 'prod'; Dry = 'true'; SignDry = 'false'; ExpectedEnvironment = 'publish-dry-run'; Windows = 'false'; MacOs = 'false' },
        @{ Name = 'TestSignedDryRunKeepsWindowsSigningOnly'; Event = 'workflow_dispatch'; RefType = 'branch'; RefName = 'master'; Environment = 'test'; Dry = 'true'; SignDry = 'true'; ExpectedEnvironment = 'publish-test'; Windows = 'true'; MacOs = 'false' },
        @{ Name = 'TestReleaseSkipsMacOsSigning'; Event = 'workflow_dispatch'; RefType = 'branch'; RefName = 'master'; Environment = 'test'; Dry = 'false'; SignDry = 'false'; ExpectedEnvironment = 'publish-test'; Windows = 'true'; MacOs = 'false' },
        @{ Name = 'OrdinaryMasterBuildSkipsReleaseSigning'; Event = 'push'; RefType = 'branch'; RefName = 'master'; Environment = 'auto'; Dry = 'false'; SignDry = 'false'; ExpectedEnvironment = 'publish-prod'; Windows = 'false'; MacOs = 'false' },
        @{ Name = 'AutoBranchDispatchSkipsMacOsSigning'; Event = 'workflow_dispatch'; RefType = 'branch'; RefName = 'feature'; Environment = 'auto'; Dry = 'false'; SignDry = 'false'; ExpectedEnvironment = 'publish-test'; Windows = 'true'; MacOs = 'false' }
    )) {
        $env:REQUESTED_VERSION = if ($case.RefType -eq 'tag') { '' } else { '2026.3.0' }
        $env:DRY_RUN = $case.Dry
        $env:SIGN_DRY_RUN = $case.SignDry
        $env:REQUESTED_ENVIRONMENT = $case.Environment
        $env:GITHUB_EVENT_NAME = $case.Event
        $env:GITHUB_REF_TYPE = $case.RefType
        $env:GITHUB_REF_NAME = $case.RefName
        [IO.File]::WriteAllText($outputPath, '')
        & $metadataScript
        $outputs = Get-StepOutputs
        if ($outputs.publish_environment -cne $case.ExpectedEnvironment -or
            $outputs.sign_packages -cne $case.Windows -or $outputs.sign_macos -cne $case.MacOs) {
            throw "$($case.Name): unexpected release signing policy: $($outputs | ConvertTo-Json -Compress)"
        }
        Write-Host "PASS: $($case.Name)"
    }

    foreach ($secret in $appleSecrets) { [Environment]::SetEnvironmentVariable($secret, $null) }
    [IO.File]::WriteAllText($outputPath, '')
    & ([scriptblock]::Create($macOsScript.Replace('${{ needs.release-metadata.outputs.sign_macos }}', 'false')))
    if ((Get-StepOutputs).should_sign -cne 'false') { throw 'Non-production macOS packaging must not need Apple credentials.' }
    Write-Host 'PASS: NonProductionMacOsPackagingNeedsNoAppleCredentials'

    $productionScript = [scriptblock]::Create($macOsScript.Replace('${{ needs.release-metadata.outputs.sign_macos }}', 'true'))
    foreach ($missingSecret in $appleSecrets) {
        foreach ($secret in $appleSecrets) { [Environment]::SetEnvironmentVariable($secret, 'fixture') }
        [Environment]::SetEnvironmentVariable($missingSecret, $null)
        [IO.File]::WriteAllText($outputPath, '')
        $rejected = $false
        try { & $productionScript }
        catch {
            if ($_.Exception.Message -notlike "Missing Apple signing/notarization secrets: *$missingSecret*") { throw }
            $rejected = $true
        }
        if (-not $rejected -or (Get-Content -LiteralPath $outputPath -Raw)) {
            throw "Production signing did not fail closed without $missingSecret."
        }
        Write-Host "PASS: ProductionMacOsSigningRejectsMissing-$missingSecret"
    }
    foreach ($secret in $appleSecrets) { [Environment]::SetEnvironmentVariable($secret, 'fixture') }
    [IO.File]::WriteAllText($outputPath, '')
    & $productionScript
    if ((Get-StepOutputs).should_sign -cne 'true') { throw 'Production signing was not enabled with its required credentials.' }
    Write-Host 'PASS: ProductionMacOsSigningEnabledWithCredentials'

    [IO.File]::WriteAllText($outputPath, '')
    $rejected = $false
    try {
        & ([scriptblock]::Create($macOsScript.Replace('${{ needs.release-metadata.outputs.sign_macos }}', '')))
    }
    catch {
        if ($_.Exception.Message -notlike 'Invalid macOS signing policy:*') { throw }
        $rejected = $true
    }
    if (-not $rejected -or (Get-Content -LiteralPath $outputPath -Raw)) {
        throw 'Missing policy metadata must not silently disable macOS signing.'
    }
    Write-Host 'PASS: MacOsSigningRejectsMissingPolicy'
    Write-Host 'Release signing policy regression checks passed.'
}
finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name])
    }
    Remove-Item -LiteralPath $outputPath -ErrorAction SilentlyContinue
}
