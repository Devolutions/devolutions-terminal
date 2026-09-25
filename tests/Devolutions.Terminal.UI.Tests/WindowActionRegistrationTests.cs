using Avalonia.Headless.XUnit;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Devolutions.Terminal.Settings;
using Devolutions.Terminal.App.Routing;
using Devolutions.Terminal.App.Views;
using Xunit;

namespace Devolutions.Terminal.UI.Tests;

public sealed class WindowActionRegistrationTests
{
    private static readonly TerminalWindowActivation SettingsOnlyStartup = new(
        null,
        null,
        null,
        null,
        TerminalWindowLaunchMode.Default,
        [new ActionAndArgs(
            ShortcutAction.OpenSettings,
            new OpenSettingsArgs(SettingsTarget.SettingsUI))]);

    [AvaloniaFact]
    public void NewTabDropdownUsesExistingProfileEntriesForCtrlClickLaunch()
    {
        var window = new MainWindow(0, string.Empty, SettingsOnlyStartup);

        var menu = window.BuildNewTabMenu();

        Assert.DoesNotContain(menu, item => Equals(item.Header, "Open as administrator"));
        var profiles = menu.Where(item =>
            AutomationProperties.GetAutomationId(item)?.StartsWith(
                "NewTabMenuItem_Profile_", StringComparison.Ordinal) == true).ToArray();
        Assert.NotEmpty(profiles);
        foreach (var profile in profiles)
        {
            var tip = Assert.IsType<string>(ToolTip.GetTip(profile));
            Assert.Contains("Alt+Click to split the current window", tip);
            Assert.Contains("Shift+Click to open a new window", tip);
            Assert.Equal(
                OperatingSystem.IsWindows(),
                tip.Contains("Ctrl+Click to open as administrator", StringComparison.Ordinal));
        }
    }

    [AvaloniaFact]
    public void RegistersMarkColorAndWindowDialogActions()
    {
        var window = new MainWindow(0, string.Empty, SettingsOnlyStartup);

        var expected = new[]
        {
            ShortcutAction.ScrollToMark,
            ShortcutAction.AddMark,
            ShortcutAction.ClearMark,
            ShortcutAction.ClearAllMarks,
            ShortcutAction.SetColorScheme,
            ShortcutAction.ColorSelection,
            ShortcutAction.OpenTabColorPicker,
            ShortcutAction.OpenTabRenamer,
            ShortcutAction.ExecuteCommandline,
            ShortcutAction.BreakIntoDebugger,
            ShortcutAction.IdentifyWindows,
            ShortcutAction.RenameWindow,
            ShortcutAction.OpenWindowRenamer,
            ShortcutAction.ShowContextMenu,
            ShortcutAction.OpenWorkspace,
            ShortcutAction.Workspaces,
            ShortcutAction.GlobalSummon,
            ShortcutAction.QuakeMode,
            ShortcutAction.OpenSystemMenu,
            ShortcutAction.ToggleShaderEffects,
        };

        Assert.All(expected, action => Assert.Contains(action, window.RegisteredActions));
    }

    [AvaloniaFact]
    public async Task OpenSettingsCreatesReusableSettingsTab()
    {
        var window = new MainWindow(0, string.Empty, SettingsOnlyStartup);

        var first = await window.ActivateAsync(new TerminalWindowActivation(
            null,
            null,
            null,
            null,
            TerminalWindowLaunchMode.Default,
            [new ActionAndArgs(
                ShortcutAction.OpenSettings,
                new OpenSettingsArgs(SettingsTarget.SettingsUI))]));

        Assert.True(first.Succeeded);
        var settingsTab = Assert.Single(window.Tabs, static tab => tab.IsSettingsTab);
        Assert.Equal("Settings", settingsTab.Title);

        var second = await window.ActivateAsync(new TerminalWindowActivation(
            null,
            null,
            null,
            null,
            TerminalWindowLaunchMode.Default,
            [new ActionAndArgs(
                ShortcutAction.OpenSettings,
                new OpenSettingsArgs(SettingsTarget.SettingsUI))]));

        Assert.True(second.Succeeded);
        Assert.Same(settingsTab, Assert.Single(window.Tabs, static tab => tab.IsSettingsTab));
        Assert.DoesNotContain(window.CaptureLayout().Tabs, tab => tab.Title == "Settings");
    }

    [AvaloniaFact]
    public async Task ClosedSettingsTabIsNotRestoredAsATerminal()
    {
        var window = new MainWindow(0, string.Empty, SettingsOnlyStartup);

        await window.ActivateAsync(new TerminalWindowActivation(
            null,
            null,
            null,
            null,
            TerminalWindowLaunchMode.Default,
            [new ActionAndArgs(
                ShortcutAction.OpenSettings,
                new OpenSettingsArgs(SettingsTarget.SettingsUI))]));
        Assert.Contains(window.Tabs, static tab => tab.IsSettingsTab);
        var settingsIndex = (uint)window.Tabs
            .Select(static (tab, index) => (tab, index))
            .First(static entry => entry.tab.IsSettingsTab)
            .index;

        var closeResult = await window.ActivateAsync(new TerminalWindowActivation(
            null,
            null,
            null,
            null,
            TerminalWindowLaunchMode.Default,
            [new ActionAndArgs(ShortcutAction.CloseTab, new CloseTabArgs(settingsIndex))]));
        Dispatcher.UIThread.RunJobs();

        Assert.True(closeResult.Succeeded, closeResult.Message);
        Assert.DoesNotContain(window.Tabs, static tab => tab.IsSettingsTab);

        // The settings tab must not be pushed onto the reopen stack, otherwise
        // "restore last closed" would resurrect it as a real terminal session.
        // Asserted through the collection rather than by dispatching the action,
        // because closing the final tab also closes the window and whether a
        // terminal tab survives here is platform dependent.
        Assert.False(window.CanRestoreLastClosedTab);
    }

    [AvaloniaFact]
    public async Task TerminalOnlyActionsAreUnavailableOnTheSettingsTab()
    {
        var window = new MainWindow(0, string.Empty, SettingsOnlyStartup);

        await window.ActivateAsync(new TerminalWindowActivation(
            null,
            null,
            null,
            null,
            TerminalWindowLaunchMode.Default,
            [new ActionAndArgs(
                ShortcutAction.OpenSettings,
                new OpenSettingsArgs(SettingsTarget.SettingsUI))]));
        Dispatcher.UIThread.RunJobs();

        var settingsIndex = (uint)window.Tabs
            .Select(static (tab, index) => (tab, index))
            .First(static entry => entry.tab.IsSettingsTab)
            .index;
        await window.ActivateAsync(new TerminalWindowActivation(
            null,
            null,
            null,
            null,
            TerminalWindowLaunchMode.Default,
            [new ActionAndArgs(ShortcutAction.SwitchToTab, new SwitchToTabArgs(settingsIndex))]));
        Dispatcher.UIThread.RunJobs();

        var paste = await window.ActivateAsync(new TerminalWindowActivation(
            null,
            null,
            null,
            null,
            TerminalWindowLaunchMode.Default,
            [new ActionAndArgs(ShortcutAction.PasteText)]));
        var duplicate = await window.ActivateAsync(new TerminalWindowActivation(
            null,
            null,
            null,
            null,
            TerminalWindowLaunchMode.Default,
            [new ActionAndArgs(ShortcutAction.DuplicateTab)]));
        Dispatcher.UIThread.RunJobs();

        Assert.False(paste.Succeeded);
        Assert.False(duplicate.Succeeded);
        Assert.Single(window.Tabs, static tab => tab.IsSettingsTab);
    }

    [AvaloniaFact]
    public async Task ActivatingAfterTheLastTabClosesDoesNotThrow()
    {
        var window = new MainWindow(0, string.Empty, SettingsOnlyStartup);

        await window.ActivateAsync(new TerminalWindowActivation(
            null,
            null,
            null,
            null,
            TerminalWindowLaunchMode.Default,
            [new ActionAndArgs(
                ShortcutAction.OpenSettings,
                new OpenSettingsArgs(SettingsTarget.SettingsUI))]));
        await window.InitialActivation;
        Dispatcher.UIThread.RunJobs();

        while (window.Tabs.Count > 0)
        {
            await window.ActivateAsync(new TerminalWindowActivation(
                null,
                null,
                null,
                null,
                TerminalWindowLaunchMode.Default,
                [new ActionAndArgs(ShortcutAction.CloseTab, new CloseTabArgs(0))]));
            Dispatcher.UIThread.RunJobs();
        }

        // Closing the final tab closes the window, so a later activation must not
        // try to re-show it.
        var afterClose = await window.ActivateAsync(new TerminalWindowActivation(
            null,
            null,
            null,
            null,
            TerminalWindowLaunchMode.Default,
            [new ActionAndArgs(ShortcutAction.RestoreLastClosed)]));
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(afterClose);
        Assert.DoesNotContain(window.Tabs, static tab => tab.IsSettingsTab);
    }

    [AvaloniaFact]
    public async Task RenameWindowUpdatesRoutingIdentityAndRejectsDuplicates()
    {
        var window = new MainWindow(
            7,
            "original",
            SettingsOnlyStartup,
            windowNameValidator: name => name != "duplicate");

        var renamed = await window.ActivateAsync(new TerminalWindowActivation(
            null,
            null,
            null,
            null,
            TerminalWindowLaunchMode.Default,
            [new ActionAndArgs(
                ShortcutAction.RenameWindow,
                new RenameWindowArgs("development"))]));
        var duplicate = await window.ActivateAsync(new TerminalWindowActivation(
            null,
            null,
            null,
            null,
            TerminalWindowLaunchMode.Default,
            [new ActionAndArgs(
                ShortcutAction.RenameWindow,
                new RenameWindowArgs("duplicate"))]));

        Assert.True(renamed.Succeeded);
        Assert.True(duplicate.Succeeded);
        Assert.Equal("development", window.WindowName);
    }

    [AvaloniaFact]
    public async Task RenameWindowRejectsPersistedWorkspaceCollision()
    {
        using var temporary = new TemporaryDirectory();
        var stateStore = new ApplicationStateStore(temporary.Path);
        stateStore.SaveWorkspace("existing", new WindowLayoutState());
        var window = new MainWindow(9, "original", SettingsOnlyStartup, stateStore: stateStore);

        var result = await window.ActivateAsync(new TerminalWindowActivation(
            null,
            null,
            null,
            null,
            TerminalWindowLaunchMode.Default,
            [new ActionAndArgs(
                ShortcutAction.RenameWindow,
                new RenameWindowArgs("existing"))]));

        Assert.True(result.Succeeded);
        Assert.Equal("original", window.WindowName);
        Assert.NotNull(stateStore.GetWorkspace("existing"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"wt-ui-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    [AvaloniaFact]
    public async Task WorkspaceActionsListAndRequestNamedWorkspace()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"Devolutions.Terminal.UI.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ApplicationStateStore(directory);
            store.SaveWorkspace("Beta", new WindowLayoutState());
            store.SaveWorkspace("alpha", new WindowLayoutState());
            string? requested = null;
            var window = new MainWindow(
                1,
                string.Empty,
                SettingsOnlyStartup,
                stateStore: store,
                workspaceRequested: name => requested = name);

            Assert.Equal(["alpha", "Beta"], window.WorkspaceNames);

            var open = await window.ActivateAsync(new TerminalWindowActivation(
                null,
                null,
                null,
                null,
                TerminalWindowLaunchMode.Default,
                [new ActionAndArgs(
                    ShortcutAction.OpenWorkspace,
                    new OpenWorkspaceArgs("alpha"))]));
            var list = await window.ActivateAsync(new TerminalWindowActivation(
                null,
                null,
                null,
                null,
                TerminalWindowLaunchMode.Default,
                [new ActionAndArgs(ShortcutAction.Workspaces)]));

            Assert.True(open.Succeeded);
            Assert.True(list.Succeeded);
            Assert.Equal("alpha", requested);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
