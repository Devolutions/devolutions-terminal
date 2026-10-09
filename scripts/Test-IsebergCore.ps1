#Requires -Version 7.4
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# Keep inexpensive editor/transport coverage and representative hosted workflows,
# not the exhaustive workbench, theme, and native-input permutations.
$tests = [ordered]@{
    'Devolutions.Terminal.App.Tests' = @(
        'PortableIseInstallationTests.'
        'PortableIseHandshakeTests.'
        'PortableIseProtocolTests.'
        'PortableIseProcessTests.'
        'PortableIseCoreTests.PersistenceLeaseContentionPreservesFirstOwnerAndLockBytes'
        'PortableIseCoreTests.ReleasedRecoveryPreservesDirtyDraftFields'
        'PortableIseCoreTests.LiveRecoveryRecordsOwnProcessButIsNotEligibleUntilReleased'
        'PortableIseCoreTests.DirtySavedRecoveryRecordsAbsolutePathAndCurrentEncoding'
        'PortableIseCoreTests.CleanSaveRemovesOnlyOwnSnapshot'
        'PowerShellIseWorkspaceCatalogTests.ConcurrentProfileRecordsPublishOneOwnerAndNeverRebind'
        'PowerShellIseWorkspaceCatalogTests.SameProfileReleasedWorkspaceIsFoundWithoutConsumingOrRewritingData'
    )
    'Devolutions.Terminal.UI.Tests' = @(
        'PortableIseEditorTests.'
        'IsebergTerminalBridgeTests.ScriptAndNativeConsoleSharePersistentRunspace'
        'IsebergTerminalBridgeTests.NativeReadHostAcceptsResponseAndKeepsRunspaceUsable'
        'IsebergTerminalBridgeTests.NativeCtrlCStopsRunningCommandAndRestoresInput'
        'IsebergTerminalBridgeTests.MultilinePasteWaitsForExplicitEnter'
        'IsebergTerminalBridgeTests.ResizeUpdatesRawUiColumnsWithoutReplacingEngine'
        'IsebergNativeContinuationTests.ParenthesizedContinuationCompletedAfterFirstEnterExecutesExactlyOnce'
        'IsebergNativeContinuationTests.CompleteInvalidInputPublishesOneParseErrorWithoutEnteringContinuation'
        'PowerShellIseTabTests.DirtyCloseCancellationPreservesDocumentAndEngine'
        'MainWindowPowerShellIseTests.SavedIseProfileLaunchesAndCapturesRealWorkbenchTab'
        'PortableIseWorkbenchTests.ExplicitRecoverySavesExactDocumentsSelectionCaretAndReleasedOwnership'
        'PortableIseWorkbenchTests.RecoverRoutesSameNamedDocumentsToSavedSessionsAndRestoresActiveSession'
    )
    'Devolutions.Terminal.Settings.Tests' = @(
        'PowerShellIseProfileTests.IseDiscriminatorAndExplicitOptInResolve'
        'PowerShellIseProfileTests.AddedIseProfileRoundTripsKindOptionsAndIdentity'
        'PowerShellIseProfileTests.UnknownProfileTypeIsUnsupportedWithError'
        'PowerShellIseProfileTests.NewIseProfileUsesIsebergNameAndDistinctIdentity'
        'PowerShellIseProfileTests.TerminalProfilesAndOverridesRemainTerminal'
        'PowerShellIseProfileTests.IseThemeDefaultsToClassicWithoutAddingThemeToLegacyTerminal'
        'PowerShellIseProfileTests.UnknownIseThemeIsPreservedWithoutDefaultOrTerminalFallback'
    )
    'Devolutions.Terminal.Settings.Editor.Tests' = @(
        'IseProfileHarmonizationTests.'
    )
}

$elapsed = [Diagnostics.Stopwatch]::StartNew()
Push-Location (Join-Path $PSScriptRoot '..')
try {
    foreach ($selection in $tests.GetEnumerator()) {
        $project = $selection.Key
        $filter = ($selection.Value | ForEach-Object { "FullyQualifiedName~$project.$_" }) -join '|'
        $projectPath = Join-Path 'tests' (Join-Path $project "$project.csproj")
        $resultsDirectory = Join-Path (Join-Path 'artifacts' 'test-results') $project
        $resultsPath = Join-Path $resultsDirectory 'iseberg-core.trx'
        if (Test-Path -LiteralPath $resultsPath) {
            Remove-Item -LiteralPath $resultsPath
        }

        dotnet test $projectPath -c Release -m:1 `
            -p:EnablePowerShellIse=true --filter $filter `
            --logger 'console;verbosity=normal' --logger 'trx;LogFileName=iseberg-core.trx' `
            --results-directory $resultsDirectory `
            --blame-hang --blame-hang-timeout 5min --blame-hang-dump-type none

        [xml]$results = Get-Content -LiteralPath $resultsPath -Raw
        if ([int]$results.TestRun.ResultSummary.Counters.executed -eq 0) {
            throw "The Iseberg core filter did not execute any tests in $project."
        }
    }
    Write-Host "Iseberg core suite completed in $($elapsed.Elapsed)."
}
finally {
    Pop-Location
}
