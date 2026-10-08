using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Devolutions.Terminal.App.Views;
using Devolutions.Terminal.Settings;
using Xunit;
using static Devolutions.Terminal.UI.Tests.PowerShellIseTestHarness;

namespace Devolutions.Terminal.UI.Tests;

public sealed class IseProfilePreferenceTests
{
    [AvaloniaFact]
    public async Task ProfilePreferencesOverrideStaleWorkspaceAppearanceAndOptionsRoutesToHostWithoutAnotherDialog()
    {
        await InitializeRuntimeAsync();
        await ProfilePreferenceCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ProfilePreferenceCoreAsync()
    {
        using var environment = new IseTestEnvironment();
        var profile = environment.Profile();
        profile.IseColorTheme = IsebergThemes.Presentation;
        profile.FontFace = "Consolas";
        profile.FontSize = 17;
        profile.IseShowLineNumbers = false;
        profile.IseWordWrap = true;
        profile.IsePromptToSaveBeforeRun = false;
        profile.IseAutoSaveMinutes = 0;
        profile.IseConsoleIntelliSense = false;
        profile.IseConsoleCompletionOnEnter = false;
        profile.IseScriptIntelliSense = false;
        profile.IseScriptCompletionOnEnter = false;
        profile.IseIntelliSenseTimeoutSeconds = 27;
        profile.IseShowOutlining = false;
        profile.IseWarnDuplicateFiles = false;
        profile.IseUseLocalHelp = false;
        profile.IseUseDefaultSnippets = false;
        profile.IseRecentFileCount = 25;
        profile.IseShowToolbar = false;
        var tab = new PowerShellIseTab(profile);
        await new Iseberg.Core.UserSettings
        {
            ThemePreset = IsebergThemes.Dark, FontFamily = "Lucida Console", FontSize = 24,
            ShowLineNumbers = true, WordWrap = false, PromptToSaveBeforeRun = true, AutoSaveMinutes = 5,
            CheckForUpdates = false
        }.SaveAsync(Path.Combine(tab.StateDirectory, "settings.json"));
        await using var host = new IseTestHost(tab);
        var requests = 0;
        tab.Workbench.OptionsRequested += (_, _) => requests++;
        await host.InitializeAsync();
        var preferences = tab.Workbench.GetScriptingSettings();
        Assert.Equal(IsebergThemes.Presentation, preferences.ThemePreset);
        Assert.Equal("Consolas", preferences.FontFamily);
        Assert.Equal(17, preferences.FontSize);
        Assert.False(preferences.ShowLineNumbers);
        Assert.True(preferences.WordWrap);
        Assert.False(preferences.PromptToSaveBeforeRun);
        Assert.Equal(0, preferences.AutoSaveMinutes);
        Assert.False(preferences.ConsoleIntelliSense);
        Assert.False(preferences.ConsoleCompletionOnEnter);
        Assert.False(preferences.ScriptIntelliSense);
        Assert.False(preferences.ScriptCompletionOnEnter);
        Assert.Equal(27, preferences.IntelliSenseTimeoutSeconds);
        Assert.False(preferences.ShowOutlining);
        Assert.False(preferences.WarnDuplicateFiles);
        Assert.False(preferences.UseLocalHelp);
        Assert.False(preferences.UseDefaultSnippets);
        Assert.Equal(25, preferences.RecentFileCount);
        Assert.False(preferences.ShowToolbar);
        Assert.Equal(17, tab.Workbench.ScriptEditorView.TextEditor.FontSize);
        Assert.Equal(17 * 3.0 / 4, tab.Terminal.FontSize, precision: 8);
        var engine = tab.Workbench.Workbench.SelectedSession!.Engine;
        var options = Assert.Single(MenuItems(tab.Workbench.FindControl<Menu>("WorkbenchMenu")!),
            item => Equals(item.Tag, "Options"));
        options.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await WaitForAsync(() => requests == 1, () => "Options was not routed to the host.");
        Assert.Empty(host.Owner.OwnedWindows);
        Assert.Same(engine, tab.Workbench.Workbench.SelectedSession!.Engine);
        await ExecuteTokenAsync(tab, "'DT-PROFILE-OPTIONS-ENGINE-RETAINED'", "DT-PROFILE-OPTIONS-ENGINE-RETAINED");
        Assert.Empty(host.Errors);
    }

    private static IEnumerable<MenuItem> MenuItems(ItemsControl parent)
    {
        foreach (var item in parent.Items.OfType<MenuItem>())
        {
            yield return item;
            foreach (var child in MenuItems(item)) yield return child;
        }
    }
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryDurablePreferenceFieldSurvivesWorkspaceSaveWhileProfileFieldsWinConflictingReload(bool flag)
    {
        await InitializeRuntimeAsync();
        await CompletePreferenceInventoryCoreAsync(flag);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task CompletePreferenceInventoryCoreAsync(bool flag)
    {
        using var environment = new IseTestEnvironment();
        var profile = environment.Profile();
        profile.IseColorTheme = IsebergThemes.Presentation;
        profile.FontFace = "Consolas";
        profile.FontSize = 22;
        profile.IseAutoSaveMinutes = 0;
        profile.IseRecentFileCount = 19;
        profile.IseIntelliSenseTimeoutSeconds = 13;
        // Complementary fields make swapped console/script or Enter/enable wiring observable.
        profile.IseShowLineNumbers = profile.IsePromptToSaveBeforeRun = profile.IseConsoleIntelliSense = flag;
        profile.IseScriptCompletionOnEnter = profile.IseShowOutlining = profile.IseUseLocalHelp = profile.IseShowToolbar = flag;
        profile.IseWordWrap = profile.IseConsoleCompletionOnEnter = profile.IseScriptIntelliSense = !flag;
        profile.IseWarnDuplicateFiles = profile.IseUseDefaultSnippets = !flag;
        var tab = new PowerShellIseTab(profile);
        var path = Path.Combine(tab.StateDirectory, "settings.json");
        var recent = Path.Combine(environment.DirectoryPath, "recent one.ps1");
        await File.WriteAllTextAsync(recent, "'owned recent file'");
        var loaded = new Iseberg.Core.UserSettings
        {
            Zoom = 150, Layout = "Right", ShowCommands = flag,
            ShowLineNumbers = !flag, WordWrap = flag, LoadProfiles = true,
            ShowOutlining = !flag, WarnDuplicateFiles = flag, PromptToSaveBeforeRun = !flag,
            ConsoleIntelliSense = !flag, ConsoleCompletionOnEnter = flag,
            ScriptIntelliSense = flag, ScriptCompletionOnEnter = !flag,
            IntelliSenseTimeoutSeconds = 29, UseLocalHelp = !flag, ShowToolbar = !flag,
            UseDefaultSnippets = flag, CheckForUpdates = true, AutoSaveMinutes = 117,
            RecentFileCount = 31, FontFamily = "Lucida Console", FontSize = 31,
            FixedWidthFontsOnly = flag, ThemePreset = "Dark",
            Theme = new() { Name = "stale owned palette" },
            CustomThemes = [new() { Name = "independent custom palette", Colors = InventoryColors() }],
            RecentFiles = [recent],
            HelpView = new() { Sections = [Iseberg.Core.HelpSectionKind.Examples, Iseberg.Core.HelpSectionKind.Notes],
                MatchCase = flag, WholeWord = !flag, Zoom = 175 },
            DebuggerSessions = [new() { Name = "PowerShell 8", Watches = ["41 + 1", "'café'"],
                Breakpoints = [new(Iseberg.Core.BreakpointKind.Command, Target: "Invoke-UncalledInventory",
                    Condition: "$true", Enabled: false),
                    new(Iseberg.Core.BreakpointKind.Line, ScriptPath: recent, Line: 1,
                        Action: "Write-Output 'owned breakpoint action'", Enabled: false),
                    new(Iseberg.Core.BreakpointKind.Variable, Target: "phase3Inventory", Condition: "$true",
                        AccessMode: System.Management.Automation.VariableAccessMode.ReadWrite, Enabled: false)] }],
            Geometry = new() { WindowWidth = 1234, WindowHeight = 876, WindowX = 37, WindowY = 53,
                Maximized = flag, TopScriptRatio = .42, RightScriptRatio = .73,
                DebuggerWidth = 451, CommandsWidth = 321 }
        };
        await loaded.SaveAsync(path);
        var loadedBytes = await File.ReadAllBytesAsync(path);
        await using (var host = new IseTestHost(tab))
        {
            await host.InitializeAsync();
            AssertInventory(tab.Workbench.GetScriptingSettings(), profile, flag, recent, mutated: false);
            Assert.Equal(loadedBytes, await File.ReadAllBytesAsync(path)); // Startup must not overwrite stale storage.
            var options = tab.Workbench.Scripting.Options;
            options.FontName = "Lucida Console";
            options.FontSize = 18;
            options.Zoom = 175;
            options.SelectedScriptPaneState = "Top";
            options.ShowLineNumbers = options.ShowOutlining = options.ShowWarningBeforeSavingOnRun = !flag;
            options.ShowIntellisenseInConsolePane = options.UseEnterToSelectInScriptPaneIntellisense = !flag;
            options.WordWrap = options.ShowWarningForDuplicateFiles = flag;
            options.UseEnterToSelectInConsolePaneIntellisense = options.ShowIntellisenseInScriptPane = flag;
            options.IntellisenseTimeoutInSeconds = 21;
            options.UseLocalHelp = options.ShowToolBar = !flag;
            options.ShowDefaultSnippets = flag;
            options.AutoSaveMinuteInterval = 0;
            options.MruCount = 23;
            options.FixedWidthFontsOnly = !flag;
            tab.Workbench.Scripting.CurrentPowerShellTab.ShowCommands = !flag;
            await tab.Workbench.DrainScriptingSettingsPersistenceAsync();
            AssertInventory(await Iseberg.Core.UserSettings.LoadAsync(path), profile, flag, recent, mutated: true);
            AssertInventory(tab.Workbench.GetScriptingSettings(), profile, flag, recent, mutated: true);
            Assert.Equal(18 * 1.75, tab.Workbench.ScriptEditorView.TextEditor.FontSize, precision: 10);
            Assert.Equal(18 * 1.75 * .75, tab.Terminal.FontSize, precision: 8);
            Assert.Equal(!flag, tab.Workbench.ScriptEditorView.TextEditor.ShowLineNumbers);
            Assert.Equal(flag, tab.Workbench.ScriptEditorView.TextEditor.WordWrap);
            Assert.Equal(!flag, tab.Workbench.FindControl<Control>("CommandsPane")!.IsVisible);
            Assert.Empty(host.Errors);
        }
        var reopened = new PowerShellIseTab(profile, workspaceId: tab.WorkspaceId);
        await using var reopenedHost = new IseTestHost(reopened);
        await reopenedHost.InitializeAsync();
        var reloaded = reopened.Workbench.GetScriptingSettings();
        AssertInventory(reloaded, profile, flag, recent, mutated: false, reopened: true);
        Assert.NotSame(tab.Workbench.Workbench.SelectedSession!.Engine, reopened.Workbench.Workbench.SelectedSession!.Engine);
        Assert.Equal(22 * 1.75, reopened.Workbench.ScriptEditorView.TextEditor.FontSize, precision: 10);
        Assert.False(reloaded.LoadProfiles); // Never execute user profiles even though stored input requests it.
        Assert.Empty(reopenedHost.Errors);
    }

    private static Dictionary<string, string> InventoryColors()
    {
        // Explicit schema inventory, not a representative palette sample or values read from the SUT.
        var keys = new[]
        {
            "Script.Foreground", "Script.Background", "Console.Foreground", "Console.Background", "Console.TextBackground",
            "Script.Comment", "Script.Keyword", "Script.String", "Script.Variable", "Script.Number", "Script.Command",
            "Script.Function", "Script.Attribute", "Script.CommandArgument", "Script.Label", "Script.Parameter",
            "Script.Type", "Script.Operator", "Script.Member", "Console.Comment", "Console.Keyword", "Console.String",
            "Console.Variable", "Console.Number", "Console.Command", "Console.Function", "Console.Attribute",
            "Console.CommandArgument", "Console.Label", "Console.Parameter", "Console.Type", "Console.Operator",
            "Console.Member", "Xml.Comment", "Xml.Tag", "Xml.Attribute", "Xml.Value", "Stream.Error", "Stream.Warning",
            "Stream.Verbose", "Stream.Debug"
        };
        return keys.Select((key, index) => (key, color: $"#{0x123400 + index:X6}"))
            .ToDictionary(item => item.key, item => item.color);
    }

    private static void AssertInventory(Iseberg.Core.UserSettings actual, ProfileSettings profile,
        bool flag, string recent, bool mutated, bool reopened = false)
    {
        Assert.Equal(reopened || mutated ? 175 : 150, actual.Zoom);
        Assert.Equal(reopened || mutated ? "Top" : "Right", actual.Layout);
        Assert.Equal(reopened || mutated ? !flag : flag, actual.ShowCommands);
        Assert.Equal(mutated ? !flag : profile.IseShowLineNumbers, actual.ShowLineNumbers);
        Assert.Equal(mutated ? flag : profile.IseWordWrap, actual.WordWrap);
        Assert.False(actual.LoadProfiles);
        Assert.Equal(mutated ? !flag : profile.IseShowOutlining, actual.ShowOutlining);
        Assert.Equal(mutated ? flag : profile.IseWarnDuplicateFiles, actual.WarnDuplicateFiles);
        Assert.Equal(mutated ? !flag : profile.IsePromptToSaveBeforeRun, actual.PromptToSaveBeforeRun);
        Assert.Equal(mutated ? !flag : profile.IseConsoleIntelliSense, actual.ConsoleIntelliSense);
        Assert.Equal(mutated ? flag : profile.IseConsoleCompletionOnEnter, actual.ConsoleCompletionOnEnter);
        Assert.Equal(mutated ? flag : profile.IseScriptIntelliSense, actual.ScriptIntelliSense);
        Assert.Equal(mutated ? !flag : profile.IseScriptCompletionOnEnter, actual.ScriptCompletionOnEnter);
        Assert.Equal(mutated ? 21 : 13, actual.IntelliSenseTimeoutSeconds);
        Assert.Equal(mutated ? !flag : profile.IseUseLocalHelp, actual.UseLocalHelp);
        Assert.Equal(mutated ? !flag : profile.IseShowToolbar, actual.ShowToolbar);
        Assert.Equal(mutated ? flag : profile.IseUseDefaultSnippets, actual.UseDefaultSnippets);
        Assert.False(actual.CheckForUpdates);
        Assert.Equal(0, actual.AutoSaveMinutes);
        Assert.Equal(mutated ? 23 : 19, actual.RecentFileCount);
        Assert.Equal(mutated ? "Lucida Console" : "Consolas", actual.FontFamily);
        Assert.Equal(mutated ? 18 : 22, actual.FontSize);
        Assert.Equal(reopened || mutated ? !flag : flag, actual.FixedWidthFontsOnly);
        Assert.Equal(IsebergThemes.Presentation, actual.ThemePreset);
        Assert.Equal(IsebergThemes.Presentation, actual.Theme.Name);
        Assert.Equal("#FFFFFF", actual.Theme.Colors["Script.Background"]);
        Assert.Equal("#000000", actual.Theme.Colors["Console.Background"]);
        var presentation = new Dictionary<string, string>
        {
            ["Script.Foreground"] = "#000000", ["Script.Background"] = "#FFFFFF",
            ["Console.Foreground"] = "#F5F5F5", ["Console.Background"] = "#000000", ["Console.TextBackground"] = "#000000",
            ["Xml.Comment"] = "#006400", ["Xml.Tag"] = "#8B0000", ["Xml.Attribute"] = "#FF0000", ["Xml.Value"] = "#00008B",
            ["Stream.Error"] = "#FF0000", ["Stream.Warning"] = "#FF8C00", ["Stream.Verbose"] = "#0000FF", ["Stream.Debug"] = "#0000FF"
        };
        var categories = new[] { "Attribute", "Command", "CommandArgument", "Parameter", "Comment", "Keyword", "Label",
            "Member", "Number", "Operator", "String", "Type", "Variable", "Function" };
        var script = "00BFFF,0000FF,8A2BE2,000080,006400,00008B,00008B,000000,800080,A9A9A9,8B0000,008080,FF4500,0000FF".Split(',');
        var console = "B0C4DE,E0FFFF,EE82EE,FFE4B5,98FB98,E0FFFF,E0FFFF,F5F5F5,FFE4C4,D3D3D3,DB7093,8FBC8F,FF4500,E0FFFF".Split(',');
        for (var index = 0; index < categories.Length; index++)
        {
            presentation.Add("Script." + categories[index], "#" + script[index]);
            presentation.Add("Console." + categories[index], "#" + console[index]);
        }
        Assert.Equal(presentation.OrderBy(pair => pair.Key), actual.Theme.Colors.OrderBy(pair => pair.Key));
        var custom = Assert.Single(actual.CustomThemes);
        Assert.Equal("independent custom palette", custom.Name);
        Assert.Equal(InventoryColors().OrderBy(pair => pair.Key), custom.Colors.OrderBy(pair => pair.Key));
        Assert.Equal(new[] { recent }, actual.RecentFiles);
        Assert.Equal(new[] { Iseberg.Core.HelpSectionKind.Examples, Iseberg.Core.HelpSectionKind.Notes }, actual.HelpView.Sections);
        Assert.Equal(flag, actual.HelpView.MatchCase);
        Assert.Equal(!flag, actual.HelpView.WholeWord);
        Assert.Equal(175, actual.HelpView.Zoom);
        var debugger = Assert.Single(actual.DebuggerSessions);
        Assert.Equal("PowerShell 8", debugger.Name);
        Assert.Equal(new[] { "41 + 1", "'café'" }, debugger.Watches);
        Assert.Equal(new[]
        {
            new Iseberg.Core.BreakpointSpec(Iseberg.Core.BreakpointKind.Command,
                Target: "Invoke-UncalledInventory", Condition: "$true", Enabled: false),
            new Iseberg.Core.BreakpointSpec(Iseberg.Core.BreakpointKind.Line, ScriptPath: recent, Line: 1,
                Action: "Write-Output 'owned breakpoint action'", Enabled: false),
            new Iseberg.Core.BreakpointSpec(Iseberg.Core.BreakpointKind.Variable, Target: "phase3Inventory", Condition: "$true",
                AccessMode: System.Management.Automation.VariableAccessMode.ReadWrite, Enabled: false)
        }, debugger.Breakpoints);
        Assert.Equal(1234, actual.Geometry.WindowWidth);
        Assert.Equal(876, actual.Geometry.WindowHeight);
        Assert.Equal(37, actual.Geometry.WindowX);
        Assert.Equal(53, actual.Geometry.WindowY);
        Assert.Equal(flag, actual.Geometry.Maximized);
        // ApplySettings captures actual pixel-rounded pane dimensions before each preference change.
        Assert.Equal(.42, actual.Geometry.TopScriptRatio, precision: mutated || reopened ? 2 : 8);
        Assert.Equal(.73, actual.Geometry.RightScriptRatio, precision: mutated || reopened ? 2 : 8);
        Assert.Equal(451, actual.Geometry.DebuggerWidth);
        Assert.Equal(321, actual.Geometry.CommandsWidth);
    }
}
