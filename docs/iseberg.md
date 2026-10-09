# Iseberg workbench profiles

**Iseberg** is an embedded PowerShell ISE-style workbench, not a terminal
command line or a separate application. **It is enabled in default NativeAOT
desktop builds and release packages.** The workbench executes PowerShell in an
owned `pwsh` subprocess through a private managed module shipped with DT.
When shell discovery detects `pwsh`, an **Iseberg** profile appears automatically
in the new-tab menu on Windows, Linux and macOS, including with existing settings.
Its stable generated identity preserves customizations across restarts. Hiding
the profile or disabling its shell-discovery source also hides/removes it; the
ordinary PowerShell profile and your default profile are otherwise unchanged.
Explicit terminal-only builds do not generate Iseberg profiles. Compatibility
validation still happens when the workbench is opened.

To create additional workbench profiles, in **Settings**, select **Add a new
profile**, choose **PowerShell ISE (Iseberg)** under **Profile type**, configure
its name, starting directory and font, then select that
profile from the new-tab menu. Existing PowerShell profiles continue to launch
their normal PTY/ConPTY terminal sessions.

The default profile logo is an original ice cube and terminal prompt contained
within a square terminal tile, with equal transparent margins on all four sides.
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
execution and typed console commands share the same persistent PowerShell
runspace in the child process, including variables, completion, debugger and host input. The lower
pane uses DT's VT renderer, scrollbar, clipboard and terminal interaction
settings, not Iseberg's original transcript editor or a separate console runspace.
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

Install stable **PowerShell 7.4.x, 7.5.x or 7.6.x** (minimum 7.4.6),
with its bundled .NET 8, 9 or 10 runtime, respectively. Use the latest servicing
release in your chosen series. The private module and wire contracts target
.NET 8 and compile against PowerShell 7.4 APIs; DT itself remains .NET 10/NativeAOT.
PowerShell 7.4.0 through 7.4.5 cannot load the patched SDK's
`System.Management.Automation` 7.4.6.500 assembly reference and are rejected up front.
The child architecture does not need to match DT. Make `pwsh` available on PATH, or set
`DT_ISEBERG_PSHOME` to its absolute installation directory before starting DT.
DT checks its version and child-runtime compatibility. A short noninteractive
`pwsh -NoProfile` probe discovers and validates the installation. DT then starts
an owned `pwsh -NoLogo -NoProfile` child and imports the absolute path to its
`Iseberg.PowerShell.dll` binary module. The module creates a custom-host execution
runspace; it does not replace PowerShell's bootstrap console host.

Parser analysis, completion, execution, host input, debugging and portable
`$psISE` proxies run in that child. The NativeAOT parent uses typed, source-generated
JSON over private bidirectional local IPC. Script text is sent as protocol data,
never interpolated into the startup command. Standard output/error are drained
separately from IPC. The module and wire contracts are DT-private and shipped in
lockstep; protocol/build mismatches fail startup instead of guessing compatibility.
The length-prefixed channel mutually authenticates a fresh challenge and the
parent's process identity. Frames are limited to 8 MiB, outstanding requests to
128, and queued outgoing frames to 512. Request overflow is reported explicitly;
outgoing queue overflow disconnects rather than silently dropping notifications.
Independent readers keep reverse host/UI callbacks and cancellation available
while a command, input prompt or debugger stop is waiting.
Terminal-size updates are acknowledged before subsequent execution, so host
RawUI dimensions reflect the latest resize. Initial layout changes are retained
until the authenticated execution session is initialized. Background command-catalog refreshes
wait for running commands to finish rather than reporting a busy-session error.

This prerequisite is lazy: ordinary terminal profiles remain usable without
compatible installed PowerShell. Opening Iseberg without it produces an explicit
DT notification/command error. DT does **not** redistribute the PowerShell SDK
runtime/native dependency graph, which includes dependencies with separate
distribution terms. Compile-time SDK references exclude runtime, native and
content assets; publishing rejects accidental engine payloads.

PowerShell executes with **DT's permissions and is not sandboxed**. Process
isolation protects DT's runtime boundary, not the user's data; scripts can access
the filesystem, network and other processes. There is no unconditional execution-policy
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
Profile identity publication never replaces another owner, even across concurrent
processes. Linux/macOS workspace storage must support hard links for this atomic
publication; unsupported filesystems report a storage error.

Closing a tab/window or quitting DT prompts to stop running commands and
save/discard dirty documents. Cancel leaves the tab alive. Bulk/application
closing prepares every workbench before disposing any, so a later cancellation
does not destroy an earlier approved tab. Forced host cleanup disposes runspaces,
timers, owned child processes and UI subscriptions. UI errors use DT notifications and tracing.

If the child exits or IPC fails, DT keeps open documents and reports that the
session's variables/debugger state were lost. Reopen the tab explicitly to start a
fresh session. DT does not automatically restart PowerShell or replay an execution
whose outcome is ambiguous.

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

The default desktop and release packages use **NativeAOT with Iseberg enabled**.
The PowerShell parser/runspace/debugger requires dynamic code only in the
`Iseberg.PowerShell` module, which is built managed and untrimmed and loaded solely
by installed PowerShell. The parent workbench, contracts and IPC client remain
AOT/trim compatible. The `dt` CLI remains NativeAOT.

```powershell
dotnet publish src/Devolutions.Terminal -c Release -r win-x64 --self-contained
dotnet publish src/Devolutions.Terminal -c Release -r win-x64 --self-contained `
  -p:EnablePowerShellIse=false -o artifacts/terminal-only/win-x64
dotnet run --project src/Devolutions.Terminal
dotnet test Devolutions.Terminal.slnx
```

The first command produces the default NativeAOT desktop with the private module
folder. The second produces an explicit terminal-only NativeAOT desktop. It
retains saved Iseberg profiles but reports the missing feature rather than silently
opening PowerShell terminals; its settings editor does not offer new Iseberg profiles.
Use the exclusion flag consistently for restore, build and test commands,
and restore with `-p:Configuration=Release` before a Release
build with `--no-restore`. Ordinary managed builds remain framework-dependent;
self-contained deployment applies at publish time. Engine/workbench tests are
excluded when the feature is off. CI builds the feature-enabled native desktop
and retains a separate terminal-only configuration gate.
Windows CI runs test projects sequentially (`-m:1`) to avoid concurrent test-host
startup starving short broker deadlines. It retains TRX results and per-test
hang diagnostics with a five-minute hang guard; the complete suite has a separate
120-minute budget because isolated `pwsh` startup is slower on hosted Windows
runners. This does not change individual test assertions or hang deadlines.

Native libraries/helpers and legal notices remain loose for package signing and
license checks. Ship only `Iseberg.PowerShell/Iseberg.PowerShell.dll` as loose
content beside the executable; it is not loaded into DT. Shared contract sources
and source-generated JSON are compiled into this module and separately into
the SMA-free `Iseberg.Contracts` project referenced by the NativeAOT parent.
The shared build-identity target fingerprints the same inputs in both builds.
`Iseberg.Contracts.dll` is a parent build artifact, not a distribution payload.
macOS app staging moves this module folder to `Contents/Resources`, while
`Contents/MacOS` contains only native code.
Keep all `THIRD-PARTY-NOTICES*.txt` files in distributions.

Do not ship the PowerShell engine or CoreCLR dependency graph. Native publication
must also be exercised by opening an Iseberg tab in the published executable;
a successful managed build or module import alone does not validate the parent
AOT boundary. Actual Linux/macOS publishing and packaging require their respective
platform/release environments.

For engine tests on a machine without a compatible installation,
`scripts/Install-IsebergTestPowerShell.ps1` downloads a pinned, SHA-256-verified
portable runtime under `artifacts/test-powershell`. It does not change the system
installation or PATH. Dot-source it to set `DT_ISEBERG_PSHOME` in the caller;
GitHub Actions sets that variable for subsequent steps automatically.
Use `-Version 7.4.6` to check the exact compatibility minimum, `-Version 7.4.20`
for the serviced 7.4 runtime, `-Version 7.5.11` or the default `7.6.6`.
CI runs the managed suites against 7.4.6, 7.5.11 and 7.6.6 on Windows,
Linux and macOS. Old runtimes are isolated compatibility-test fixtures,
not recommended user installations.
**Never package this test-runtime directory with DT.**

### Windows NativeAOT subprocess evidence

The original two-DLL feature-enabled `win-x64` Release publish was exercised as
an actual native desktop, not a managed harness. Both desktop and CLI executables have no CLR
header. Its loose module folder contains only the two DT-owned assemblies; the
publish contains no PowerShell engine/CoreCLR payload.

In isolated, test-owned windows, an editor script produced a computed result,
typed native-console input read and changed its variable, and subsequent editor
execution observed that change in the same child PID. Child assembly paths proved
the published bridge/contract DLLs were loaded by PowerShell 7.6.6/.NET 10.0.12.
Exact-PID parent module enumeration after execution found no SMA, CoreCLR,
hostfxr or hostpolicy. Closing the window normally also terminated its child.

A final fresh publish also verified completion acceptance, `Read-Host`, GUI
Stop followed by execution in the same session, child-driven `$psISE` editor and
line-number changes, and a saved-script breakpoint with Step Over and Continue.
The final parent had 80 loaded modules with no SMA/CoreCLR matches; the child
loaded both bridge assemblies from the published module folder. Killing a
separate test-owned parent during a long-running script caused its exact child
to exit without a child-kill command; that check observed cleanup within 104 ms,
not a general shutdown-time guarantee.

A separate owned-child termination check retained the unsaved document exactly,
displayed explicit session-loss/reopen guidance, and observed no replacement child
or replay across nine samples over approximately 15 seconds. This is bounded
runtime evidence, not an unbounded lifetime claim. Linux/macOS execution and signed
installer certification remain platform-CI/release responsibilities.

The .NET 8 compatibility delivery publish was then exercised through native GUI
automation on the exact minimum **PowerShell 7.4.6/.NET 8.0.10** and the installed
**7.6.6/.NET 10.0.12**. Both passed editor/native-console shared state, accepted
completion, typed `Read-Host`, Stop followed by state-preserving execution,
`$psISE` reverse editor/option callbacks, and saved-script breakpoint, Step Over
and Continue (values 0, 10 and 42). Each retained the same child PID throughout,
loaded the published net8.0 bridge/contracts, and enumerated 80 parent modules
with no SMA/CoreCLR/hostfxr/hostpolicy. All eight owned processes exited through
normal UI closing. An earlier compatibility publish also passed these GUI
workflows on serviced PowerShell 7.4.20/.NET 8.0.31.

### Subprocess regression evidence

The latest complete affected-project runs passed **1,639 tests, 0 failed,
9 skipped** across the affected projects. The skips are existing Unix PTY cases
on Windows; no Iseberg case was skipped.

| Test project | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Devolutions.Terminal.App.Tests | 589 | 0 | 0 |
| Devolutions.Terminal.UI.Tests | 665 | 0 | 0 |
| Devolutions.Terminal.Settings.Tests | 202 | 0 | 0 |
| Devolutions.Terminal.Settings.Editor.Tests | 64 | 0 | 0 |
| Devolutions.Terminal.Connection.Tests | 119 | 0 | 9 |

The App suite includes persistent regressions for fragmented/coalesced frames,
Unicode, invalid/truncated lengths, the exact request/queue limits, correlated
reverse callbacks, in-flight cancellation, authentication/build/process identity
mismatches and replay rejection. Process tests cover independent sessions, raw
stdout/stderr separation, child loss, cancellation and owned-child disposal.
The complete UI suite exercises the subprocess-backed editing, debugger, host
input, command forms, snippets, scripting proxies and recovery behaviors.
The final App suite passed on 7.4.6, 7.4.20, 7.5.11 and installed 7.6.6;
the final complete UI suite passed on the exact 7.4.6 minimum. A 50-iteration
transport regression covers cancellation overlapping handler cleanup and
connection disposal; cancellation/removal is synchronized so a completed
handler's disposed token source cannot cause unexpected session loss.
CLI, compatibility and package suites separately passed 34 tests.

`SingleAssemblyModuleAuthenticatesAndExecutesWithoutContractsDll` copies only
the module into an isolated directory, completes the authenticated handshake,
executes with persistent state and parses incomplete input. It verifies that
the child contracts belong to `Iseberg.PowerShell`, with no separate contracts
assembly referenced or loaded, and that the parent remains SMA-free. This
regression passes within all four complete App runs above; the single-DLL
minimum-runtime UI run passes all 665 cases.

Automatic profile regressions cover Windows/Linux/macOS discovery, one stable
generated identity even with multiple PowerShell installations, absence when
`pwsh` is missing, disabling the shared discovery source, and retaining user
overrides and the existing default profile through settings serialization.
The complete Settings suite passes 202 cases; the terminal-only discovery
subset passes 31 cases and verifies that no Iseberg profile is generated.

The complete Release solution builds with zero warnings/errors, and the default
Release NativeAOT desktop/CLI publish succeeds. Positive and negative publish
guard checks accept the single owned module and reject SMA, CoreCLR,
unexpected bridge dependencies (including a separate contracts DLL) or a missing
module. MSI component generation preserves the single module at
`<INSTALLLOCATION>/Iseberg.PowerShell/Iseberg.PowerShell.dll`. Actual desktop
compiler references exclude SMA and the managed module. These checks are
separate from the published native GUI evidence above.

## Windows PowerShell ISE compatibility and historical validation

The GUI comparison and test totals below describe the earlier **in-process,
managed** integration. They document the behavioral baseline, not validation of
the subprocess/NativeAOT implementation.

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

### Historical managed Windows integration evidence

The Iseberg-enabled source passed **1,570 tests, 0 failed, 9 skipped** across the complete
affected projects. The nine skips are the existing Unix PTY cases, which require
Linux/macOS; no Iseberg case is skipped.

| Test project | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Devolutions.Terminal.App.Tests | 533 | 0 | 0 |
| Devolutions.Terminal.UI.Tests | 664 | 0 | 0 |
| Devolutions.Terminal.Settings.Tests | 190 | 0 | 0 |
| Devolutions.Terminal.Settings.Editor.Tests | 64 | 0 | 0 |
| Devolutions.Terminal.Connection.Tests | 119 | 0 | 9 |

These are successful fresh Debug `dotnet test` runs with Iseberg enabled, not
sums of overlapping filtered runs. These totals predate the subprocess migration.
Each project was run with `-p:SkipNativeRestore=true`,
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
| Iseberg.Core | Parent session client, files and settings models (engine/host extracted into the child module) |
| Iseberg.Contracts | SMA-free parent contracts project; canonical contract sources and build identity also compiled directly into the child module |
| Iseberg.PowerShell | Managed binary module, custom PowerShell host, parser, execution and debugger |
| Iseberg.Editor | Engine-independent AvaloniaEdit editor, accessibility, markers and editing commands |
| Iseberg | Embeddable WorkbenchControl, dialogs, models and scoped resources |

Standalone App, MainWindow, Program, update/runtime startup and standalone
application themes were not adopted. Local adaptations add DT starting
directories and host profile names, two-phase cancelable closing, DIP font
sizes, scoped theme presets and completion popup resources, the DT VT console
adapter, hidden-session action gates, and the
installed-engine discovery and subprocess bridge in DT's connections layer. DT remains on Avalonia
12.1.1; AvaloniaEdit is 12.0.0 and the compile-time PowerShell SDK is 7.4.20.

Iseberg is **MIT**, copyright (c) 2026 Adam Driscoll. Its license is retained in
[`docs/licenses/iseberg.txt`](licenses/iseberg.txt) and emitted as
`THIRD-PARTY-NOTICES-ISEBERG.txt`. The copied upstream
[editor notices](licenses/iseberg-editor.md) describe upstream's editor
distribution and are emitted as `THIRD-PARTY-NOTICES-ISEBERG-EDITOR.txt`.
DT also includes
the full AvaloniaEdit/AvalonEdit MIT notices, including AvaloniaUI, AvalonEdit
Contributors and AlphaSierraPapa/SharpDevelop attribution, in
[`docs/licenses/avaloniaedit.txt`](licenses/avaloniaedit.txt), emitted as
`THIRD-PARTY-NOTICES-AVALONIAEDIT.txt`.
Other DT dependency notices and their own licenses still apply.
