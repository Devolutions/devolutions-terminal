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
}
