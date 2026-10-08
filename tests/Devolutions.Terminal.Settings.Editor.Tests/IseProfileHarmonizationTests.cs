using System.Text.Json.Nodes;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Devolutions.Terminal.Settings;
using Xunit;

namespace Devolutions.Terminal.Settings.Editor.Tests;

public sealed class IseProfileHarmonizationTests
{
    private const string Defaults = """{"profiles":{"defaults":{"font":{"face":"Cascadia Mono","size":12}},"list":[]},"schemes":[{"name":"Campbell"}]}""";
    private const string Id = "{11111111-1111-1111-1111-111111111111}";

    [Fact]
    public void NestedOptionsOverrideLegacyKeysAndAdvancedEditsPreserveUnknownFields()
    {
        var json = $$$$"""
            {"profiles":{"list":[{"guid":"{{{{Id}}}}","name":"ISE","type":"powershellIse","commandline":"",
            "ise.loadProfiles":true,"ise.colorTheme":"Dark",
            "ise":{"loadProfiles":false,"colorTheme":"Light","future":{"keep":42}}}]}}
            """;
        var editor = Editor(() => SettingsLoader.Load(Defaults, json), settings => json = SettingsLoader.SerializeUserDocument(settings));
        editor.SelectProfile(Id);
        var profile = Assert.IsType<ProfilesSettingsViewModel>(editor.CurrentPage).SelectedProfile!;
        Assert.False(profile.IseLoadProfiles);
        Assert.Equal(IsebergThemes.Light, profile.IseColorTheme);
        profile.IseColorTheme = IsebergThemes.Presentation;
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
        editor.Apply();
        Assert.False(editor.IsDirty);
        var saved = JsonNode.Parse(json)!["profiles"]!["list"]![0]!;
        Assert.False(saved["ise.loadProfiles"]!.GetValue<bool>());
        Assert.Equal(IsebergThemes.Presentation, saved["ise.colorTheme"]!.GetValue<string>());
        Assert.Equal(IsebergThemes.Presentation, saved["ise"]!["colorTheme"]!.GetValue<string>());
        Assert.Equal(42, saved["ise"]!["future"]!["keep"]!.GetValue<int>());
        var loaded = Assert.Single(SettingsLoader.Load(Defaults, json).Profiles);
        Assert.False(loaded.IseLoadProfiles);
        Assert.False(loaded.IseConsoleIntelliSense);
        Assert.False(loaded.IseConsoleCompletionOnEnter);
        Assert.False(loaded.IseScriptIntelliSense);
        Assert.False(loaded.IseScriptCompletionOnEnter);
        Assert.Equal(27, loaded.IseIntelliSenseTimeoutSeconds);
        Assert.False(loaded.IseShowOutlining);
        Assert.False(loaded.IseWarnDuplicateFiles);
        Assert.False(loaded.IseUseLocalHelp);
        Assert.False(loaded.IseUseDefaultSnippets);
        Assert.Equal(25, loaded.IseRecentFileCount);
        Assert.False(loaded.IseShowToolbar);
    }

    [Theory]
    [InlineData(-1, 0, 0, 1, -1, 0)]
    [InlineData(0, 0, 1, 1, 0, 0)]
    [InlineData(1, 1, 2, 2, 1, 1)]
    [InlineData(7, 7, 11, 11, 25, 25)]
    [InlineData(119, 119, 29, 29, 99, 99)]
    [InlineData(120, 120, 30, 30, 100, 100)]
    [InlineData(121, 120, 31, 30, 101, 100)]
    public void NumericOptionBoundariesSurviveSaveReload(
        int recovery, int expectedRecovery, int timeout, int expectedTimeout, int recent, int expectedRecent)
    {
        var json = $$$$"""
            {"profiles":{"list":[{"guid":"{{{{Id}}}}","name":"ISE","type":"powershellIse","commandline":"",
            "ise":{"autoSaveMinutes":{{{{recovery}}}},"intelliSenseTimeoutSeconds":{{{{timeout}}}},"recentFileCount":{{{{recent}}}}}}]}}
            """;
        var settings = SettingsLoader.Load(Defaults, json);
        var loaded = Assert.Single(settings.Profiles);
        Assert.Equal(expectedRecovery, loaded.IseAutoSaveMinutes);
        Assert.Equal(expectedTimeout, loaded.IseIntelliSenseTimeoutSeconds);
        Assert.Equal(expectedRecent, loaded.IseRecentFileCount);
        var reloaded = Assert.Single(SettingsLoader.Load(Defaults, SettingsLoader.SerializeUserDocument(settings)).Profiles);
        Assert.Equal(expectedRecovery, reloaded.IseAutoSaveMinutes);
        Assert.Equal(expectedTimeout, reloaded.IseIntelliSenseTimeoutSeconds);
        Assert.Equal(expectedRecent, reloaded.IseRecentFileCount);
    }

    [Fact]
    public void StandardProfileTypeConversionPreservesTerminalConfigurationAcrossSaveReloadAndSwitchBack()
    {
        var json = $$$$"""
            {"profiles":{"list":[{"guid":"{{{{Id}}}}","name":"My shell","commandline":"pwsh.exe -NoProfile",
            "connectionType":"{22222222-2222-2222-2222-222222222222}","elevate":true,
            "icon":"custom.png","environment":{"KEEP":"value","REMOVE":null},"future":{"keep":42}}]}}
            """;
        var editor = Editor(() => SettingsLoader.Load(Defaults, json), settings => json = SettingsLoader.SerializeUserDocument(settings));
        editor.SelectProfile(Id);
        var profile = Assert.IsType<ProfilesSettingsViewModel>(editor.CurrentPage).SelectedProfile!;
        profile.ProfileType = "PowerShell ISE (Iseberg)";
        Assert.True(profile.IsPowerShellIse);
        Assert.False(profile.IsTerminal);
        Assert.Empty(profile.Commandline);
        Assert.False(profile.Elevate);
        profile.IseWordWrap = true;
        profile.IseShowLineNumbers = false;
        profile.IsePromptToSaveBeforeRun = false;
        profile.IseAutoSaveMinutes = 7;
        profile.IseColorTheme = IsebergThemes.Presentation;
        editor.Apply();
        Assert.False(editor.IsDirty);
        Assert.Equal(Id, Assert.IsType<ProfilesSettingsViewModel>(editor.CurrentPage).SelectedProfile!.Guid);
        var saved = JsonNode.Parse(json)!["profiles"]!["list"]![0]!;
        Assert.Equal("powershellIse", saved["type"]!.GetValue<string>());
        Assert.True(saved["ise"]!["wordWrap"]!.GetValue<bool>());
        Assert.Equal("value", saved["ise"]!["terminal"]!["environment"]!["KEEP"]!.GetValue<string>());
        Assert.Equal(42, saved["future"]!["keep"]!.GetValue<int>());
        var ise = Assert.Single(SettingsLoader.Load(Defaults, json).Profiles);
        Assert.Null(ise.ConnectionType);
        Assert.Empty(ise.Environment);
        Assert.Equal("custom.png", ise.Icon);
        Assert.False(ise.IseShowLineNumbers);
        Assert.False(ise.IsePromptToSaveBeforeRun);
        Assert.Equal(7, ise.IseAutoSaveMinutes);
        Assert.Equal(IsebergThemes.Presentation, ise.IseColorTheme);

        editor = Editor(() => SettingsLoader.Load(Defaults, json), settings => json = SettingsLoader.SerializeUserDocument(settings));
        editor.SelectProfile(Id);
        profile = Assert.IsType<ProfilesSettingsViewModel>(editor.CurrentPage).SelectedProfile!;
        profile.ProfileType = "Terminal";
        editor.Apply();
        var terminal = Assert.Single(SettingsLoader.Load(Defaults, json).Profiles);
        Assert.Equal(ProfileKind.Terminal, terminal.Kind);
        Assert.Equal("pwsh.exe -NoProfile", terminal.Commandline);
        Assert.True(terminal.Elevate);
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), Guid.Parse(terminal.ConnectionType!));
        Assert.Equal("value", terminal.Environment["KEEP"]);
        Assert.Null(terminal.Environment["REMOVE"]);
        Assert.Equal("custom.png", terminal.Icon);
        Assert.Equal(Id, terminal.Guid);
        Assert.True(terminal.IseWordWrap);
    }

    [Fact]
    public void DefaultsEditsActualDefaultsAndProfileSelectionFollowsIntoAppearance()
    {
        var settings = SettingsLoader.Load(Defaults, $$$$"""{"profiles":{"list":[{"guid":"{{{{Id}}}}","name":"Named","commandline":"pwsh.exe"}]}}""");
        var editor = Editor(() => settings);
        editor.SelectProfile(Id);
        editor.SelectPage(SettingsPage.ProfileAppearance);
        var appearance = Assert.IsType<ProfileAppearanceSettingsViewModel>(editor.CurrentPage);
        Assert.Equal(Id, appearance.SelectedProfile!.Guid);
        editor.SelectPage(SettingsPage.Profiles);
        var defaults = Assert.IsType<ProfilesSettingsViewModel>(editor.CurrentPage).SelectedProfile!;
        Assert.True(defaults.IsDefaults);
        Assert.Equal("Defaults", defaults.Name);
        Assert.False(defaults.CanChangeProfileType);
        defaults.FontFace = "Consolas";
        defaults.ProfileType = "PowerShell ISE (Iseberg)";
        Assert.Equal("Consolas", settings.ProfileDefaults.FontFace);
        Assert.Equal(ProfileKind.Terminal, settings.ProfileDefaults.Kind);
        Assert.Equal("Cascadia Mono", Assert.Single(settings.Profiles).FontFace);
        Assert.Equal("Named", Assert.Single(settings.Profiles).Name);
        editor.SelectPage(SettingsPage.ProfileAppearance);
        Assert.True(Assert.IsType<ProfileAppearanceSettingsViewModel>(editor.CurrentPage).SelectedProfile!.IsDefaults);
    }

    [Fact]
    public void InvalidEnvironmentDraftBlocksTypeConversionWithoutLosingDraftOrIdentity()
    {
        var editor = Editor(() => SettingsLoader.Load(Defaults, $$$$"""{"profiles":{"list":[{"guid":"{{{{Id}}}}","name":"Named","commandline":"pwsh.exe"}]}}"""));
        editor.SelectProfile(Id);
        var profile = Assert.IsType<ProfilesSettingsViewModel>(editor.CurrentPage).SelectedProfile!;
        profile.EnvironmentJson = "{ invalid";
        profile.ProfileType = "PowerShell ISE (Iseberg)";
        Assert.Equal("Terminal", profile.ProfileType);
        Assert.Equal("pwsh.exe", profile.Commandline);
        Assert.Equal("{ invalid", profile.EnvironmentJson);
        Assert.Equal(Id, profile.Guid);
        Assert.Contains("invalid", editor.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnavailableHostCannotConvertOrCreateIseButKeepsExistingIseSettings()
    {
        var settings = SettingsLoader.Load(Defaults, $$$$"""{"profiles":{"list":[{"guid":"{{{{Id}}}}","name":"ISE","type":"powershellIse","commandline":"","ise":{"wordWrap":true}}]}}""");
        var editor = new SettingsEditorViewModel(() => settings, _ => { }, () => SettingsLoader.Load(Defaults), supportsPowerShellIse: false);
        editor.SelectProfile(Id);
        var profile = Assert.IsType<ProfilesSettingsViewModel>(editor.CurrentPage).SelectedProfile!;
        Assert.False(profile.CanChangeProfileType);
        profile.ProfileType = "Terminal";
        editor.AddPowerShellIseProfile();
        Assert.Single(settings.Profiles);
        Assert.True(profile.IsPowerShellIse);
        Assert.True(profile.IseWordWrap);
    }

    [AvaloniaFact]
    public void NormalAddProfileOffersIseTypeAndSidebarUsesLogoWithoutSeparateCreationButton()
    {
        var editor = Editor(() => SettingsLoader.Load(Defaults));
        editor.AddProfileCommand.Execute(null);
        var profile = Assert.IsType<ProfilesSettingsViewModel>(editor.CurrentPage).SelectedProfile!;
        Assert.Contains("PowerShell ISE (Iseberg)", profile.ProfileTypeChoices);
        profile.ProfileType = "PowerShell ISE (Iseberg)";
        Assert.True(editor.SelectedNavigationItem!.HasImageIcon);
        Assert.NotNull(editor.SelectedNavigationItem.ImageIcon);
        var view = new SettingsView(editor);
        var window = new Window { Content = view, Width = 1000, Height = 900 };
        window.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.DoesNotContain(view.GetLogicalDescendants().OfType<Button>(),
                button => AutomationProperties.GetName(button) == "Add Iseberg profile");
            Assert.Single(view.GetLogicalDescendants().OfType<Button>(),
                button => AutomationProperties.GetName(button) == "Add a new profile");
            Assert.Single(view.GetLogicalDescendants().OfType<ComboBox>(),
                combo => AutomationProperties.GetName(combo) == "Profile type");
        }
        finally { window.Close(); }
    }

    private static SettingsEditorViewModel Editor(Func<AppSettings> load, Action<AppSettings>? save = null) =>
        new(load, save ?? (_ => { }), () => SettingsLoader.Load(Defaults));
}
