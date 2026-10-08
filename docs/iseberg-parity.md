# Original Windows PowerShell ISE feature comparison

DT's source-integrated Iseberg in an explicitly enabled managed developer build
already covers the main edit/run/debug workflow. Default NativeAOT builds and
release packages disable Iseberg; saved profile settings are preserved.
The integration now matches additional portable ISE features, but this is **not a
drop-in replacement for the original `$psISE` object model, WPF add-ons, or
Windows PowerShell 5.1**. Several features exist in the imported workbench but
are deliberately disabled by DT; exact interaction and advanced endpoint
combinations still require comparison.

This report compares the installed original ISE implementation, its documented
contracts, and the actual DT integration, not the standalone Iseberg application.
The initial audit identified the gaps below; subsequent licensed-source
adaptations implemented the portable subset recorded in this report. See
[integration and deployment](iseberg.md) for ownership and deployment details.

## Scope, provenance, and evidence

ILSpy CLI **11.1.0.9782** successfully exported the original managed launcher and
four UI assemblies as local projects: **1,213 C# files and 44 XAML files**.
The editor assembly contains substantial Visual Studio text infrastructure;
these counts are not counts of user features. The decompiled projects were not
built or imported into DT. The Windows PowerShell engine itself was not
decompiled.

| Original component | File version | C# files | XAML files | Responsibility |
| --- | --- | --- | --- | --- |
| `powershell_ise.exe` | 10.0.26100.8875 | 7 | 0 | STA launcher, argument parsing handoff, existing-instance file routing |
| `Microsoft.PowerShell.GPowerShell` | 10.0.26100.8875 | 139 | 20 | ISE workbench, host, execution, debugging, object model, recovery |
| `Microsoft.PowerShell.Editor` | 10.0.26100.7309 | 899 | 8 | Text model, WPF editing, classification, IntelliSense infrastructure |
| `Microsoft.PowerShell.GraphicalHost` | 10.0.26100.1591 | 159 | 16 | Graphical command/help and related Windows UI |
| `Microsoft.PowerShell.ISECommon` | 10.0.26100.1 | 9 | 0 | Shared ISE command-line and instance communication support |

The launcher is under
`C:\Windows\System32\WindowsPowerShell\v1.0`. The four DLLs are under
`C:\Windows\Microsoft.NET\assembly\GAC_MSIL`, with assembly identity
`3.0.0.0`, public key token `31bf3856ad364e35`.
The GUI reference engine reports **Windows PowerShell 5.1.26100.9444**;
assembly file versions are not the engine version. DT uses installed
**PowerShell 7.6.6**, Avalonia **12.1.1**, and AvaloniaEdit **12.0.0**.

Local evidence lives in ignored
`artifacts\iseberg\original-ise-analysis`. Its `manifest.json` records exact
input paths, sizes, SHA-256 digests, tool version, and output counts.
Decompilation used `ilspycmd -p -o <output>` with the local .NET Framework
reference directory. All five exports completed successfully; an error-marker
scan found no decompilation failure markers. This does not prove every
decompiled expression is a perfect reconstruction.

**Microsoft's implementation remains proprietary.** No Microsoft decompiled
source, reconstructed XAML, or original binaries belong in DT's source or
distribution. This report contains feature facts and API names, not copied
implementations. Future matching should use documented contracts and observable
behavior, implemented within the licensed Iseberg/Avalonia components.
Iseberg's MIT provenance and editor notices remain as documented in
[source provenance](iseberg.md#source-provenance).

Official reference:
[About Windows PowerShell ISE](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_windows_powershell_ise?view=powershell-5.1).

### Coverage labels

| Label | Meaning |
| --- | --- |
| **G** | The stated workflow was exercised in both original ISE and DT. This is not exhaustive feature parity. |
| **S** | An implementation is present in the inspected source; the full original workflow was not newly GUI-tested. |
| **P** | A substantive subset exists, but behavior, API shape, or host wiring differs. |
| **D** | Implementation exists in imported Iseberg but DT deliberately disables it. |
| **M** | No corresponding implementation/wiring was found in the inspected integration. |
| **U** | Parity remains unverified; dependency capability alone is not proof. |
| **I** | Intentional architectural or runtime difference, not a missing portable feature. |

### Source map

Evidence codes in the matrices refer to these concrete implementation surfaces:

| Code | DT / imported source |
| --- | --- |
| `L` | [DT tab creation and closing](../src/Devolutions.Terminal.App/Views/MainWindow.PowerShellIse.cs), [tab host options](../src/Devolutions.Terminal.App/Views/PowerShellIseTab.cs) |
| `R` | `src\Devolutions.Terminal.Ise\Iseberg.Core\PowerShellSession*.cs`, `WorkbenchHost.cs` |
| `C` | [native console adapter](../src/Devolutions.Terminal.App/Connections/IsebergTerminalConnection.cs), [console control bridge](../src/Devolutions.Terminal.App/Views/DtIsebergConsole.cs) |
| `E` | [editor control](../src/Devolutions.Terminal.Ise/Iseberg.Editor/PowerShellEditorControl.cs), [analysis](../src/Devolutions.Terminal.Ise/Iseberg.Core/EditorAnalysis.cs), [rendering](../src/Devolutions.Terminal.Ise/Iseberg/EditorRendering.cs) |
| `W` | [workbench editor, commands, help, and snippet actions](../src/Devolutions.Terminal.Ise/Iseberg/WorkbenchControl.axaml.cs) |
| `F` | [persistence/recovery integration](../src/Devolutions.Terminal.Ise/Iseberg/WorkbenchControl.Persistence.cs), `Iseberg.Core\ScriptRecovery.cs`, `ScriptFile.cs` |
| `N` | [snippet service](../src/Devolutions.Terminal.Ise/Iseberg.Core/IseSnippetService.cs), `Iseberg.Core\SnippetCatalog.cs` |
| `B` | [debugger UI](../src/Devolutions.Terminal.Ise/Iseberg/WorkbenchControl.Debugger.cs), `Iseberg.Core\PowerShellSession.Debugger.cs`, `DebuggerModels.cs` |
| `O` | [ISE scripting wrappers](../src/Devolutions.Terminal.Ise/Iseberg/WorkbenchControl.Scripting.cs) |
| `P` | [DT themes](../src/Devolutions.Terminal.Settings/IsebergThemes.cs), `Iseberg\OptionsWindow.axaml.cs`, `Iseberg.Core\UserSettings.cs` |
| `A` | [editor accessibility peer](../src/Devolutions.Terminal.Ise/Iseberg.Editor/AccessibleTextEditor.cs), installed AvaloniaEdit editing handlers |

Original evidence paths below are relative to the local ignored analysis root.
Namespaces are retained in ILSpy's directory layout. In `GPowerShell`, internal
editor/host types below are under `Microsoft.Windows.PowerShell.Gui.Internal`;
the public scripting types are under `Microsoft.PowerShell.Host.ISE`.

## 1. Launching, runspaces, execution, and native console

Original evidence: `Launcher\Microsoft.Windows.PowerShell.GuiExe.Internal\GPowerShell.cs`,
`Microsoft.PowerShell.ISECommon\Microsoft.Windows.PowerShell.GuiExe.Internal\ISECommandLineParser.cs`, and
`GPowerShell\Microsoft.Windows.PowerShell.Gui.Internal` host, console,
profile, and execution classes.

| Original feature | Coverage | DT behavior / remaining difference | Evidence |
| --- | --- | --- | --- |
| Open ISE as an editing/execution environment | I | A distinct `powershellIse` profile opens an embedded DT tab, not an external ISE process or ordinary shell profile. | L |
| STA interactive runspace, persistent state | G | Windows STA/reused thread; script and native console share the same engine. Variables survive successive commands. | R, C |
| F5 saved-script execution | G | Executes in the owning runspace; saved-file execution and reopen were compared. | R, W |
| F8 selection/current-line execution | G | Both supported; common current-line behavior was compared. | W, R |
| Stop, then execute another command | G | Cancellation/stop returns the session to usable input. | R, C, L |
| Pipeline, arithmetic, Unicode output | G | Common language/output cases matched, not every formatting or encoding scenario. | R, C |
| Host input such as `Read-Host` | G | Native console returns input to the owning engine. | R, C |
| Secure input, credentials, typed and choice prompts | S | Implemented host/dialog paths; each prompt form still needs dedicated comparison. | R |
| History, editable input, incomplete commands, multiline paste | S / P | Native editing and history retain complete multiline entries; bracketed paste is one undo edit. Exact original vertical-navigation semantics are not claimed. | C |
| Explicit Shift+Enter multiline editing | S | Inserts a newline without execution through the native input contract. | C |
| Console syntax coloring, popup IntelliSense, descriptions | S / P | Parser-colored VT input and a revision-guarded engine popup with descriptions are wired to the native console and its preferences. Not every original completion interaction is equivalent. | C, W |
| Console undo/redo/cut as a text editor | S | Input selection, cut/copy, bounded undo/redo and paste work through the console bridge; secure prompt text is excluded from public input/history APIs. | C, W |
| F1 / Show-Command for the active console command | S | Active native input exposes text/caret context to the workbench handlers. | C, W |
| Profiles and execution policy | I | Profile loading is opt-in and uses DT's Iseberg host profile, not Microsoft's ISE-specific profile. Execution remains unsandboxed and subject to the installed engine's policy. | R, L |
| Multiple local/remote ISE session tabs | D / I | Nested session creation is host-gated off; DT owns outer tabs and isolates their engines. | L, W |
| Enter/exit an interactive remote runspace | S | Pushed-runspace/remoting support exists. Hiding New Remote Tab does not remove `Enter-PSSession` support; endpoint/platform combinations remain unverified. | R |
| Original `-MTA`, `-NoProfile`, file arguments and existing-instance file routing | I / M | DT owns application startup/profile dispatch. No equivalent original ISE command-line/file-routing contract was found for this profile. | L |
| Windows PowerShell 5.1-specific workflows/modules | I | PowerShell 7 is not a compatibility clone of 5.1. Availability of legacy modules, Windows graphical cmdlets, COM/WPF tooling, and workflow syntax must be assessed separately. | R |

Both original ISE and Iseberg have raw-host limitations. The original
`GPSHostRawUserInterface` itself rejects `ReadKey`, buffer reads, and several
cursor/window operations. Do not describe raw console limitations as a feature
that the original universally supports. Some original stored cursor/buffer/title
properties do differ from the rewrite.

## 2. Script editing and completion

Original evidence: `GPowerShell`'s `PowerShellTokenizationService.cs`,
`PowerShellErrorTagger.cs`, `PowerShellErrorTaggerProvider.cs`, `SmartIndent.cs`,
and the XML classification/outlining services;
`Microsoft.PowerShell.Editor\Microsoft.VisualStudio.Text.Operations.Implementation\EditorOperations.cs`.

| Original feature | Coverage | DT behavior / remaining difference | Evidence |
| --- | --- | --- | --- |
| PowerShell token coloring, brace matching/navigation | S | Engine/parser-aware classification and brace navigation are implemented. | E, W |
| Parser diagnostics | S / P | Inline diagnostic rendering and diagnostic hover supplement the diagnostics text. Exact original marker presentation is not claimed. | E, W |
| Script IntelliSense and candidate acceptance | P | Engine completion, replacement spans, filtering, and popup acceptance exist. Original candidate selection/Tab acceptance and DT popup rendering were checked separately, not as a complete identical interaction suite. | E, W |
| Readable completion descriptions | S | DT's scoped description styling was GUI-verified. Original parent-window screenshots did not capture its separate WPF popup, so visual parity is not established. | E, P |
| Line numbers, wrap, font size, tabs/spaces | S | Existing editor/options support remains available. | E, W, P |
| Search/replace, navigation, undo/redo, cut/copy/paste | S | Provided by the editor/workbench; not every shortcut or search-option combination was compared. | E, W, A |
| Indentation | P | Basic indentation is inherited. Original `SmartIndent` copies the preceding nonempty line's indentation; it is not an AST-based formatter. Exact blank-line/selection behavior still needs comparison. | E, A |
| Case conversion and rectangular selection | S / U | AvaloniaEdit includes case-conversion commands and rectangular selection support. Their availability must not be classified as missing just because `$psISE.Editor` lacks wrappers; original bindings/UX were not tested. | A |
| Colored/rich clipboard copy | S | Copy/cut enrich the existing payload with escaped, palette-colored HTML, retaining plain text and selection metadata. RTF/original application-specific formats are not promised. | A, W |
| Curly-brace outlining | S | Multiline curly-brace folds are built and displayed. | E, W |
| `#region`, multiline comment/string outlining | S | Nested regions and multiline comment/string folds complement brace folds. | E |
| Dedicated XML / `.ps1xml` editing | S / P | Dedicated diagnostics, classification and folds are selected by document path; DTD/external resource resolution is prohibited. Not the original Visual Studio XML service. | E |
| Accessibility, IME, RTL editing | U | Accessible editor name/value support exists; the custom peer does not itself implement a text-range provider. Full screen-reader, IME, RTL, and native-console parity was not exercised. | A, C |

## 3. Documents, recovery, and snippets

Original evidence: `GPowerShell`'s `AutoSaveManager.cs`,
`Microsoft.PowerShell.Host.ISE\ISEFile*.cs`, and snippet classes.

| Original feature | Coverage | DT behavior / remaining difference | Evidence |
| --- | --- | --- | --- |
| New/open/save/save-as; multiple script documents | G / S | Save As, close/reopen, and execution were compared; other file/encoding/conflict paths are source-backed. | W, F, O |
| Unsaved-close Save/Discard/Cancel | G | Cancel preserves the document and tab/window. DT's two-phase host close also preserves ownership across multiple tabs. | L, W |
| Recovery copies after crash / restart | S / P | Per-workspace recovery, exclusive storage leases and same-profile abandoned-workspace discovery are enabled. Forced cleanup retains dirty snapshots; approved closing removes them. | L, F, W |
| Restore layout/session | P | Stable DT workspace identity restores documents/options and offers dirty recovery into a fresh engine; runspace variables are not serialized. | L, F |
| Recent files and durable workbench geometry/options | S / P | DT-owned per-workspace persistence is enabled; it is not the original global ISE settings store. | L, F, P |
| Remote file editing/debug-source opening | S | Remote runspace ownership and open/read/save paths exist; dedicated remote end-to-end comparison remains necessary. | R, W, `WorkbenchControl.Remoting.cs` |
| Snippet XML, insertion, caret/indent expansion, export | S | Catalog, engine snippet service, expansion, and file transfer UI exist. | N, W |
| UI Create/Import using the host-owned snippet folder | S | Both save actions now use the selected engine's configured DT snippet directory, matching reload ownership. | N, W, L |

Recovery uses DT workspace/profile identities and storage leases rather than
enabling standalone global persistence. Script text remains sensitive local
data, and recovery is not a backup or serialization of a live runspace.

## 4. Debugging, inspection, and progress

Original evidence: `GPowerShell` debugger/source-location classes,
`Microsoft.PowerShell.Host.ISE\ISEEditor.cs`, and
`Microsoft.Windows.PowerShell.Gui.Internal\ProgressSource.cs`.

| Original feature | Coverage | DT behavior / remaining difference | Evidence |
| --- | --- | --- | --- |
| Breakpoint, Step Over, Continue | G | The common interactive debugging sequence was compared. | B, R, W |
| Step Into/Out, Break All, paused evaluation | S | Implemented; not a complete debugger command/edge-case comparison. | B, R |
| Line/command/variable breakpoints; conditions/actions; enable/delete | S | Existing engine/debugger models support these forms. | B, R |
| Variables, watches, call stack, object inspection | S | Available in the enabled debugger pane; watches and paged object inspection are useful rewrite features, not proof of original UX equivalence. | B |
| Exact paused statement extent / column breakpoint | P | Clean-source parser extents are used only when line/column starts match; otherwise highlighting falls back to the line. Original column breakpoint contracts remain unsupported. | B, E |
| Variable value on editor hover | S / P | Hover reads the selected paused-frame snapshot with source/session/frame/lifetime invalidation. Arbitrary expression evaluation and unsupported scoped/provider variables are not guessed. | B, E, W |
| Remote debugging / attached process combinations | U | Source support does not establish parity across WinRM/SSH endpoints, engine editions, or process attachment. | B, R |
| Nested/concurrent progress records | S / P | Source-isolated activity/parent hierarchy, multiple record text and completed-subtree cleanup are retained. The UI uses one selected-record bar rather than one bar per activity. | R, W |

## 5. `$psISE` and add-on compatibility

Original evidence: `GPowerShell\Microsoft.PowerShell.Host.ISE\ObjectModelRoot.cs`,
`PowerShellTab.cs`, `ISEFileCollection.cs`, `ISEEditor.cs`, `ISEOptions.cs`,
and their tab/add-on/snippet collection classes. DT evidence: `O`.

| Original contract | Coverage | Compatibility difference |
| --- | --- | --- |
| Current tab/file and file/editor access | P | Useful wrappers exist, but they do not constitute the complete original contract. |
| Root `Options`, visible add-on tool properties | P / I | Portable validated Options are implemented; WPF tool controls remain unsupported. |
| `CurrentPowerShellTab.Files.SelectedFile` | S | File-collection selection alias is implemented with ownership checks. |
| Mutable tab `DisplayName` | S | Mutable display name retains the session's persistence identity. |
| Tab `ConsolePane`, status/expanded-script/commands/zoom surface | P | Portable status/zoom/Commands properties are implemented; this is not the entire original console/tool API. |
| `PowerShellTabs.Add/Remove`, `Invoke/InvokeSynchronous` | M / I | Wrappers explicitly throw unsupported exceptions. Any matching must preserve DT tab ownership, initialization, cancellation, and error handling. |
| File editing/saving and add-on menu actions/shortcuts | S | Existing wrappers provide useful portable functionality. |
| `Editor.CanGoToMatch`, `GoToMatch`, `ToggleOutliningExpansion` | S | Portable brace navigation and folding scripting members are wired. |
| Original snippet metadata shape | P | Provenance/fragment metadata is exposed; not every original collection mutator is reproduced. |
| Observable collections / original events | P | Collection/property notifications and disposal invalidation are added; original event shapes are not universally identical. |
| WPF horizontal/vertical add-on tools | I | Explicitly unsupported. Arbitrary Windows WPF controls cannot become cross-platform Avalonia controls through an API alias. |

These selected documented members are implemented within the licensed rewrite,
without importing Microsoft code. Unsupported behavior throws explicitly.
Legacy profiles/add-ons are not drop-in compatible merely because Options,
selection aliases and events are available.

## 6. Commands, help, themes, and settings

Original evidence: `Microsoft.PowerShell.GraphicalHost` command/help UI and
`GPowerShell\Microsoft.PowerShell.Host.ISE\ISEOptions.cs`.

| Original feature | Coverage | DT behavior / remaining difference | Evidence |
| --- | --- | --- | --- |
| Docked Commands/module browser | S | Host gate is enabled; initially hidden and user-controlled. | L, W |
| Show-Command parameter forms | S | Menu/dialog and engine host request paths still exist. Disabling the Commands dock does **not** disable Show-Command. | W, R |
| F1 local/online command help | S | Help viewers/online launching use the active editor or native-console context. | W |
| Update Help menu workflow | P | The PowerShell command remains available; the original dedicated menu workflow is not matched. | W, R |
| Original token/color preferences | P / I | Imported custom token/theme functionality exists, but DT disables color management when it owns the palette. | P, L |
| Persistent theme selection | S / I | Six original palette combinations and legacy Classic/Dark/Light/Follow DT are available in DT Profile appearance. The profile font wins; standalone theme-manager recommendations remain supported. | P, L |
| Other persistent Options | S / P | Profile-owned editor/recovery/completion settings are edited in DT Settings. Workspace geometry, view state and transient scripting changes remain scoped; this is not original global-user preference storage. | F, P |
| Native-console IntelliSense preferences | S | Automatic completion, timeout and Enter-acceptance preferences are wired to the native popup. | C, W, P |
| Localization | P | Imported workbench has an English resource catalog; this is not original ISE's localized resource coverage. | `Iseberg\Resources\Strings.resx`, `UiText.cs` |
| Print preview | S | Rewrite has an HTML/browser preview path. This is not treated as a missing original feature: original printing parity was not established. | F |

## 7. Implemented scope and boundaries

The portable implementation repairs snippet ownership, enables leased
workspace recovery/options, extends native input/completion/help, adds editor
and paused-debug details, extends a targeted `$psISE` subset, enables Commands
docking, and retains progress hierarchy. All six original built-in theme names
are available alongside DT's legacy choices.

Exact original binary/window behavior, WPF plug-ins, cross-workbench scripting
operations, column breakpoints, advanced remoting combinations, full
accessibility equivalence and 5.1 engine compatibility remain outside this
verified portable subset.
There is no meaningful single "parity percentage" across these categories.

## Validation boundary

The final integration passes **1,570 tests, 0 failed, 9 skipped** across the
complete affected App, UI, Settings, Settings.Editor and Connection projects.
The nine skips are Unix PTY tests on this Windows host, not Iseberg tests.
The [final requirement evidence](iseberg.md#final-windows-integration-evidence)
records exact project totals, representative test names, GUI checks and
distribution gates. These results supersede the earlier 171/647-test scoped
baselines; they are not a claim that every original ISE API or workflow has
been reproduced.

Original ISE Cua checks used a test-owned process and verified shared variables,
Read-Host, save/reopen/execute, actual completion selection/acceptance, and
canceling dirty close. Additional common run/stop/debug checks are recorded in
[the integration validation section](iseberg.md#windows-powershell-ise-compatibility-and-validation)
and ignored `artifacts\iseberg\validation-results.json`.

The comparison informed the portable production/editor/console/scripting and
profile changes described above; no proprietary Microsoft implementation was
imported. User-owned DT processes and unsaved documents were preserved.
Linux/macOS packaging, installed Windows package certification, separate WPF
completion-popup visual parity, advanced debugger/remoting cases, and complete
accessibility/editor shortcut equivalence remain unverified.
