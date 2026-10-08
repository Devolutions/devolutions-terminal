using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Devolutions.Terminal.App.Models;
using Devolutions.Terminal.App.Routing;
using Devolutions.Terminal.App.Views;
using Devolutions.Terminal.Settings;
using Xunit;
using static Devolutions.Terminal.UI.Tests.PowerShellIseTestHarness;

namespace Devolutions.Terminal.UI.Tests;

public sealed class MainWindowPowerShellIseTests
{
    [AvaloniaFact]
    public async Task SavedIseProfileLaunchesAndCapturesRealWorkbenchTab()
    {
        await InitializeRuntimeAsync();
        await LaunchCoreAsync();
    }

    [AvaloniaFact]
    public async Task DuplicateAndReopenRetainIseAndUseIndependentWorkbenches()
    {
        await InitializeRuntimeAsync();
        await DuplicateReopenCoreAsync();
    }

    [AvaloniaFact]
    public async Task GroupCancellationReenablesEarlierPreparedWorkbenches()
    {
        await InitializeRuntimeAsync();
        await GroupCancellationCoreAsync();
    }

    [AvaloniaFact]
    public async Task SwitchingTabsRetainsNativeTerminalAndRunspace()
    {
        await InitializeRuntimeAsync();
        await SwitchNativeCoreAsync();
    }

    [AvaloniaFact]
    public async Task SavedIseThemeSurvivesCaptureDuplicateAndReopen()
    {
        await InitializeRuntimeAsync();
        await SavedThemeCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task SavedThemeCoreAsync() => WithWindowAsync(async (window, _) =>
    {
        var original = ActiveIse(window);
        AssertDarkTheme(original);
        var originalDescriptor = Assert.Single(window.CaptureLayout().Tabs).Root.Session!;
        Assert.Equal("Dark", originalDescriptor.IseColorTheme);
        await ActivateAsync(window, ShortcutAction.DuplicateTab);
        var duplicateTab = window.ActiveTab!;
        var duplicate = ActiveIse(window);
        AssertDarkTheme(duplicate);
        var saved = window.CaptureLayout().Tabs.Single(tab => tab.TabId == duplicateTab.Id).Root.Session!;
        Assert.Equal("Dark", saved.IseColorTheme);
        Assert.Equal(originalDescriptor.ProfileId, saved.ProfileId);
        await ActivateAsync(window, ShortcutAction.CloseTab, new CloseTabArgs(IndexOf(window, duplicateTab)));
        Assert.True(duplicate.IsDisposed);
        await ActivateAsync(window, ShortcutAction.RestoreLastClosed);
        var reopened = ActiveIse(window);
        Assert.NotSame(duplicate, reopened);
        AssertDarkTheme(reopened);
        var restored = window.CaptureLayout().Tabs.Single(tab => tab.TabId == duplicateTab.Id).Root.Session!;
        Assert.Equal("Dark", restored.IseColorTheme);
        Assert.Equal(saved.SessionId, restored.SessionId);
        Assert.Equal(saved.ProfileId, restored.ProfileId);
        await SubmitTokenAsync(reopened, "'DT-REOPENED-DARK-THEME'", "DT-REOPENED-DARK-THEME");
        await ExecuteTokenAsync(original, "'DT-ORIGINAL-DARK-THEME'", "DT-ORIGINAL-DARK-THEME");

        static void AssertDarkTheme(PowerShellIseTab content)
        {
            Assert.Equal("Dark", content.Terminal.Profile!.IseColorTheme);
            Assert.Equal(0xFFD4D4D4u, content.Terminal.Engine.Scheme.Foreground);
            Assert.Equal(0xFF1E1E1Eu, content.Terminal.Engine.Scheme.Background);
            Assert.True(content.Terminal.IsRunning);
        }
    }, colorTheme: "Dark");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task SwitchNativeCoreAsync() => WithWindowAsync(async (window, _) =>
    {
        var originalTab = window.ActiveTab!;
        var original = ActiveIse(window);
        var terminal = original.Terminal;
        var connection = NativeConnection(original);
        var session = Assert.Single(original.Workbench.Workbench.Sessions);
        var runspace = session.Engine.LocalRunspaceId;
        await SubmitTokenAsync(original, "$global:dtNativeSwitch = 'retained'; 'DT-SWITCH-BEFORE'", "DT-SWITCH-BEFORE");
        await ActivateAsync(window, ShortcutAction.OpenSettings, new OpenSettingsArgs(SettingsTarget.SettingsUI));
        Assert.True(window.ActiveTab!.IsSettingsTab);
        Assert.False(original.IsDisposed);
        Assert.True(terminal.IsRunning);
        await ActivateAsync(window, ShortcutAction.SwitchToTab, new SwitchToTabArgs(IndexOf(window, originalTab)));
        Assert.Same(originalTab, window.ActiveTab);
        Assert.Same(original, ActiveIse(window));
        Assert.Same(terminal, ActiveIse(window).Terminal);
        Assert.Same(window, Avalonia.Controls.TopLevel.GetTopLevel(terminal));
        Assert.Same(connection, NativeConnection(original));
        Assert.Same(session, Assert.Single(original.Workbench.Workbench.Sessions));
        Assert.Equal(runspace, session.Engine.LocalRunspaceId);
        await SubmitTokenAsync(original, "'DT-SWITCH-SETTINGS:' + $global:dtNativeSwitch", "DT-SWITCH-SETTINGS:retained");
        await ActivateAsync(window, ShortcutAction.DuplicateTab);
        var other = ActiveIse(window);
        Assert.NotSame(terminal, other.Terminal);
        await ActivateAsync(window, ShortcutAction.SwitchToTab, new SwitchToTabArgs(IndexOf(window, originalTab)));
        Assert.Same(terminal, ActiveIse(window).Terminal);
        Assert.Same(connection, NativeConnection(original));
        Assert.Equal(runspace, session.Engine.LocalRunspaceId);
        Assert.True(terminal.IsRunning);
        await SubmitTokenAsync(original, "'DT-SWITCH-ISE:' + $global:dtNativeSwitch", "DT-SWITCH-ISE:retained");
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task LaunchCoreAsync() => WithWindowAsync(async (window, environment) =>
    {
        var tab = window.ActiveTab!;
        var content = ActiveIse(window);
        Assert.True(content.Workbench.IsStarted);
        var engineSession = Assert.Single(content.Workbench.Workbench.Sessions);
        Assert.Equal(Iseberg.Core.SessionState.Ready, engineSession.Engine.State);
        Assert.Same(content.Terminal, tab.Panes.ActiveContent!.Control);
        Assert.True(content.Terminal.IsRunning);
        var layout = window.CaptureLayout();
        Assert.Equal(tab.Id, layout.ActiveTabId);
        var saved = Assert.Single(layout.Tabs);
        Assert.True(saved.Root.IsLeaf);
        Assert.Null(saved.Root.First);
        Assert.Null(saved.Root.Second);
        Assert.Null(saved.ZoomedSessionId);
        var session = saved.Root.Session!;
        Assert.Equal(ProfileKind.PowerShellIse, session.Kind);
        Assert.False(session.IseLoadProfiles);
        Assert.Equal(Guid.Parse(environment.ProfileId), Guid.Parse(session.ProfileId!));
        Assert.Equal(tab.Id, saved.TabId);
        Assert.Equal(tab.Panes.ActiveContent.Session.SessionId, session.SessionId);
        Assert.Equal(session.SessionId, saved.ActiveSessionId);
        var reloaded = TerminalLayoutSerializer.DeserializeTabs(TerminalLayoutSerializer.SerializeTabs(layout))!;
        var restored = Assert.Single(reloaded.Tabs);
        Assert.Equal(saved.TabId, restored.TabId);
        Assert.Equal(session.SessionId, restored.Root.Session!.SessionId);
        Assert.Equal(session.ProfileId, restored.Root.Session.ProfileId);
        Assert.Equal(ProfileKind.PowerShellIse, restored.Root.Session.Kind);
        Assert.False(restored.Root.Session.IseLoadProfiles);
        await ExecuteTokenAsync(content, "'DT-WINDOW-LAUNCH'", "DT-WINDOW-LAUNCH");
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task DuplicateReopenCoreAsync() => WithWindowAsync(async (window, _) =>
    {
        var originalTab = window.ActiveTab!;
        var original = ActiveIse(window);
        var originalSession = original.Workbench.Workbench.SelectedSession!;
        await ActivateAsync(window, ShortcutAction.DuplicateTab);
        var duplicatedTab = window.ActiveTab!;
        var duplicated = ActiveIse(window);
        var duplicatedSession = duplicated.Workbench.Workbench.SelectedSession!;
        Assert.NotEqual(originalTab.Id, duplicatedTab.Id);
        Assert.NotEqual(originalTab.Panes.ActiveContent!.Session.SessionId, duplicatedTab.Panes.ActiveContent!.Session.SessionId);
        Assert.NotSame(original.Workbench, duplicated.Workbench);
        Assert.NotSame(originalSession.Engine, duplicatedSession.Engine);
        Assert.NotEqual(originalSession.Engine.LocalRunspaceId, duplicatedSession.Engine.LocalRunspaceId);
        var saved = window.CaptureLayout().Tabs.Single(t => t.TabId == duplicatedTab.Id);
        Assert.Equal(ProfileKind.PowerShellIse, saved.Root.Session!.Kind);
        Assert.False(saved.Root.Session.IseLoadProfiles);
        Assert.Equal(originalTab.Panes.ActiveContent.Session.ProfileId, saved.Root.Session.ProfileId);
        await ExecuteTokenAsync(original, "'DT-ORIGINAL-AFTER-DUPLICATE'", "DT-ORIGINAL-AFTER-DUPLICATE");

        var closedRunspace = duplicatedSession.Engine.LocalRunspaceId;
        await ActivateAsync(window, ShortcutAction.CloseTab, new CloseTabArgs(IndexOf(window, duplicatedTab)));
        Assert.DoesNotContain(duplicatedTab, window.Tabs);
        Assert.Contains(originalTab, window.Tabs);
        Assert.True(duplicated.IsDisposed);
        Assert.Equal(Iseberg.Core.SessionState.Disposed, duplicatedSession.Engine.State);
        Assert.True(window.CanRestoreLastClosedTab);
        await ActivateAsync(window, ShortcutAction.RestoreLastClosed);
        var reopenedTab = window.ActiveTab!;
        var reopened = ActiveIse(window);
        var reopenedSession = reopened.Workbench.Workbench.SelectedSession!;
        Assert.NotSame(duplicatedTab, reopenedTab);
        Assert.Equal(saved.TabId, reopenedTab.Id);
        Assert.NotSame(duplicated.Workbench, reopened.Workbench);
        Assert.NotSame(duplicatedSession.Engine, reopenedSession.Engine);
        Assert.NotEqual(closedRunspace, reopenedSession.Engine.LocalRunspaceId);
        Assert.True(reopened.Workbench.IsStarted);
        var restored = window.CaptureLayout().Tabs.Single(t => t.TabId == saved.TabId);
        Assert.Equal(saved.ActiveSessionId, restored.ActiveSessionId);
        Assert.Equal(saved.Root.Session.SessionId, restored.Root.Session!.SessionId);
        Assert.Equal(saved.Root.Session.ProfileId, restored.Root.Session.ProfileId);
        Assert.Equal(ProfileKind.PowerShellIse, restored.Root.Session.Kind);
        Assert.False(restored.Root.Session.IseLoadProfiles);
        await ExecuteTokenAsync(reopened, "'DT-REOPENED-WORKBENCH'", "DT-REOPENED-WORKBENCH");
        await ExecuteTokenAsync(original, "'DT-ORIGINAL-AFTER-REOPEN'", "DT-ORIGINAL-AFTER-REOPEN");
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task GroupCancellationCoreAsync() => WithWindowAsync(async (window, environment) =>
    {
        var firstTab = window.ActiveTab!;
        var first = ActiveIse(window);
        var firstSession = first.Workbench.Workbench.SelectedSession!;
        await ActivateAsync(window, ShortcutAction.DuplicateTab);
        var secondTab = window.ActiveTab!;
        var second = ActiveIse(window);
        var secondSession = second.Workbench.Workbench.SelectedSession!;
        const string text = "Write-Output 'group-unsaved-content'";
        var document = second.Workbench.CreateDocument(text);
        Assert.True(document.File.IsDirty);
        await ActivateAsync(window, ShortcutAction.OpenSettings, new OpenSettingsArgs(SettingsTarget.SettingsUI));
        var settingsTab = window.ActiveTab!;
        Assert.True(settingsTab.IsSettingsTab);
        var before = window.OwnedWindows.ToArray();
        var request = window.ActivateAsync(Activation(ShortcutAction.CloseOtherTabs)).AsTask();
        var dialog = await NewDialogAsync(window, before, request);
        Assert.False(first.Workbench.IsEnabled);
        Assert.False(first.IsDisposed);
        Assert.Equal(Iseberg.Core.SessionState.Ready, firstSession.Engine.State);
        ClickCancel(dialog);
        var result = await request.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(result.Succeeded, result.Message);
        Assert.Contains(firstTab, window.Tabs);
        Assert.Contains(secondTab, window.Tabs);
        Assert.Same(settingsTab, window.ActiveTab);
        Assert.All(new[] { first, second }, content =>
        {
            Assert.True(content.Workbench.IsEnabled);
            Assert.True(content.Workbench.IsStarted);
            Assert.False(content.IsDisposed);
        });
        Assert.Contains(document, secondSession.Files);
        Assert.Equal(text, document.Document.Text);
        Assert.Equal(Iseberg.Core.SessionState.Ready, secondSession.Engine.State);
        await ExecuteTokenAsync(first, "'DT-GROUP-FIRST-RETAINED'", "DT-GROUP-FIRST-RETAINED");
        await ExecuteTokenAsync(second, "'DT-GROUP-SECOND-RETAINED'", "DT-GROUP-SECOND-RETAINED");
        Assert.True(await second.Workbench.SaveDocumentAsync(document, Path.Combine(environment.DirectoryPath, "group-cancelled.ps1")));
    });

    private static PowerShellIseTab ActiveIse(MainWindow window)
    {
        Assert.NotNull(window.ActiveTab);
        Assert.False(window.ActiveTab.IsSettingsTab);
        Assert.False(window.ActiveTab.IsTerminalTab);
        return Assert.IsType<PowerShellIseTab>(window.ActiveTab.CustomContent);
    }

    private static uint IndexOf(MainWindow window, TerminalTab tab) =>
        (uint)window.Tabs.Select((value, index) => (value, index)).Single(item => ReferenceEquals(item.value, tab)).index;

    private static TerminalWindowActivation Activation(ShortcutAction action, IActionArgs? args = null) =>
        new(null, null, null, null, TerminalWindowLaunchMode.Default, [new ActionAndArgs(action, args)]);

    private static async Task ActivateAsync(MainWindow window, ShortcutAction action, IActionArgs? args = null)
    {
        var result = await window.ActivateAsync(Activation(action, args)).AsTask().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(result.Succeeded, result.Message);
    }

    private static async Task WithWindowAsync(Func<MainWindow, IseTestEnvironment, Task> test,
        string colorTheme = IsebergThemes.Classic)
    {
        using var environment = new IseTestEnvironment();
        environment.SaveDefaultProfile(colorTheme);
        var window = new MainWindow(0, string.Empty, null,
            stateStore: new ApplicationStateStore(environment.DirectoryPath), isolated: true);
        using var diagnostics = new StringWriter();
        using var listener = new TextWriterTraceListener(diagnostics);
        Trace.Listeners.Add(listener);
        Exception? primaryFailure = null;
        try
        {
            window.Show();
            var initial = await window.InitialActivation.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(initial.Succeeded, initial.Message);
            await test(window, environment);
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw new InvalidOperationException($"{error.Message}\nMainWindow trace:\n{diagnostics}", error);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            try
            {
                // Save deliberate dirty content to an absolute fixture path: never a picker.
                foreach (var tab in window.Tabs)
                {
                    if (tab.CustomContent is not PowerShellIseTab content || content.IsDisposed) continue;
                    foreach (var session in content.Workbench.Workbench.Sessions)
                        foreach (var document in session.Files.Where(file => file.File.IsDirty).ToArray())
                            Assert.True(await content.Workbench.SaveDocumentAsync(document,
                                Path.Combine(environment.DirectoryPath, $"{document.RecoveryId:N}.ps1")));
                }
                while (window.Tabs.Count > 0)
                {
                    var tab = window.Tabs[0];
                    var content = tab.CustomContent as PowerShellIseTab;
                    var engines = content?.Workbench.Workbench.Sessions.Select(session => session.Engine).ToArray();
                    await ActivateAsync(window, ShortcutAction.CloseTab, new CloseTabArgs(0));
                    Assert.DoesNotContain(tab, window.Tabs);
                    if (engines is not null)
                        Assert.All(engines, engine => Assert.Equal(Iseberg.Core.SessionState.Disposed, engine.State));
                }
                window.Close();
                Assert.False(window.IsVisible);
            }
            catch (Exception cleanupError) when (primaryFailure is not null)
            {
                primaryFailure.Data["ISE cleanup error"] = cleanupError.ToString();
                Trace.TraceError("ISE cleanup after primary failure: {0}", cleanupError);
            }
        }
    }

    [AvaloniaFact]
    public async Task MainWindowSessionIdentityOwnsRecoveryPathAcrossDuplicateAndReopen()
    {
        await InitializeRuntimeAsync();
        await SessionRecoveryIdentityContractCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task SessionRecoveryIdentityContractCoreAsync() => WithWindowAsync(async (window, environment) =>
    {
        var originalTab = window.ActiveTab!;
        var original = ActiveIse(window);
        var originalSession = Assert.Single(window.CaptureLayout().Tabs).Root.Session!;
        Assert.Equal(originalSession.SessionId, original.WorkspaceId);
        Assert.Equal(Path.Combine(environment.DirectoryPath, "PowerShellIse", "Workspaces",
            originalSession.SessionId.ToString("N")), original.StateDirectory);
        await original.Workbench.SaveRecoveryAsync();
        var originalStatePath = Path.Combine(original.StateDirectory, "settings.json");
        Assert.Throws<IOException>(() => new Iseberg.Core.WorkbenchPersistenceLease(originalStatePath));

        await ActivateAsync(window, ShortcutAction.DuplicateTab);
        var duplicateTab = window.ActiveTab!;
        var duplicate = ActiveIse(window);
        var duplicateSession = window.CaptureLayout().Tabs.Single(tab => tab.TabId == duplicateTab.Id).Root.Session!;
        Assert.NotEqual(original.WorkspaceId, duplicate.WorkspaceId);
        Assert.Equal(duplicateSession.SessionId, duplicate.WorkspaceId);
        Assert.Equal(Path.Combine(environment.DirectoryPath, "PowerShellIse", "Workspaces",
            duplicateSession.SessionId.ToString("N")), duplicate.StateDirectory);
        await duplicate.Workbench.SaveRecoveryAsync();
        var duplicateStatePath = Path.Combine(duplicate.StateDirectory, "settings.json");
        Assert.Throws<IOException>(() => new Iseberg.Core.WorkbenchPersistenceLease(duplicateStatePath));
        await ActivateAsync(window, ShortcutAction.CloseTab, new CloseTabArgs(IndexOf(window, duplicateTab)));
        Assert.True(duplicate.IsDisposed);
        using (var released = new Iseberg.Core.WorkbenchPersistenceLease(duplicateStatePath))
            Assert.Throws<IOException>(() => new Iseberg.Core.WorkbenchPersistenceLease(originalStatePath));

        await ActivateAsync(window, ShortcutAction.RestoreLastClosed);
        var reopened = ActiveIse(window);
        var reopenedSession = window.CaptureLayout().Tabs.Single(tab => tab.TabId == duplicateTab.Id).Root.Session!;
        Assert.Equal(duplicateSession.SessionId, reopenedSession.SessionId);
        Assert.Equal(duplicate.WorkspaceId, reopened.WorkspaceId);
        Assert.Equal(duplicate.StateDirectory, reopened.StateDirectory);
        Assert.Throws<IOException>(() => new Iseberg.Core.WorkbenchPersistenceLease(duplicateStatePath));
        Assert.False(original.IsDisposed);
        Assert.Same(original, Assert.IsType<PowerShellIseTab>(originalTab.CustomContent));
        Assert.Equal(originalSession.SessionId, original.WorkspaceId);
    });

    [AvaloniaTheory]
    [InlineData("Dark Console, Light Editor (default)", 0xFFF5F5F5u, 0xFF012456u, "#000000", "#FFFFFF")]
    [InlineData("Light Console, Dark Editor", 0xFF626262u, 0xFFFFFFFFu, "#F5F5F5", "#012456")]
    [InlineData("Dark Console, Dark Editor", 0xFFF5F5F5u, 0xFF012456u, "#F5F5F5", "#012456")]
    [InlineData("Light Console, Light Editor", 0xFF626262u, 0xFFFFFFFFu, "#000000", "#FFFFFF")]
    [InlineData("Monochrome Green", 0xFF00FF00u, 0xFF000000u, "#00FF00", "#000000")]
    [InlineData("Presentation", 0xFFF5F5F5u, 0xFF000000u, "#000000", "#FFFFFF")]
    public async Task OriginalBuiltInThemesSurviveCaptureDuplicateAndReopenWithIndependentEngines(
        string theme, uint foreground, uint background, string scriptForeground, string scriptBackground)
    {
        await InitializeRuntimeAsync();
        await OriginalThemeLifecycleCoreAsync(theme, foreground, background, scriptForeground, scriptBackground);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task OriginalThemeLifecycleCoreAsync(
        string theme, uint foreground, uint background, string scriptForeground, string scriptBackground) =>
        WithWindowAsync(async (window, _) =>
        {
            var original = ActiveIse(window);
            var originalEngine = original.Workbench.Workbench.SelectedSession!.Engine;
            var first = Assert.Single(window.CaptureLayout().Tabs).Root.Session!;
            Assert.Equal(theme, first.IseColorTheme);
            AssertPalette(original);
            await ActivateAsync(window, ShortcutAction.DuplicateTab);
            var duplicatedTab = window.ActiveTab!;
            var duplicate = ActiveIse(window);
            var duplicateEngine = duplicate.Workbench.Workbench.SelectedSession!.Engine;
            Assert.NotSame(originalEngine, duplicateEngine);
            Assert.NotEqual(originalEngine.LocalRunspaceId, duplicateEngine.LocalRunspaceId);
            AssertPalette(duplicate);
            var saved = window.CaptureLayout().Tabs.Single(tab => tab.TabId == duplicatedTab.Id).Root.Session!;
            Assert.Equal(theme, saved.IseColorTheme);
            Assert.Equal(first.ProfileId, saved.ProfileId);
            duplicate.Workbench.Scripting.Options.Zoom = 125;
            await duplicate.Workbench.DrainScriptingSettingsPersistenceAsync();
            Assert.Equal(theme, (await Iseberg.Core.UserSettings.LoadAsync(
                Path.Combine(duplicate.StateDirectory, "settings.json"))).ThemePreset);
            await ActivateAsync(window, ShortcutAction.CloseTab, new CloseTabArgs(IndexOf(window, duplicatedTab)));
            Assert.True(duplicate.IsDisposed);
            await ActivateAsync(window, ShortcutAction.RestoreLastClosed);
            var reopened = ActiveIse(window);
            var reopenedEngine = reopened.Workbench.Workbench.SelectedSession!.Engine;
            Assert.NotSame(duplicateEngine, reopenedEngine);
            Assert.NotEqual(duplicateEngine.LocalRunspaceId, reopenedEngine.LocalRunspaceId);
            Assert.NotEqual(originalEngine.LocalRunspaceId, reopenedEngine.LocalRunspaceId);
            AssertPalette(reopened);
            Assert.Equal(125, reopened.Workbench.GetScriptingSettings().Zoom);
            var restored = window.CaptureLayout().Tabs.Single(tab => tab.TabId == duplicatedTab.Id).Root.Session!;
            Assert.Equal(theme, restored.IseColorTheme);
            Assert.Equal(saved.SessionId, restored.SessionId);
            Assert.Equal(saved.ProfileId, restored.ProfileId);
            await SubmitTokenAsync(reopened, "'DT-ORIGINAL-THEME-REOPENED'", "DT-ORIGINAL-THEME-REOPENED");
            await ExecuteTokenAsync(original, "'DT-ORIGINAL-THEME-RETAINED'", "DT-ORIGINAL-THEME-RETAINED");
            AssertPalette(original);

            void AssertPalette(PowerShellIseTab content)
            {
                Assert.Equal(theme, content.Terminal.Profile!.IseColorTheme);
                Assert.Equal(theme, content.Workbench.GetScriptingSettings().ThemePreset);
                Assert.Equal(foreground, content.Terminal.Engine.Scheme.Foreground);
                Assert.Equal(background, content.Terminal.Engine.Scheme.Background);
                var editor = content.Workbench.ScriptEditorView.TextEditor;
                Assert.Equal(Avalonia.Media.Color.Parse(scriptForeground),
                    Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(editor.Foreground).Color);
                Assert.Equal(Avalonia.Media.Color.Parse(scriptBackground),
                    Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(editor.Background).Color);
                Assert.True(content.Terminal.IsRunning);
            }
        }, colorTheme: theme);

    [AvaloniaTheory]
    [InlineData("other")]
    [InlineData("after")]
    [InlineData("window")]
    [InlineData("application")]
    public async Task ActualCloseEntryPointsCancelAndRetainPreferencesRecoveryLeasesSelectionAndNativeRuntimes(string route)
    {
        await InitializeRuntimeAsync();
        await CloseEntryPointConjunctionCoreAsync(route);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task CloseEntryPointConjunctionCoreAsync(string route)
    {
        await WithWindowAsync(async (window, _) =>
        {
        var keep = window.ActiveTab!;
        await ActivateAsync(window, ShortcutAction.DuplicateTab);
        var preparedTab = window.ActiveTab!;
        var prepared = ActiveIse(window);
        await ActivateAsync(window, ShortcutAction.DuplicateTab);
        var dirtyTab = window.ActiveTab!;
        var dirty = ActiveIse(window);
        var document = dirty.Workbench.CreateDocument("dirty close conjunction");
        dirty.Workbench.ScriptEditorView.CaretOffset = 5;
        var participants = new[] { prepared, dirty };
        var engines = participants.Select(content => content.Workbench.Workbench.SelectedSession!.Engine).ToArray();
        var connections = participants.Select(NativeConnection).ToArray();
        for (var index = 0; index < participants.Length; index++)
        {
            var content = participants[index];
            content.Workbench.Scripting.Options.AutoSaveMinuteInterval = 0;
            content.Workbench.Scripting.Options.ShowLineNumbers = false;
            content.Workbench.Scripting.Options.Zoom = 125 + index * 25;
            await content.Workbench.DrainScriptingSettingsPersistenceAsync();
            await ExecuteTokenAsync(content, "'DT-CLOSE-BASE-" + index + "'", "DT-CLOSE-BASE-" + index);
            await content.Workbench.SaveRecoveryAsync();
        }
        var settingsPaths = participants.Select(content => Path.Combine(content.StateDirectory, "settings.json")).ToArray();
        var snapshotPath = Path.Combine(settingsPaths[1] + ".recovery", document.RecoveryId.ToString("N") + ".json");
        var snapshot = await File.ReadAllBytesAsync(snapshotPath);
        var tabs = window.Tabs.ToArray();
        var active = window.ActiveTab;
        var desktop = System.Reflection.DispatchProxy.Create<
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime, Phase3OwnedDesktopLifetime>();
        var lifetime = (Phase3OwnedDesktopLifetime)(object)desktop;
        lifetime.OwnedWindows = [window];
        Assert.Equal(new[] { window }, desktop.Windows.OfType<MainWindow>());
        for (var attempt = 0; attempt < 2; attempt++)
        {
            // Native execution between attempts legitimately persists debugger state; snapshot each accepted transaction.
            var preferences = await File.ReadAllBytesAsync(settingsPaths[1]);
            var before = window.OwnedWindows.ToArray();
            Task request;
            if (route == "application") request = MainWindow.RequestApplicationCloseAsync(desktop);
            else if (route == "window")
            {
                window.Close(); // Actual OnClosing -> ConfirmWindowCloseAsync route.
                request = Task.CompletedTask;
            }
            else request = window.ActivateAsync(Activation(route == "other" ? ShortcutAction.CloseOtherTabs : ShortcutAction.CloseTabsAfter,
                route == "other" ? new CloseOtherTabsArgs(0) : new CloseTabsAfterArgs(0))).AsTask();
            await WaitForAsync(() => window.OwnedWindows.Any(dialog => !before.Contains(dialog)), () => "Owned dirty-close prompt missing.");
            var dialog = Assert.Single(window.OwnedWindows, value => !before.Contains(value));
            try
            {
                Assert.False(prepared.Workbench.IsEnabled);
                Assert.False(prepared.IsDisposed);
                if (route == "application")
                    Assert.False(await MainWindow.RequestApplicationCloseAsync(desktop)); // Pending guard, not another prompt.
                ClickCancel(dialog);
            }
            finally { if (dialog.IsVisible) dialog.Close(); }
            await request.WaitAsync(TimeSpan.FromSeconds(60));
            if (route == "application") Assert.False(await (Task<bool>)request);
            await WaitForAsync(() => participants.All(content => content.Workbench.IsEnabled), () => "Cancellation did not re-enable all participants.");
            Assert.True(window.IsVisible);
            Assert.Equal(tabs, window.Tabs.ToArray());
            Assert.Same(active, window.ActiveTab);
            Assert.Equal(0, lifetime.ShutdownCalls);
            Assert.Contains(keep, window.Tabs);
            Assert.Contains(preparedTab, window.Tabs);
            Assert.Contains(dirtyTab, window.Tabs);
            Assert.Same(document, dirty.Workbench.Workbench.SelectedSession!.SelectedFile);
            Assert.True(document.File.IsDirty);
            Assert.Equal("dirty close conjunction", document.Document.Text);
            Assert.Equal(5, dirty.Workbench.ScriptEditorView.CaretOffset);
            Assert.Equal(snapshot, await File.ReadAllBytesAsync(snapshotPath));
            Assert.Equal(preferences, await File.ReadAllBytesAsync(settingsPaths[1]));
            for (var index = 0; index < participants.Length; index++)
            {
                var content = participants[index];
                Assert.False(content.IsDisposed);
                Assert.Same(engines[index], content.Workbench.Workbench.SelectedSession!.Engine);
                Assert.Same(connections[index], NativeConnection(content));
                Assert.Equal(125 + index * 25, content.Workbench.Scripting.Options.Zoom);
                Assert.False(content.Workbench.Scripting.Options.ShowLineNumbers);
                Assert.Throws<IOException>(() => new Iseberg.Core.WorkbenchPersistenceLease(settingsPaths[index]));
                await SubmitTokenAsync(content, "'DT-CLOSE-CANCEL-" + index + "'", "DT-CLOSE-CANCEL-" + index);
            }
        }
        document.Document.Insert(document.Document.TextLength, "\n# editable");
        Assert.Equal("dirty close conjunction\n# editable", document.Document.Text);
        // Lease ownership alone is insufficient: a canceled group must republish LIVE crash metadata.
        var state = System.Text.Json.JsonSerializer.Deserialize<Iseberg.Core.WorkbenchState>(
            await File.ReadAllTextAsync(settingsPaths[0] + ".workbench.json"))!;
        Assert.Equal(Environment.ProcessId, state.OwnerProcessId);
        });
    }

    public class Phase3OwnedDesktopLifetime : System.Reflection.DispatchProxy
    {
        public IReadOnlyList<Avalonia.Controls.Window> OwnedWindows { get; set; } = [];
        public int ShutdownCalls { get; private set; }
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "get_Windows") return OwnedWindows;
            if (targetMethod.Name == "Shutdown") { ShutdownCalls++; return null; }
            throw new NotSupportedException("Unexpected public desktop lifetime operation: " + targetMethod.Name);
        }
    }

    [AvaloniaTheory]
    [InlineData("Classic ISE", 0xFFFFFFFFu, 0xFF012456u, "#FFFFFF", "#000000")]
    [InlineData("Dark", 0xFFD4D4D4u, 0xFF1E1E1Eu, "#1E1E1E", "#D4D4D4")]
    [InlineData("Light", 0xFF202020u, 0xFFFFFFFFu, "#FFFFFF", "#000000")]
    [InlineData("Follow DT", 0xFF202020u, 0xFFFFFFFFu, "#FFFFFF", "#000000")]
    public async Task LegacyAliasesKeepAcceptedOptionsIdentityAcrossProfileCaptureDuplicateReopenAndScopedCompletion(
        string alias, uint foreground, uint background, string scriptBackground, string scriptForeground)
    {
        await InitializeRuntimeAsync();
        await AliasLifecycleCoreAsync(alias, foreground, background, scriptBackground, scriptForeground);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task AliasLifecycleCoreAsync(string alias, uint foreground, uint background,
        string scriptBackground, string scriptForeground) => WithWindowAsync(async (window, _) =>
    {
        window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        var original = ActiveIse(window);
        var originalEngine = original.Workbench.Workbench.SelectedSession!.Engine;
        var saved = Assert.Single(window.CaptureLayout().Tabs).Root.Session!;
        Assert.Equal(alias, saved.IseColorTheme);
        await ActivateAsync(window, ShortcutAction.DuplicateTab);
        var tab = window.ActiveTab!;
        var duplicate = ActiveIse(window);
        var duplicateEngine = duplicate.Workbench.Workbench.SelectedSession!.Engine;
        duplicate.Workbench.Scripting.Options.Zoom = 150;
        duplicate.Workbench.Scripting.Options.SelectedScriptPaneState = "Right";
        await duplicate.Workbench.DrainScriptingSettingsPersistenceAsync();
        var path = Path.Combine(duplicate.StateDirectory, "settings.json");
        Assert.Equal(alias, (await Iseberg.Core.UserSettings.LoadAsync(path)).ThemePreset);
        await ActivateAsync(window, ShortcutAction.CloseTab, new CloseTabArgs(IndexOf(window, tab)));
        await ActivateAsync(window, ShortcutAction.RestoreLastClosed);
        var reopened = ActiveIse(window);
        Assert.Equal(alias, reopened.Workbench.SelectedThemePreset);
        Assert.Equal(alias, window.CaptureLayout().Tabs.Single(value => value.TabId == tab.Id).Root.Session!.IseColorTheme);
        Assert.Equal(foreground, reopened.Terminal.Engine.Scheme.Foreground);
        Assert.Equal(background, reopened.Terminal.Engine.Scheme.Background);
        var editor = reopened.Workbench.ScriptEditorView.TextEditor;
        Assert.Equal(Avalonia.Media.Color.Parse(scriptBackground), Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(editor.Background).Color);
        Assert.Equal(Avalonia.Media.Color.Parse(scriptForeground), Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(editor.Foreground).Color);
        Assert.Equal(150, reopened.Workbench.Scripting.Options.Zoom);
        Assert.Equal("Right", reopened.Workbench.Scripting.Options.SelectedScriptPaneState);
        Assert.NotSame(originalEngine, reopened.Workbench.Workbench.SelectedSession!.Engine);
        Assert.NotSame(duplicateEngine, reopened.Workbench.Workbench.SelectedSession.Engine);
        await SubmitTokenAsync(reopened, "'DT-ALIAS-REOPEN-ENGINE'", "DT-ALIAS-REOPEN-ENGINE");
        await AssertPhase3CompletionScopeAsync(reopened, alias == "Dark");
        Assert.Equal(alias, original.Workbench.SelectedThemePreset);
    }, colorTheme: alias);

    [AvaloniaTheory]
    [InlineData("Dark Console, Light Editor (default)", false)]
    [InlineData("Light Console, Dark Editor", true)]
    [InlineData("Dark Console, Dark Editor", true)]
    [InlineData("Light Console, Light Editor", false)]
    [InlineData("Monochrome Green", true)]
    [InlineData("Presentation", false)]
    public async Task OriginalPresetSecondSelectedCompletionAppearanceSurvivesDuplicateAndDurableReopen(string theme, bool dark)
    {
        await InitializeRuntimeAsync();
        await CompletionLifecycleCoreAsync(theme, dark);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task CompletionLifecycleCoreAsync(string theme, bool dark) => WithWindowAsync(async (window, _) =>
    {
        window.RequestedThemeVariant = dark ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
        var original = ActiveIse(window);
        var engine = original.Workbench.Workbench.SelectedSession!.Engine;
        await AssertPhase3CompletionScopeAsync(original, dark);
        await ActivateAsync(window, ShortcutAction.DuplicateTab);
        var duplicate = ActiveIse(window);
        var tab = window.ActiveTab!;
        await AssertPhase3CompletionScopeAsync(duplicate, dark);
        duplicate.Workbench.Scripting.Options.Zoom = 125;
        await duplicate.Workbench.DrainScriptingSettingsPersistenceAsync();
        await ActivateAsync(window, ShortcutAction.CloseTab, new CloseTabArgs(IndexOf(window, tab)));
        await ActivateAsync(window, ShortcutAction.RestoreLastClosed);
        var reopened = ActiveIse(window);
        Assert.Equal(theme, reopened.Workbench.SelectedThemePreset);
        Assert.Equal(125, reopened.Workbench.Scripting.Options.Zoom);
        Assert.NotSame(engine, reopened.Workbench.Workbench.SelectedSession!.Engine);
        await AssertPhase3CompletionScopeAsync(reopened, dark);
        Assert.Equal(theme, original.Workbench.SelectedThemePreset);
    }, colorTheme: theme);

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealHostCloseStorageFailuresNotifyAndRetainPreparedTabOrRemoveDisposedTabWithoutOrphans(bool afterPreparation)
    {
        await InitializeRuntimeAsync();
        await HostStorageCloseFailureCoreAsync(afterPreparation);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task HostStorageCloseFailureCoreAsync(bool afterPreparation) => WithWindowAsync(async (window, _) =>
    {
        var originalTab = window.ActiveTab!;
        var original = ActiveIse(window);
        await ActivateAsync(window, ShortcutAction.DuplicateTab);
        var targetTab = window.ActiveTab!;
        var target = ActiveIse(window);
        var engine = target.Workbench.Workbench.SelectedSession!.Engine;
        target.Workbench.Scripting.Options.AutoSaveMinuteInterval = 0;
        await target.Workbench.DrainScriptingSettingsPersistenceAsync();
        if (afterPreparation) Assert.True(await target.PrepareCloseAsync());
        var path = Path.Combine(target.StateDirectory, "settings.json");
        var backup = path + ".owned-backup";
        var bytes = await File.ReadAllBytesAsync(path);
        File.Move(path, backup);
        Directory.CreateDirectory(path);
        try
        {
            await ActivateAsync(window, ShortcutAction.CloseTab, new CloseTabArgs(IndexOf(window, targetTab)));
            var title = window.FindControl<Avalonia.Controls.TextBlock>("NotificationTitle")!;
            var body = window.FindControl<Avalonia.Controls.TextBlock>("NotificationBody")!;
            Assert.Equal(afterPreparation ? "Unable to close Iseberg cleanly" : "Unable to close workbench", title.Text);
            Assert.False(string.IsNullOrWhiteSpace(body.Text));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(backup));
            Assert.Contains(originalTab, window.Tabs);
            Assert.False(original.IsDisposed);
            if (afterPreparation)
            {
                Assert.True(target.IsDisposed);
                Assert.DoesNotContain(targetTab, window.Tabs);
                Assert.Equal(Iseberg.Core.SessionState.Disposed, engine.State);
                using var reacquired = new Iseberg.Core.WorkbenchPersistenceLease(path);
                Assert.DoesNotContain(target, window.GetVisualDescendants().OfType<PowerShellIseTab>());
                Assert.Same(originalTab, window.ActiveTab);
            }
            else
            {
                Assert.Contains(targetTab, window.Tabs);
                Assert.Same(targetTab, window.ActiveTab);
                Assert.False(target.IsDisposed);
                Assert.True(target.Workbench.IsEnabled);
                Assert.Throws<IOException>(() => new Iseberg.Core.WorkbenchPersistenceLease(path));
                Assert.Equal(Iseberg.Core.SessionState.Ready, engine.State);
            }
        }
        finally
        {
            Directory.Delete(path);
            File.Move(backup, path);
        }
        await ExecuteTokenAsync(original, "'DT-CLOSE-FAULT-ORIGINAL-VALID'", "DT-CLOSE-FAULT-ORIGINAL-VALID");
        if (!target.IsDisposed)
            await ExecuteTokenAsync(target, "'DT-CLOSE-FAULT-TARGET-VALID'", "DT-CLOSE-FAULT-TARGET-VALID");
    });

    [AvaloniaTheory]
    [InlineData("settings")]
    [InlineData("workbench")]
    [InlineData("recovery")]
    public async Task FailedReopenInitializationRollsBackAttachmentReportsErrorAndReleasesOnlyItsOwnedLease(string corrupt)
    {
        await InitializeRuntimeAsync();
        await FailedReopenAttachmentCoreAsync(corrupt);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task FailedReopenAttachmentCoreAsync(string corrupt) => WithWindowAsync(async (window, _) =>
    {
        var originalTab = window.ActiveTab!;
        var original = ActiveIse(window);
        await ActivateAsync(window, ShortcutAction.DuplicateTab);
        var targetTab = window.ActiveTab!;
        var target = ActiveIse(window);
        await ActivateAsync(window, ShortcutAction.CloseTab, new CloseTabArgs(IndexOf(window, targetTab)));
        var settingsPath = Path.Combine(target.StateDirectory, "settings.json");
        var path = corrupt switch
        {
            "settings" => settingsPath,
            "workbench" => settingsPath + ".workbench.json",
            _ => Path.Combine(settingsPath + ".recovery", "ad4976254876453582211e5b22742982.json")
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{independently malformed reopen");
        var bytes = await File.ReadAllBytesAsync(path);
        var result = await window.ActivateAsync(Activation(ShortcutAction.RestoreLastClosed)).AsTask();
        Assert.False(result.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.Contains("invalid start of a property name", result.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { originalTab }, window.Tabs);
        Assert.Same(originalTab, window.ActiveTab);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        using var reacquired = new Iseberg.Core.WorkbenchPersistenceLease(settingsPath);
        Assert.Throws<IOException>(() => new Iseberg.Core.WorkbenchPersistenceLease(Path.Combine(original.StateDirectory, "settings.json")));
        Assert.Equal(new[] { original }, window.GetVisualDescendants().OfType<PowerShellIseTab>());
        await ExecuteTokenAsync(original, "'DT-FAILED-REOPEN-ORIGINAL-VALID'", "DT-FAILED-REOPEN-ORIGINAL-VALID");
    });

    [AvaloniaTheory]
    [InlineData("Dark Console, Light Editor (default)", "Light Console, Dark Editor", false)]
    [InlineData("Light Console, Dark Editor", "Dark Console, Light Editor (default)", true)]
    public async Task MixedPresetWorkspaceChangesRemainScopedBesideActualSettingsAndAnOwnedOrdinaryTerminal(
        string firstTheme, string secondTheme, bool darkScript)
    {
        await InitializeRuntimeAsync();
        await OrdinaryScopeConjunctionCoreAsync(firstTheme, secondTheme, darkScript);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task OrdinaryScopeConjunctionCoreAsync(string firstTheme, string secondTheme, bool darkScript) =>
        WithThemePairWindowAsync(firstTheme, secondTheme, async (window, secondGuid) =>
        {
            window.RequestedThemeVariant = darkScript ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
            var firstTab = window.ActiveTab!;
            var first = ActiveIse(window);
            await ActivateAsync(window, ShortcutAction.NewTab, new NewTabArgs(new NewTerminalArgs(Profile: secondGuid)));
            var second = ActiveIse(window);
            second.Workbench.Scripting.Options.SelectedScriptPaneState = "Right";
            second.Workbench.Scripting.Options.Zoom = 125;
            await second.Workbench.DrainScriptingSettingsPersistenceAsync();
            await ActivateAsync(window, ShortcutAction.OpenSettings, new OpenSettingsArgs(SettingsTarget.SettingsUI));
            var settingsTab = window.ActiveTab!;
            var settingsView = Assert.IsAssignableFrom<Avalonia.Controls.Control>(settingsTab.CustomContent);
            var settingsVariant = settingsView.ActualThemeVariant;
            var windowVariant = window.ActualThemeVariant;
            var ordinary = new TermControl();
            var ordinaryProfile = new ProfileSettings { Name = "Owned ordinary terminal", ColorScheme = "Solarized Dark", FontSize = 15 };
            ordinary.ApplyAppearance(ordinaryProfile); // No shell process, profile script, OS terminal or foreground window.
            ordinary.Engine.Scheme = Devolutions.Terminal.Core.ColorScheme.SolarizedDark;
            var ordinaryOwner = new Window { Width = 400, Height = 200, Content = ordinary, RequestedThemeVariant = windowVariant };
            try
            {
            ordinaryOwner.Show(window);
            ordinaryOwner.UpdateLayout();
            Assert.True(ordinary.IsEffectivelyVisible);
            var scheme = ordinary.Engine.Scheme;
            var table = (uint[])scheme.Table.Clone();
            var ordinaryFont = ordinary.FontSize;
            await ActivateAsync(window, ShortcutAction.SwitchToTab, new SwitchToTabArgs(IndexOf(window, firstTab)));
            first.Workbench.Scripting.Options.ShowLineNumbers = false;
            first.Workbench.Scripting.Options.Zoom = 150;
            await first.Workbench.DrainScriptingSettingsPersistenceAsync();
            // The second's own palette is independent of view settings in the first.
            Assert.Equal(firstTheme, first.Workbench.SelectedThemePreset);
            Assert.Equal(secondTheme, second.Workbench.SelectedThemePreset);
            Assert.Equal(125, second.Workbench.Scripting.Options.Zoom);
            Assert.True(second.Workbench.Scripting.Options.ShowLineNumbers);
            Assert.Equal(Avalonia.Media.Color.Parse(darkScript ? "#FFFFFF" : "#012456"),
                Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(second.Workbench.ScriptEditorView.TextEditor.Background).Color);
            Assert.Equal(darkScript ? 0xFF012456u : 0xFFFFFFFFu, second.Terminal.Engine.Scheme.Background);
            Assert.Equal(windowVariant, window.ActualThemeVariant);
            Assert.Equal("Solarized Dark", scheme.Name);
            Assert.Equal(0xFF839496u, ordinary.Engine.Scheme.Foreground);
            Assert.Equal(0xFF002B36u, ordinary.Engine.Scheme.Background);
            Assert.Equal(table, ordinary.Engine.Scheme.Table);
            Assert.Equal(ordinaryFont, ordinary.FontSize);
            Assert.False(ordinary.IsRunning);
            Assert.Equal(windowVariant, ordinary.ActualThemeVariant);
            Assert.Equal("Owned ordinary terminal", ordinary.Profile!.Name);
            await ActivateAsync(window, ShortcutAction.SwitchToTab, new SwitchToTabArgs(IndexOf(window, settingsTab)));
            window.UpdateLayout();
            Assert.Same(settingsView, window.ActiveTab!.CustomContent);
            Assert.Equal(settingsVariant, settingsView.ActualThemeVariant);
            Assert.Equal(windowVariant, settingsView.ActualThemeVariant);
            }
            finally
            {
                await ordinary.CloseAsync();
                ordinaryOwner.Content = null;
                ordinaryOwner.Close();
            }
        });

    private static async Task WithThemePairWindowAsync(string firstTheme, string secondTheme, Func<MainWindow, string, Task> test)
    {
        using var environment = new IseTestEnvironment();
        environment.SaveDefaultProfile(firstTheme);
        var settings = SettingsLoader.Load(SettingsLoader.ReadEmbeddedDefaults(), File.ReadAllText(SettingsService.SettingsPath));
        var profile = environment.Profile();
        profile.Guid = Guid.NewGuid().ToString("B");
        profile.Name = "Owned second mixed profile";
        profile.IseColorTheme = secondTheme;
        profile.IseAutoSaveMinutes = 0;
        settings.Profiles.Add(profile);
        SettingsService.Save(settings);
        var window = new MainWindow(0, string.Empty, null,
            stateStore: new ApplicationStateStore(environment.DirectoryPath), isolated: true);
        try
        {
            window.Show();
            var result = await window.InitialActivation.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(result.Succeeded, result.Message);
            await test(window, profile.Guid);
        }
        finally
        {
            foreach (var content in window.Tabs.Select(tab => tab.CustomContent).OfType<PowerShellIseTab>())
                foreach (var document in content.Workbench.Workbench.Sessions.SelectMany(session => session.Files).Where(file => file.File.IsDirty).ToArray())
                    await content.Workbench.SaveDocumentAsync(document, Path.Combine(environment.DirectoryPath, $"{document.RecoveryId:N}.ps1"));
            while (window.Tabs.Count > 0) await ActivateAsync(window, ShortcutAction.CloseTab, new CloseTabArgs(0));
            window.Close();
        }
    }
}
