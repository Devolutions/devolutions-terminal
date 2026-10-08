# Iseberg workbench profiles

**Iseberg** is an embedded PowerShell ISE-style workbench, not a terminal
command line or a separate application. In **Settings**, select **Add a new
profile**, choose **PowerShell ISE (Iseberg)** under **Profile type**, configure
its name, starting directory and font, then select that
profile from the new-tab menu. Existing PowerShell profiles continue to launch
their normal PTY/ConPTY terminal sessions.

The default profile logo is an original ice cube over a terminal prompt.
`assets/profile-icons/iseberg.svg` is its vector source; the derived high-resolution
PNG uses DT's existing profile-icon loader without adding an SVG runtime
dependency. New profiles, icon-less Iseberg profiles and the earlier default
ISE glyph use this logo; custom icons and ordinary PowerShell icons are retained.
`scripts/Build-IsebergIcon.ps1` regenerates the PNG with the managed SkiaSharp
assembly and matching native library from a DT build supplied as parameters.

Each Iseberg tab owns a script editor, DT-native `TermControl` console, documents and
PowerShell runspace. F5 runs the script, F8 runs the selection/current line, and
the Stop button interrupts execution. Variables persist across executions in
that tab. The imported workbench also provides completion, script save/open,
debugging/breakpoints and snippets. **Tools / Options** opens the owning profile
in DT's existing Settings tab, rather than another preferences window. Nested
workbench session tabs and remote-session creation are hidden: DT owns tabs.

The editor sits above the terminal in a resizable horizontal split. Editor
execution and typed console commands share the same in-process PowerShell
runspace, including variables, completion, debugger and host input. The lower
pane uses DT's VT renderer, scrollbar, clipboard and terminal interaction
settings, not Iseberg's original transcript editor or another `pwsh` process.
Document tabs and workbench chrome use compact desktop UI typography independently
of code font and zoom. The toolbar wraps in narrow DT windows, long document
titles truncate with a full-title tooltip, and the status/zoom bar stays compact.
The native console converts workbench DIP font sizes to DT's point-based renderer,
so editor and console glyphs use the same effective size rather than enlarging the
console by a second points-to-DIPs conversion.
Multiline paste is held until Enter; Shift+Enter inserts a newline and history
recall retains complete multiline commands. Native input supports selection,
cut/copy, bounded undo/redo, syntax coloring, and an engine-backed completion
popup with descriptions. Completion preferences apply to this console as well
as the script editor. Ctrl+C stops execution or cancels host input.

**Native-program limitation:** this host is not a PTY. Redirected native output
is displayed, but interactive native programs and raw-host ReadKey/cursor/buffer
APIs are not fully supported. Use an ordinary terminal profile for interactive
native applications.

## Color themes

**Profile appearance** offers the six original
ISE built-in names: **Dark Console, Light Editor (default)**, **Light Console,
Dark Editor**, **Dark Console, Dark Editor**, **Light Console, Light Editor**,
**Monochrome Green**, and **Presentation**. The configured profile font is
honored even when an original palette is selected. Original font recommendations
remain available in the reusable workbench's standalone theme manager. The independently
adapted palettes preserve the editor/console combinations and representative
token/stream colors, not every original classification or pixel.

**Classic ISE**, **Dark**, **Light**, and **Follow DT** remain supported for
existing profiles. Classic ISE is the default white editor/blue console.
Follow DT tracks DT's light/dark theme; other presets scope chrome, dialogs,
editor, completion descriptions, and terminal colors to the workbench without
changing DT's application theme. Profile changes apply to newly opened tabs.
Fonts, palettes, editor/recovery options and completion preferences are owned by
the profile JSON. **Profile advanced** contains ISE completion, outlining, help,
snippet and toolbar settings. These profile values override stale recovered
workspace preferences without replacing recovered documents, caret positions,
zoom or pane geometry. Transient view changes and scripting Options changes
remain local to the current workspace; custom palette editing/import remains
disabled in the DT host.

## Installed PowerShell prerequisite

Install **PowerShell 7.6.6 or newer within 7.6.x**, using .NET 10.0 and the
**same architecture as DT**. Make `pwsh` available on PATH, or set
`DT_ISEBERG_PSHOME` to its absolute installation directory before starting DT.
DT checks its version, runtime and architecture, and loads that installed engine
in-process. It does not start an external Iseberg desktop or use `pwsh` as the
workbench console process. A short noninteractive `pwsh -NoProfile` probe
discovers and validates the installation.

This prerequisite is lazy: ordinary terminal profiles remain usable without
compatible installed PowerShell. Opening Iseberg without it produces an explicit
DT notification/command error. DT does **not** redistribute the PowerShell SDK
runtime/native dependency graph, which includes dependencies with separate
distribution terms. Compile-time SDK references exclude runtime, native and
content assets; publishing rejects accidental engine payloads.

PowerShell executes with **DT's permissions and is not sandboxed**. Runspace
isolation is not process/security isolation; scripts can access the filesystem,
network and process-wide state. There is no unconditional execution-policy
bypass. **Load PowerShell profile scripts** is opt-in and defaults to off.
The current-host profile is `Devolutions.Terminal.Iseberg_profile.ps1`; normal
all-host profile scripts also run when enabled. Per-profile elevation, terminal
command lines, connection types and process environment overrides are unsupported.
Start DT itself elevated if an elevated workbench is necessary.

## Persistence and lifecycle

The explicit JSON discriminator is independent of the profile's display name:

```json
{
  "guid": "{ff522154-bcf4-47ec-b8a7-6c0201e5e8f0}",
  "name": "Iseberg",
  "type": "powershellIse",
  "commandline": "",
  "startingDirectory": "%USERPROFILE%",
  "ise": {
    "loadProfiles": false,
    "colorTheme": "Classic ISE",
    "showLineNumbers": true,
    "wordWrap": false,
    "promptToSaveBeforeRun": true,
    "autoSaveMinutes": 2,
    "consoleIntelliSense": true,
    "consoleCompletionOnEnter": true,
    "scriptIntelliSense": true,
    "scriptCompletionOnEnter": true,
    "intelliSenseTimeoutSeconds": 3,
    "showOutlining": true,
    "warnDuplicateFiles": true,
    "useLocalHelp": true,
    "useDefaultSnippets": true,
    "recentFileCount": 10,
    "showToolbar": true
  },
  "font": { "face": "Cascadia Code", "size": 12 }
}
```

The optional `ise` object extends the existing Windows Terminal-shaped profile;
identity, directory, icon, font and tab metadata keep their established fields.
Earlier dotted `ise.loadProfiles` / `ise.colorTheme` keys are still read and
updated when present; nested values take precedence. Unknown JSON properties,
including unknown `ise` members, survive editing. Older Iseberg profiles without
a color theme use Classic ISE. Unknown theme
names produce an explicit error instead of silently substituting a palette.
Profiles without `type` remain terminal profiles; their serialized shape does
not change. Unknown type strings are retained and rejected, not launched as
shells. Iseberg is unavailable in the browser and terminal-only hosts, where
saved ISE profiles are preserved but ISE creation/conversion is not offered.

Changing an existing Terminal profile to ISE retains its identity and common
fields. Terminal-only command, elevation, connection and environment settings
move into inactive `ise.terminal` configuration and are restored if the type is
changed back. They are not applied to the ISE runspace; explicit incompatible
ISE launch overrides still fail. **Defaults** edits `profiles.defaults`, not the
first or previously selected named profile.

DT persists profile and whole-tab layout descriptors. Version 2 layouts still
accept version 1 terminal layouts. Duplicate, reopen and layout restoration
create a **fresh runspace**; variables and live engine state are never serialized.
Workspace restoration can reopen saved documents and offer abandoned dirty
document recovery. New launches offer recovery only for the same profile and
never take over a live workspace. Iseberg tabs cannot split, zoom terminal panes
or tear off into other windows. Save scripts explicitly; recovery is not a
replacement for saving or backups.
Automatic same-profile recovery discovery requires a saved, nonempty profile
identity; anonymous profiles do not share recovery candidates.

Closing a tab/window or quitting DT prompts to stop running commands and
save/discard dirty documents. Cancel leaves the tab alive. Bulk/application
closing prepares every workbench before disposing any, so a later cancellation
does not destroy an earlier approved tab. Forced host cleanup disposes runspaces,
timers and UI subscriptions. UI errors use DT notifications and tracing.

DT owns the host window, application theme, fonts, tabs and settings location.
Standalone global settings and update checks remain disabled. Each workspace
uses `PowerShellIse/Workspaces/<workspace-id>/settings.json`, its workbench state
and recovery snapshots under DT's settings directory. An exclusive lease
prevents concurrent writers. The autosave preference controls recovery timing;
forced cleanup retains the latest dirty snapshot, while approved save/discard
closing removes it. Recovery contains script text and should be treated as
sensitive local data; it is not encrypted.

Shared snippets live in `PowerShellIse/Snippets`; Create and Import write through
the selected engine's host-owned directory. Commands docking is available but
hidden initially. Progress retains source/activity/parent identities and shows
multiple records, rather than flattening every update into one activity.

## Portable editing and scripting contracts

The adapted editor adds region, multiline comment/string, and XML folds,
dedicated `.xml`/`.ps1xml` analysis, inline parser diagnostics with hover text,
and escaped, palette-colored HTML clipboard data alongside plain text.
XML analysis prohibits DTD/external resource resolution. Debugger hover reads
the selected paused frame's variable snapshot; it does not evaluate arbitrary
expressions. Paused statement highlighting uses a matching clean-source parser
extent, with a whole-line fallback when that extent cannot be established.

The portable `$psISE` layer includes Options/preferences, file-collection
selection, mutable tab display names, status/zoom/Commands properties,
brace/outlining editor methods, snippet provenance metadata, and observable
notifications. Preference validation, ownership, UI-thread dispatch and
persistence errors are explicit. This is a supported subset, not the complete
Microsoft object model: WPF add-on controls, cross-workbench tab creation/removal
and original Invoke/InvokeSynchronous contracts remain unsupported.

## Build and distribution

The default desktop includes Iseberg and publishes **self-contained, single-file,
managed and untrimmed**. The full PowerShell parser/runspace/debugger requires
dynamic code, so it cannot run in NativeAOT. The `dt` CLI remains NativeAOT.

```powershell
dotnet publish src/Devolutions.Terminal -c Release -r win-x64 --self-contained
dotnet publish src/Devolutions.Terminal -c Release -r win-x64 --self-contained `
  -p:EnablePowerShellIse=false -o artifacts/terminal-only/win-x64
```

The second command produces the terminal-only NativeAOT variant. It retains
saved Iseberg profiles but reports the missing feature rather than silently
opening PowerShell terminals. Publishing an Iseberg-enabled build with trimming
or NativeAOT explicitly enabled fails before compiling project dependencies.

Native libraries/helpers and legal notices remain loose for package signing and
license checks. macOS packaging supports the default managed single-file layout
or terminal-only NativeAOT, not an arbitrary loose managed application layout.
Keep all `THIRD-PARTY-NOTICES*.txt` files in distributions.

For engine tests on a machine without the matching installation,
`scripts/Install-IsebergTestPowerShell.ps1` downloads a pinned, SHA-256-verified
portable runtime under `artifacts/test-powershell`. It does not change the system
installation or PATH. Dot-source it to set `DT_ISEBERG_PSHOME` in the caller;
GitHub Actions sets that variable for subsequent steps automatically.
**Never package this test-runtime directory with DT.**

## Windows PowerShell ISE compatibility and validation

Microsoft's original Windows PowerShell ISE uses Windows PowerShell 5.1 and the
`Windows PowerShell ISE Host`; DT's workbench uses installed PowerShell 7.6.6 and
the `Iseberg` host. Compare common PowerShell behavior, not engine-version or
host-object identity. Windows PowerShell-only modules and scripts may need the
usual changes for PowerShell 7.

The [part-by-part original ISE comparison](iseberg-parity.md) records a local
ILSpy audit of the original launcher and four UI assemblies. It distinguishes
verified workflows, source-backed capabilities, DT-disabled features, remaining
compatibility gaps, and runtime differences; it does not claim full ISE parity
or import Microsoft's implementation.

Actual Windows GUI comparison verified matching saved-script arithmetic,
pipeline and Unicode results, F8 current-line execution, Stop followed by another
execution, and breakpoint/Step Over/Continue behavior. Cua Driver also verified
both directions of editor/console variable sharing, Read-Host input, native
Save As and disk reopen/execute, and canceling an unsaved window close without
losing its document in original ISE. These workflows also passed in DT.
Original ISE completion selection/acceptance was verified separately; DT
verified its four theme presets and readable, theme-scoped completion
descriptions. The managed
single-file distribution executed an editor script using installed PowerShell;
an ordinary profile separately executed in a ConPTY-backed `ConsoleHost`.

Original ISE's WPF text providers ignored background ValuePattern writes, but
Cua Driver's snapshot-targeted native background keys delivered editor/console
input successfully without foreground escalation. Completion was verified from
the actual candidate selection and resulting editor text; the parent-window
capture did not include the separate WPF completion popup, so this does not
claim visual-description parity or full UI parity. Actual
Linux/macOS packaging and installed Windows package certification still require
their respective platform/release environments.

### Final Windows integration evidence

The final source passes **1,570 tests, 0 failed, 9 skipped** across the complete
affected projects. The nine skips are the existing Unix PTY cases, which require
Linux/macOS; no Iseberg case is skipped.

| Test project | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Devolutions.Terminal.App.Tests | 533 | 0 | 0 |
| Devolutions.Terminal.UI.Tests | 664 | 0 | 0 |
| Devolutions.Terminal.Settings.Tests | 190 | 0 | 0 |
| Devolutions.Terminal.Settings.Editor.Tests | 64 | 0 | 0 |
| Devolutions.Terminal.Connection.Tests | 119 | 0 | 9 |

These are successful fresh Debug `dotnet test` runs, not sums of overlapping
filtered runs. Each project was run with `-p:SkipNativeRestore=true`,
`-p:UseArtifactsOutput=true` and the same absolute `ArtifactsPath`, sequentially
to avoid shared build-output collisions. Exact results are retained locally in
ignored `artifacts\iseberg\portable-final-results`: `final-App.trx`,
`final-UI.trx`, `final-Settings.trx`, `final-Settings.Editor.trx` and
`final-Connection.trx`. Older filtered runs in that directory are historical,
not part of these totals.

| Requirement | Representative passing evidence |
| --- | --- |
| Standard profile creation, reversible conversion, identity and terminal compatibility | `IseProfileHarmonizationTests.StandardProfileTypeConversionPreservesTerminalConfigurationAcrossSaveReloadAndSwitchBack`; `NormalAddProfileOffersIseTypeAndSidebarUsesLogoWithoutSeparateCreationButton` |
| Defaults, canonical nested JSON, legacy aliases, unknown fields and option bounds | `DefaultsEditsActualDefaultsAndProfileSelectionFollowsIntoAppearance`; `NestedOptionsOverrideLegacyKeysAndAdvancedEditsPreserveUnknownFields`; `NumericOptionBoundariesSurviveSaveReload` |
| Shared editor/native-console variables and independent outer tabs | `IsebergTerminalBridgeTests.ScriptAndNativeConsoleSharePersistentRunspace`; `MainWindowPowerShellIseTests.DuplicateAndReopenRetainIseAndUseIndependentWorkbenches` |
| Persistent profile-owned preferences and DT Options routing | `IseProfilePreferenceTests.ProfilePreferencesOverrideStaleWorkspaceAppearanceAndOptionsRoutesToHostWithoutAnotherDialog` |
| Original palettes, Follow DT behavior and completion-description contrast | `IsebergTerminalBridgeTests.OriginalBuiltInThemesRenderNativeTerminalColorsAndRemainFixedAcrossHostChanges`; `OriginalBuiltInThemesRenderCompletionAndDescriptionWithinScriptScope`; `NativeConsolePaletteUsesSelectedThemeAndTracksOnlyFollowDt` |
| Ordered recovery, caret/selection/session restoration and storage failures | `PowerShellIseTabTests.RecoverRestoresSavedCaretOffsetsForEveryRecoveredDocument`; `RecoverRestoresSavedDocumentSelection`; `RecoveryStorageFailureKeepsPublishedDraftAndOriginalSnapshotForRetry`; `PortableIseWorkbenchTests.RecoverRoutesSameNamedDocumentsToSavedSessionsAndRestoresActiveSession` |
| Cancelable actual close routes, live ownership and deterministic cleanup | `MainWindowPowerShellIseTests.ActualCloseEntryPointsCancelAndRetainPreferencesRecoveryLeasesSelectionAndNativeRuntimes`; `PowerShellIseTabTests.FailedHostedInitializationReleasesLeaseImmediatelyWithoutWaitingForDispose`; `PortableIseWorkbenchTests.DisposalDetachesRetainedScriptingModelsWithoutExplicitSessionRemoval` |
| Native continuation and atomic invalid-completion rejection | `IsebergNativeContinuationTests.IncompleteEnterContinuesAndCompletedDraftExecutesOnce`; `BracketedLeadingLfAfterEnterIsNotMistakenForCrLfContinuation`; `IsebergTerminalBridgeTests.NativeCompletionRejectsOverflowAndEndSpanAtomicallyPreservingBothEditStacks` |
| Rich copy, contextual local help and retained progress hierarchy | `PortableIseWorkbenchTests.ActualHeadlessClipboardCopyCarriesOnlySelectedPlainTextAndRichHtml`; `F1LocalHelpUsesEnclosingCommandAcrossCommandArgumentNestedAndPipelineContexts`; `PortableIseProgressContinuationTests.WorkbenchCloseAndDisposalClearProgressAndRejectAlreadyQueuedDelivery` |
| Licensed source, host boundaries and deployment gates | Source provenance below; desktop `ValidatePowerShellIsePublish` and `ValidateInstalledPowerShellBoundary` targets; successful managed/NativeAOT publications |

Cua Driver verified normal Add profile/type selection, saved canonical JSON,
Defaults editing and Tools/Options routing in a test-owned application. The final
managed single-file publish executed an editor script and emitted
`DT-FINAL-PUBLISHED:42` in its embedded DT-native console using installed
PowerShell 7.6.6. A separate ordinary PowerShell profile emitted
`DT-FINAL-NORMAL:42` in the same final binary. The test-owned application then
closed normally through its unsaved-script prompt; only the explicitly created
test script was discarded. User-owned applications and unsaved documents were
not used for these checks.

The final desktop Debug build completes with zero warnings/errors. Both the
managed Iseberg desktop with NativeAOT CLI and terminal-only NativeAOT desktop
publish successfully for `win-x64`; both CLI `--help` processes exit zero.
Explicit Iseberg-enabled AOT/trimming requests fail with the intended
dynamic-code diagnostic. Required MIT/editor notices remain in the managed
publish, and the installed PowerShell runtime is not redistributed.

Failure-path corrections retain recovery order/selection/caret, avoid publishing
released ownership during cancelable close preparation, unwind failed startup
before a recovery draft is published, and keep a partially recovered draft and
its lease until explicit cleanup when storage fails. Clipboard enrichment uses
AvaloniaEdit 12's supported `TextCopied` event rather than its inactive legacy
copying event. Collection moves/resets preserve active wrappers and detach
removed sessions; disposed controls reject queued progress delivery.

## Source provenance

The local module is `src/Devolutions.Terminal.Ise`, imported from
[mamoreau-devolutions/iseberg](https://github.com/mamoreau-devolutions/iseberg)
at revision **`d2e723f4f8ed4e08db28a89f09a783e40aed001c`**:

| Local project | Imported responsibility |
| --- | --- |
| Iseberg.Core | PowerShell engine/host, execution, completion, debugger, files and settings models |
| Iseberg.Editor | Engine-independent AvaloniaEdit editor, accessibility, markers and editing commands |
| Iseberg | Embeddable WorkbenchControl, dialogs, models and scoped resources |

Standalone App, MainWindow, Program, update/runtime startup and standalone
application themes were not adopted. Local adaptations add DT starting
directories and host profile names, two-phase cancelable closing, DIP font
sizes, scoped theme presets and completion popup resources, the DT VT console
adapter, hidden-session action gates, and the
installed-engine resolver in DT's connections layer. DT remains on Avalonia
12.1.1; AvaloniaEdit is 12.0.0 and the compile-time PowerShell SDK is 7.6.6.

Iseberg is **MIT**, copyright (c) 2026 Adam Driscoll. Its license is retained in
the module and emitted as `THIRD-PARTY-NOTICES-ISEBERG.txt`. The copied upstream
`EDITOR-NOTICES.md` describes upstream's editor distribution; DT also includes
the full AvaloniaEdit/AvalonEdit MIT notices, including AvaloniaUI, AvalonEdit
Contributors and AlphaSierraPapa/SharpDevelop attribution, in
`AVALONIAEDIT-LICENSE.txt` / `THIRD-PARTY-NOTICES-AVALONIAEDIT.txt`.
Other DT dependency notices and their own licenses still apply.
