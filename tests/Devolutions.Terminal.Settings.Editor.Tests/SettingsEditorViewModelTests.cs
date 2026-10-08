using System.Text.Json.Nodes;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Devolutions.Terminal.Settings;
using Devolutions.Terminal.Settings.Editor.Controls;
using Xunit;

namespace Devolutions.Terminal.Settings.Editor.Tests;

public sealed class SettingsEditorViewModelTests
{
    private const string Defaults = """
        {
          "initialCols": 80,
          "initialRows": 30,
          "profiles": {
            "defaults": { "font": { "face": "Cascadia Mono", "size": 12 } },
            "list": [
              {
                "guid": "{11111111-1111-1111-1111-111111111111}",
                "name": "PowerShell",
                "commandline": "pwsh.exe"
              }
            ]
          },
          "schemes": [
            {
              "name": "Campbell",
              "foreground": "#CCCCCC",
              "background": "#0C0C0C"
            }
          ],
          "newTabMenu": [ { "type": "remainingProfiles" } ],
          "actions": [],
          "keybindings": []
        }
        """;

    [Fact]
    public void SearchFiltersToMatchingSettingsPages()
    {
        var viewModel = CreateEditor();

        viewModel.SearchText = "kitty";

        var page = Assert.Single(viewModel.VisibleNavigationItems);
        Assert.Equal(SettingsPage.ProfileAdvanced, page.Page);
        Assert.Same(page, viewModel.SelectedNavigationItem);
    }

    [Fact]
    public void ClosingSearchClearsHiddenNavigationFilter()
    {
        var viewModel = CreateEditor();
        viewModel.IsSearchOpen = true;
        viewModel.SearchText = "kitty";

        Assert.Single(viewModel.VisibleNavigationItems);

        viewModel.IsSearchOpen = false;

        Assert.Empty(viewModel.SearchText);
        Assert.Equal(14, viewModel.VisibleNavigationItems.Count);
    }

    [Fact]
    public void AddProfilePreservesPendingJsonEditsAndRejectsInvalidJson()
    {
        var viewModel = CreateEditor();
        viewModel.SelectPage(SettingsPage.Actions);
        var actions = Assert.IsType<ActionsSettingsViewModel>(viewModel.CurrentPage);
        var edited = actions.ActionsJson.Replace(
            "]",
            """{ "command": "closePane", "keys": "ctrl+shift+q" }]""",
            StringComparison.Ordinal);
        actions.ActionsJson = edited;

        viewModel.AddProfile();

        Assert.Contains(viewModel.VisibleNavigationItems, item => item.Title == "New profile");

        viewModel.SelectPage(SettingsPage.Actions);
        var rebuilt = Assert.IsType<ActionsSettingsViewModel>(viewModel.CurrentPage);
        Assert.Contains("closePane", rebuilt.ActionsJson, StringComparison.Ordinal);

        rebuilt.ActionsJson = "{ invalid";
        var profilesBefore = viewModel.VisibleNavigationItems.Count;

        viewModel.AddProfile();

        Assert.Equal(profilesBefore, viewModel.VisibleNavigationItems.Count);
        Assert.Contains("invalid JSON", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddProfileAppendsUserProfileToNavigation()
    {
        var viewModel = CreateEditor();
        var before = viewModel.VisibleNavigationItems.Count;

        viewModel.AddProfile();

        Assert.Equal(before + 1, viewModel.VisibleNavigationItems.Count);
        Assert.Equal("New profile", viewModel.SelectedNavigationItem?.Title);
        Assert.True(viewModel.IsDirty);
    }

    [Fact]
    public void StartupUsesFriendlyProfileChoicesAndStateLabels()
    {
        var viewModel = CreateEditor();
        var startup = Assert.IsType<StartupSettingsViewModel>(viewModel.CurrentPage);

        var profile = Assert.Single(startup.Profiles);
        Assert.Equal("PowerShell", profile.Name);
        Assert.Same(profile, startup.SelectedProfile);
        Assert.Equal("Off", startup.CenterOnLaunchState);

        startup.SelectedProfile = profile;
        startup.CenterOnLaunch = true;

        Assert.Equal("{11111111-1111-1111-1111-111111111111}", startup.DefaultProfile);
        Assert.Equal("On", startup.CenterOnLaunchState);
        Assert.True(viewModel.IsDirty);
    }

    [Fact]
    public void ApplyPersistsTypedChangeAndUnknownUserProperties()
    {
        var persisted = """
            {
              "futureSetting": { "preserve": true },
              "initialCols": 90,
              "profiles": { "list": [] }
            }
            """;
        var saveCount = 0;
        var viewModel = CreateEditor(
            () => SettingsLoader.Load(Defaults, persisted),
            settings =>
            {
                saveCount++;
                persisted = SettingsLoader.SerializeUserDocument(settings);
            });
        var startup = Assert.IsType<StartupSettingsViewModel>(viewModel.CurrentPage);

        startup.InitialColumns = 132;
        viewModel.Apply();

        Assert.Equal(1, saveCount);
        Assert.False(viewModel.IsDirty);
        var saved = Assert.IsType<JsonObject>(JsonNode.Parse(persisted));
        Assert.Equal(132, saved["initialCols"]!.GetValue<int>());
        Assert.True(saved["futureSetting"]!["preserve"]!.GetValue<bool>());
    }

    [Fact]
    public void RevertReloadsPersistedValues()
    {
        var persisted = """{ "initialCols": 91, "profiles": { "list": [] } }""";
        var viewModel = CreateEditor(() => SettingsLoader.Load(Defaults, persisted));
        var startup = Assert.IsType<StartupSettingsViewModel>(viewModel.CurrentPage);
        startup.InitialColumns = 150;

        viewModel.Revert();

        var reverted = Assert.IsType<StartupSettingsViewModel>(viewModel.CurrentPage);
        Assert.Equal(91, reverted.InitialColumns);
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public void DefaultResetLoadsDefaultsAndRemainsDirtyUntilApply()
    {
        var viewModel = CreateEditor(
            () => SettingsLoader.Load(Defaults, """{ "initialCols": 140 }"""));

        viewModel.ResetToDefaults();

        var startup = Assert.IsType<StartupSettingsViewModel>(viewModel.CurrentPage);
        Assert.Equal(80, startup.InitialColumns);
        Assert.True(viewModel.IsDirty);
    }

    [Fact]
    public void InvalidActionJsonBlocksSave()
    {
        var saveCount = 0;
        var viewModel = CreateEditor(save: _ => saveCount++);
        viewModel.SelectPage(SettingsPage.Actions);
        var actions = Assert.IsType<ActionsSettingsViewModel>(viewModel.CurrentPage);
        actions.ActionsJson = "{ invalid";

        viewModel.Apply();

        Assert.Equal(0, saveCount);
        Assert.True(viewModel.IsDirty);
        Assert.Contains("invalid JSON", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProfileSelectionRaisesPropertyChanged()
    {
        var profiles = new[]
        {
            new ProfileItemViewModel(ProfileSettings.CreatePowerShell(), () => { }),
            new ProfileItemViewModel(ProfileSettings.CreateCmd(), () => { }),
        };
        var page = new ProfilesSettingsViewModel(profiles);
        var changes = new List<string?>();
        page.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        page.SelectedProfile = profiles[1];

        Assert.Contains(nameof(ProfilesSettingsViewModel.SelectedProfile), changes);
    }

    [Fact]
    public void ExternalRevisionChangeBlocksSave()
    {
        var revision = "one";
        var saveCount = 0;
        var viewModel = new SettingsEditorViewModel(
            () => SettingsLoader.Load(Defaults),
            _ => saveCount++,
            () => SettingsLoader.Load(Defaults),
            () => revision);
        var startup = Assert.IsType<StartupSettingsViewModel>(viewModel.CurrentPage);
        startup.InitialColumns = 100;
        revision = "two";

        viewModel.Apply();

        Assert.Equal(0, saveCount);
        Assert.Contains("changed on disk", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownNewTabEntryIsPreservedWhenOtherSettingsChange()
    {
        var persisted = """
            {
              "newTabMenu": [
                { "type": "futureProviderEntry", "payload": { "keep": 42 } }
              ],
              "profiles": { "list": [] }
            }
            """;
        var viewModel = CreateEditor(
            () => SettingsLoader.Load(Defaults, persisted),
            settings => persisted = SettingsLoader.SerializeUserDocument(settings));
        var startup = Assert.IsType<StartupSettingsViewModel>(viewModel.CurrentPage);
        startup.InitialColumns = 123;

        viewModel.Apply();

        var saved = Assert.IsType<JsonObject>(JsonNode.Parse(persisted));
        Assert.Equal(
            42,
            saved["newTabMenu"]![0]!["payload"]!["keep"]!.GetValue<int>());
    }

    [Fact]
    public void EditingUnsupportedNewTabEntryIsRejectedInsteadOfDropped()
    {
        var saveCount = 0;
        var viewModel = CreateEditor(
            () => SettingsLoader.Load(
                Defaults,
                """{ "newTabMenu": [ { "type": "futureProviderEntry", "payload": 1 } ] }"""),
            _ => saveCount++);
        viewModel.SelectPage(SettingsPage.NewTabMenu);
        var menu = Assert.IsType<NewTabMenuSettingsViewModel>(viewModel.CurrentPage);
        menu.Json = menu.Json.Replace("\"payload\": 1", "\"payload\": 2", StringComparison.Ordinal);

        viewModel.Apply();

        Assert.Equal(0, saveCount);
        Assert.Contains("unsupported type", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MalformedFolderPayloadIsRejectedInsteadOfErased()
    {
        var saveCount = 0;
        var viewModel = CreateEditor(save: _ => saveCount++);
        viewModel.SelectPage(SettingsPage.NewTabMenu);
        var menu = Assert.IsType<NewTabMenuSettingsViewModel>(viewModel.CurrentPage);
        menu.Json = """[ { "type": "folder", "name": "Broken", "entries": { "future": true } } ]""";

        viewModel.Apply();

        Assert.Equal(0, saveCount);
        Assert.Contains("must be an array", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void WindowConstructsWithCompiledXaml()
    {
        var viewModel = CreateEditor();

        var window = new SettingsWindow(viewModel);

        Assert.Same(viewModel, window.DataContext);
    }

    [Fact]
    public void StartupAndAppearanceChoicesIncludeWindowsTerminalValues()
    {
        var settings = SettingsLoader.Load(Defaults);
        var startup = new StartupSettingsViewModel(settings, () => { });
        var appearance = new AppearanceSettingsViewModel(settings, () => { });

        Assert.Contains("persistedLayout", startup.FirstWindowPreferenceChoices);
        Assert.Contains("useAnyExisting", startup.WindowingBehaviorChoices);
        Assert.Contains("useExistingOrCreate", startup.WindowingBehaviorChoices);
        Assert.Contains("afterCurrentTab", appearance.NewTabPositionChoices);
        Assert.Contains("atEnd", appearance.NewTabPositionChoices);
    }

    [AvaloniaTheory]
    [InlineData(SettingsPage.Appearance, "Disable animations")]
    [InlineData(SettingsPage.Appearance, "Acrylic tab row")]
    [InlineData(SettingsPage.ProfileAppearance, "Use acrylic")]
    [InlineData(SettingsPage.Compatibility, "Enable unfocused acrylic")]
    [InlineData(SettingsPage.Extensions, "Language")]
    public void UnsupportedTogglesAreDisabledAndExplained(SettingsPage page, string header)
    {
        var editor = CreateEditor();
        editor.SelectPage(page);
        var view = new SettingsView(editor);
        var content = Assert.Single(view.DataTemplates, template => template.Match(editor.CurrentPage)).Build(editor.CurrentPage)!;
        content.DataContext = editor.CurrentPage;
        var row = Assert.Single(content.GetLogicalDescendants().OfType<SettingsRow>(),
            row => row.Header == header);
        Assert.Contains("Not supported", row.Description, StringComparison.Ordinal);
        Assert.False(Assert.IsAssignableFrom<Control>(row.Value).IsEnabled);
    }

    [AvaloniaFact]
    public void AdministratorProfileToggleIsAvailableOnWindows()
    {
        var editor = CreateEditor();
        editor.SelectPage(SettingsPage.Profiles);
        var view = new SettingsView(editor);
        var content = Assert.Single(view.DataTemplates, template => template.Match(editor.CurrentPage)).Build(editor.CurrentPage)!;
        content.DataContext = editor.CurrentPage;
        var row = Assert.Single(content.GetLogicalDescendants().OfType<SettingsRow>(),
            candidate => candidate.Header == "Run this profile as Administrator");
        var toggle = Assert.IsType<SettingsToggle>(row.Value);

        Assert.Equal(OperatingSystem.IsWindows(), toggle.IsEnabled);
        Assert.Contains("gsudo", row.Description, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void AdminShieldToggleIsAvailableOnWindows()
    {
        var editor = CreateEditor();
        editor.SelectPage(SettingsPage.Extensions);
        var view = new SettingsView(editor);
        var content = Assert.Single(view.DataTemplates, template => template.Match(editor.CurrentPage)).Build(editor.CurrentPage)!;
        content.DataContext = editor.CurrentPage;
        var row = Assert.Single(content.GetLogicalDescendants().OfType<SettingsRow>(),
            candidate => candidate.Header == "Show admin shield");

        Assert.Equal(OperatingSystem.IsWindows(), Assert.IsType<SettingsToggle>(row.Value).IsEnabled);
    }

    [AvaloniaFact]
    public void UnsupportedMeasurementChoicesAreDisabledWithoutChangingSavedValues()
    {
        var settings = SettingsLoader.Load(Defaults,
            """{ "compatibility.textMeasurement": "wcswidth", "compatibility.ambiguousWidth": "wide" }""");
        var editor = CreateEditor(() => settings);
        editor.SelectPage(SettingsPage.Compatibility);
        var view = new SettingsView(editor);
        var content = Assert.Single(view.DataTemplates, template => template.Match(editor.CurrentPage)).Build(editor.CurrentPage)!;
        content.DataContext = editor.CurrentPage;
        Assert.Equal(2, content.GetLogicalDescendants().OfType<ComboBox>().Count(combo => !combo.IsEnabled));
        Assert.Equal("wcswidth", settings.TextMeasurement);
        Assert.Equal("wide", settings.AmbiguousWidth);
    }

    [AvaloniaFact]
    public void SettingsToggleShowsStateTextAndRoundTripsBinding()
    {
        var toggle = new SettingsToggle();
        var panel = Assert.IsType<StackPanel>(toggle.Content);
        var state = Assert.IsType<TextBlock>(panel.Children[0]);
        var switchControl = Assert.IsType<ToggleSwitch>(panel.Children[1]);

        Assert.Equal("Off", state.Text);
        Assert.False(switchControl.IsChecked);

        toggle.IsChecked = true;

        Assert.Equal("On", state.Text);
        Assert.True(switchControl.IsChecked);

        switchControl.IsChecked = false;

        Assert.False(toggle.IsChecked);
        Assert.Equal("Off", state.Text);
    }

    [AvaloniaFact]
    public void ColorFieldRoundTripsHexAndUpdatesSwatch()
    {
        var field = new ColorField { Text = "#E74856" };
        var layout = Assert.IsType<DockPanel>(field.Content);
        var host = Assert.IsType<Panel>(layout.Children[0]);
        var swatch = Assert.IsType<Border>(host.Children[0]);

        Assert.Equal("#E74856", field.Text);
        Assert.Equal(Color.Parse("#E74856"), Assert.IsType<SolidColorBrush>(swatch.Background).Color);

        field.Text = "#16C60C";
        Assert.Equal(Color.Parse("#16C60C"), Assert.IsType<SolidColorBrush>(swatch.Background).Color);
    }

    [AvaloniaFact]
    public void FontFaceFieldKeepsConfiguredFaceAndListsSystemFonts()
    {
        var field = new FontFaceField { Text = "Cascadia Mono" };

        Assert.Equal("Cascadia Mono", field.Text);
        Assert.Contains("Cascadia Mono", field.Fonts);
    }

    [AvaloniaFact]
    public void FilePathFieldRoundTripsSelectedPath()
    {
        var field = new FilePathField { Text = "/tmp/icon.png", ImageFiles = true, Title = "Select icon" };

        Assert.Equal("/tmp/icon.png", field.Text);
        Assert.True(field.ImageFiles);
        Assert.Equal("Select icon", field.Title);
    }

    [AvaloniaFact]
    public void FilePathFieldSupportsFolderPickerMode()
    {
        var field = new FilePathField
        {
            Text = "%USERPROFILE%",
            Folders = true,
            Title = "Select starting directory",
        };

        Assert.True(field.Folders);
        Assert.Equal("%USERPROFILE%", field.Text);
    }

    [AvaloniaFact]
    public void ProfileAppearancePageUsesPickers()
    {
        var editor = CreateEditor();
        editor.SelectPage(SettingsPage.ProfileAppearance);
        var view = new SettingsView(editor);
        var content = Assert.Single(view.DataTemplates, template => template.Match(editor.CurrentPage)).Build(editor.CurrentPage)!;
        content.DataContext = editor.CurrentPage;

        Assert.NotEmpty(content.GetLogicalDescendants().OfType<FontFaceField>());
        Assert.NotEmpty(content.GetLogicalDescendants().OfType<ColorField>());
        Assert.NotEmpty(content.GetLogicalDescendants().OfType<FilePathField>());
    }

    [AvaloniaFact]
    public void SettingsRowKeepsLabelsAndRightSideValue()
    {
        var value = new TextBox();
        var row = new SettingsRow
        {
            Header = "Command line",
            Description = "Executable used when this profile starts.",
            Value = value,
        };

        Assert.Equal("Command line", AutomationProperties.GetName(row));
        Assert.Equal("Command line", AutomationProperties.GetName(value));
        Assert.NotNull(AutomationProperties.GetLabeledBy(value));
        Assert.Same(value, row.Value);
    }

    private static SettingsEditorViewModel CreateEditor(
        Func<AppSettings>? load = null,
        Action<AppSettings>? save = null) =>
        new(
            load ?? (() => SettingsLoader.Load(Defaults)),
            save ?? (_ => { }),
            () => SettingsLoader.Load(Defaults));

    [Fact]
    public void AddPowerShellIseProfileCreatesDistinctIsebergProfile()
    {
        var editor = CreateEditor();
        editor.AddPowerShellIseProfile();
        var first = editor.SelectedNavigationItem!.Profile!;
        Assert.Same(first, Assert.IsType<ProfilesSettingsViewModel>(editor.CurrentPage).SelectedProfile);
        editor.AddPowerShellIseProfileCommand.Execute(null);
        var second = editor.SelectedNavigationItem!.Profile!;
        Assert.Same(second, Assert.IsType<ProfilesSettingsViewModel>(editor.CurrentPage).SelectedProfile);
        Assert.All(new[] { first, second }, profile =>
        {
            Assert.Equal("Iseberg", profile.Name);
            Assert.True(profile.IsPowerShellIse);
            Assert.False(profile.IsTerminal);
            Assert.NotEqual(Guid.Empty, Guid.Parse(profile.Guid!));
        });
        Assert.NotEqual(Guid.Parse(first.Guid!), Guid.Parse(second.Guid!));
        Assert.True(editor.IsDirty);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ApplyReloadPreservesIseAndExistingPowerShellProfiles(bool existing, bool option)
    {
        var persisted = existing
            ? """{"profiles":{"list":[{"guid":"{22222222-2222-2222-2222-222222222222}","name":"Iseberg","type":"powershellIse","commandline":"","ise.loadProfiles":false}]}}"""
            : """{"profiles":{"list":[]}}""";
        var saveCount = 0;
        var editor = CreateEditor(() => SettingsLoader.Load(Defaults, persisted), settings =>
        {
            saveCount++;
            persisted = SettingsLoader.SerializeUserDocument(settings);
        });
        if (existing)
            editor.SelectedNavigationItem = editor.VisibleNavigationItems.Single(item => item.Profile?.IsPowerShellIse == true);
        else
            editor.AddPowerShellIseProfile();
        var item = editor.SelectedNavigationItem!.Profile!;
        var id = Guid.Parse(item.Guid!);
        item.Name = "Edited Iseberg";
        item.IseLoadProfiles = option;

        editor.Apply();

        Assert.Equal(1, saveCount);
        Assert.False(editor.IsDirty);
        var json = JsonNode.Parse(persisted)!;
        var saved = json["profiles"]!["list"]!.AsArray().Single(p => Guid.Parse(p!["guid"]!.GetValue<string>()) == id)!;
        Assert.Equal("powershellIse", saved["type"]!.GetValue<string>());
        if (existing)
        {
            Assert.Equal(option, saved["ise.loadProfiles"]!.GetValue<bool>());
            if (option)
                Assert.True(saved["ise"]!["loadProfiles"]!.GetValue<bool>());
            else
                Assert.Null(saved["ise"]?["loadProfiles"]);
        }
        else
        {
            Assert.Equal(option, saved["ise"]!["loadProfiles"]!.GetValue<bool>());
            Assert.False(saved.AsObject().ContainsKey("ise.loadProfiles"));
        }
        Assert.Equal("Edited Iseberg", saved["name"]!.GetValue<string>());
        var restoredNavigation = editor.VisibleNavigationItems.Single(p => p.Profile is { IsNamedProfile: true } && Guid.Parse(p.Profile.Guid!) == id);
        editor.SelectedNavigationItem = restoredNavigation;
        var restored = restoredNavigation.Profile!;
        Assert.NotSame(item, restored);
        Assert.Same(restored, Assert.IsType<ProfilesSettingsViewModel>(editor.CurrentPage).SelectedProfile);
        Assert.True(restored.IsPowerShellIse);
        Assert.False(restored.IsTerminal);
        Assert.Equal(option, restored.IseLoadProfiles);
        Assert.Equal("Edited Iseberg", restored.Name);
        var terminal = editor.VisibleNavigationItems.Single(p => p.Profile?.Guid == "{11111111-1111-1111-1111-111111111111}").Profile!;
        Assert.True(terminal.IsTerminal);
        Assert.False(terminal.IsPowerShellIse);
        Assert.Equal("PowerShell", terminal.Name);
        Assert.False(terminal.IseLoadProfiles);
        Assert.Equal("pwsh.exe", terminal.Commandline);
        var model = SettingsLoader.Load(Defaults, persisted);
        var preservedPowerShell = model.Profiles.Single(p => Guid.Parse(p.Guid!) == Guid.Parse(terminal.Guid!));
        Assert.Equal(ProfileKind.Terminal, preservedPowerShell.Kind);
        Assert.Equal("PowerShell", preservedPowerShell.Name);
        Assert.False(preservedPowerShell.IseLoadProfiles);
        Assert.Equal("Cascadia Mono", preservedPowerShell.FontFace);
        Assert.Equal(12, preservedPowerShell.FontSize);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddProfileStillCreatesTerminalProfile(bool command)
    {
        var persisted = """{"profiles":{"list":[]}}""";
        var editor = CreateEditor(() => SettingsLoader.Load(Defaults, persisted),
            settings => persisted = SettingsLoader.SerializeUserDocument(settings));
        if (command) editor.AddProfileCommand.Execute(null);
        else editor.AddProfile();
        var item = editor.SelectedNavigationItem!.Profile!;
        var id = Guid.Parse(item.Guid!);
        Assert.True(item.IsTerminal);
        Assert.False(item.IsPowerShellIse);
        Assert.Same(item, Assert.IsType<ProfilesSettingsViewModel>(editor.CurrentPage).SelectedProfile);
        editor.Apply();
        var restored = editor.VisibleNavigationItems.Single(p => p.Profile is { IsNamedProfile: true } && Guid.Parse(p.Profile.Guid!) == id).Profile!;
        Assert.True(restored.IsTerminal);
        Assert.False(restored.IsPowerShellIse);
        Assert.Equal(ProfileKind.Terminal, SettingsLoader.Load(Defaults, persisted).Profiles.Single(p => Guid.Parse(p.Guid!) == id).Kind);
    }

    [Theory]
    [InlineData("Classic ISE")]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("Follow DT")]
    public void ApplyReloadPreservesSelectedIseThemeWithoutChangingPowerShell(string theme)
    {
        var persisted = """{"profiles":{"list":[]}}""";
        var editor = CreateEditor(() => SettingsLoader.Load(Defaults, persisted),
            settings => persisted = SettingsLoader.SerializeUserDocument(settings));
        editor.AddPowerShellIseProfile();
        var id = Guid.Parse(editor.SelectedNavigationItem!.Profile!.Guid!);
        editor.Apply();
        var item = editor.VisibleNavigationItems.Single(entry => entry.Profile is { IsNamedProfile: true } && Guid.Parse(entry.Profile.Guid!) == id).Profile!;
        Assert.Equal("Classic ISE", item.IseColorTheme);
        Assert.Equal(new[]
        {
            "Dark Console, Light Editor (default)", "Light Console, Dark Editor", "Dark Console, Dark Editor",
            "Light Console, Light Editor", "Monochrome Green", "Presentation",
            "Classic ISE", "Dark", "Light", "Follow DT"
        }, item.IseColorThemeChoices);
        Assert.False(editor.IsDirty);
        item.IseColorTheme = theme;
        Assert.Equal(theme != "Classic ISE", editor.IsDirty);
        editor.Apply();
        Assert.False(editor.IsDirty);
        var saved = JsonNode.Parse(persisted)!["profiles"]!["list"]!.AsArray()
            .Single(profile => Guid.Parse(profile!["guid"]!.GetValue<string>()) == id)!;
        Assert.Equal(theme, saved["ise"]!["colorTheme"]!.GetValue<string>());
        Assert.Equal("powershellIse", saved["type"]!.GetValue<string>());
        var restored = editor.VisibleNavigationItems.Single(entry => entry.Profile is { IsNamedProfile: true } && Guid.Parse(entry.Profile.Guid!) == id).Profile!;
        Assert.Equal(theme, restored.IseColorTheme);
        Assert.True(restored.IsPowerShellIse);
        Assert.False(restored.IseLoadProfiles);
        var powerShell = SettingsLoader.Load(Defaults, persisted).Profiles.Single(profile =>
            profile.Guid == "{11111111-1111-1111-1111-111111111111}");
        Assert.Equal(ProfileKind.Terminal, powerShell.Kind);
        Assert.Equal("pwsh.exe", powerShell.Commandline);
        Assert.Equal("Classic ISE", powerShell.IseColorTheme);
    }

    [Theory]
    [InlineData(ProfileKind.PowerShellIse, "PowerShell ISE (Iseberg)", true, false)]
    [InlineData(ProfileKind.Terminal, "Terminal", false, true)]
    [InlineData(ProfileKind.Unsupported, "Unsupported profile type", false, false)]
    public void IsePageFlagsAndOptInNotifyOnlyOnChange(ProfileKind kind, string label, bool ise, bool terminal)
    {
        var model = new ProfileSettings { Kind = kind };
        var changes = 0;
        var notifications = new List<string?>();
        var page = new ProfileItemViewModel(model, () => changes++);
        page.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        Assert.Equal(label, page.ProfileType);
        Assert.Equal(ise, page.IsPowerShellIse);
        Assert.Equal(terminal, page.IsTerminal);
        Assert.Equal(OperatingSystem.IsWindows() && terminal, page.CanElevate);
        page.IseLoadProfiles = true;
        Assert.True(model.IseLoadProfiles);
        Assert.Equal(1, changes);
        Assert.Single(notifications, p => p == nameof(ProfileItemViewModel.IseLoadProfiles));
        page.IseLoadProfiles = true;
        Assert.Equal(1, changes);
        Assert.Single(notifications);
        page.IseLoadProfiles = false;
        Assert.False(model.IseLoadProfiles);
        Assert.Equal(2, changes);
        Assert.Equal(2, notifications.Count);
    }

    [Theory]
    [InlineData("Dark Console, Light Editor (default)")]
    [InlineData("Light Console, Dark Editor")]
    [InlineData("Dark Console, Dark Editor")]
    [InlineData("Light Console, Light Editor")]
    [InlineData("Monochrome Green")]
    [InlineData("Presentation")]
    public void OriginalBuiltInThemesAppearInProfileSelectionAndApplyReload(string theme)
    {
        var persisted = """{"profiles":{"list":[]}}""";
        var saves = 0;
        var editor = CreateEditor(() => SettingsLoader.Load(Defaults, persisted), settings =>
        {
            saves++;
            persisted = SettingsLoader.SerializeUserDocument(settings);
        });
        editor.AddPowerShellIseProfile();
        var selected = editor.SelectedNavigationItem!.Profile!;
        var id = selected.Guid;
        Assert.Equal(new[]
        {
            "Dark Console, Light Editor (default)", "Light Console, Dark Editor", "Dark Console, Dark Editor",
            "Light Console, Light Editor", "Monochrome Green", "Presentation",
            "Classic ISE", "Dark", "Light", "Follow DT"
        }, selected.IseColorThemeChoices);
        selected.IseColorTheme = theme;
        selected.IseLoadProfiles = true;
        editor.Apply();
        Assert.Equal(1, saves);
        Assert.False(editor.IsDirty);
        var restored = editor.VisibleNavigationItems.Single(item => item.Profile?.Guid == id).Profile!;
        Assert.NotSame(selected, restored);
        Assert.Equal(theme, restored.IseColorTheme);
        Assert.True(restored.IsPowerShellIse);
        Assert.True(restored.IseLoadProfiles);
        restored.IseColorTheme = theme;
        Assert.False(editor.IsDirty);
        var ordinary = editor.VisibleNavigationItems.Single(item =>
            item.Profile?.Guid == "{11111111-1111-1111-1111-111111111111}").Profile!;
        Assert.True(ordinary.IsTerminal);
        Assert.Equal("PowerShell", ordinary.Name);
        Assert.Equal("pwsh.exe", ordinary.Commandline);
        var saved = JsonNode.Parse(persisted)!["profiles"]!["list"]!.AsArray().Single(node =>
            node!["guid"]!.GetValue<string>() == id)!;
        Assert.Equal(theme, saved["ise"]!["colorTheme"]!.GetValue<string>());
    }
}
