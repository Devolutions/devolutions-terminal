using Devolutions.Terminal.Settings;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Devolutions.Terminal.Settings.Editor;

public sealed record SettingsDiagnosticViewModel(string Severity, string Code, string Message, string? Source);

public sealed class SettingsNavigationItem : ObservableObject
{
    private static readonly Lazy<Bitmap> IseIcon = new(() =>
    {
        using var stream = AssetLoader.Open(new Uri("avares://Devolutions.Terminal.Settings.Editor/Assets/iseberg.scale-100.png"));
        return new Bitmap(stream);
    });
    private ProfileItemViewModel? _profile;
    public required SettingsPage Page { get; init; }
    public required string Icon { get; init; }
    public required string IconFontFamily { get; init; }
    public required string Title { get; init; }
    public string GroupHeader { get; init; } = string.Empty;
    public bool HasGroupHeader => !string.IsNullOrEmpty(GroupHeader);
    public required string Keywords { get; init; }
    public required object ViewModel { get; init; }
    public ProfileItemViewModel? Profile
    {
        get => _profile;
        init
        {
            _profile = value;
            if (value is not null) value.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName is nameof(ProfileItemViewModel.ProfileType) or nameof(ProfileItemViewModel.Icon))
                {
                    OnPropertyChanged(nameof(HasImageIcon));
                    OnPropertyChanged(nameof(ImageIcon));
                }
            };
        }
    }
    public bool HasImageIcon => Profile is { IsPowerShellIse: true } profile &&
        (string.IsNullOrWhiteSpace(profile.Icon) || profile.Icon is "\uE943" or ProfileSettings.PowerShellIseIcon);
    public Bitmap? ImageIcon => HasImageIcon ? IseIcon.Value : null;

    public override string ToString() => Title;
}

public sealed class SettingsEditorViewModel : ObservableObject
{
    private readonly Func<AppSettings> _load;
    private readonly Action<AppSettings> _save;
    private readonly Func<AppSettings> _createDefault;
    private readonly Func<string?> _getRevision;
    private AppSettings _settings;
    private string? _loadedRevision;
    private IReadOnlyList<SettingsNavigationItem> _navigationItems = [];
    private IReadOnlyList<SettingsNavigationItem> _visibleNavigationItems = [];
    private IReadOnlyList<ProfileItemViewModel> _profiles = [];
    private ProfileItemViewModel? _profileDefaults;
    private SettingsNavigationItem? _selectedNavigationItem;
    private string _searchText = string.Empty;
    private bool _isSearchOpen;
    private bool _isDirty;
    private string _statusMessage = "Settings loaded.";
    private IReadOnlyList<SettingsDiagnosticViewModel> _diagnostics = [];
    private ActionsSettingsViewModel? _actions;
    private NewTabMenuSettingsViewModel? _newTabMenu;

    public SettingsEditorViewModel()
        : this(
            SettingsService.Load,
            SettingsService.Save,
            SettingsService.CreateDefault,
            ReadSettingsRevision)
    {
    }

    public SettingsEditorViewModel(
        Func<AppSettings> load,
        Action<AppSettings> save,
        Func<AppSettings> createDefault,
        Func<string?>? getRevision = null,
        bool supportsPowerShellIse = true)
    {
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _createDefault = createDefault ?? throw new ArgumentNullException(nameof(createDefault));
        _getRevision = getRevision ?? (() => null);
        _settings = _load();
        SupportsPowerShellIse = supportsPowerShellIse;
        _loadedRevision = _getRevision();
        ApplyCommand = new(Apply);
        RevertCommand = new(Revert);
        ResetCommand = new(ResetToDefaults);
        OpenJsonCommand = new(OpenJsonFile);
        AddProfileCommand = new(AddProfile);
        AddPowerShellIseProfileCommand = new(AddPowerShellIseProfile, () => SupportsPowerShellIse);
        BuildPages(SettingsPage.Startup);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                FilterNavigation();
            }
        }
    }

    public bool IsSearchOpen
    {
        get => _isSearchOpen;
        set
        {
            if (SetProperty(ref _isSearchOpen, value) && !value)
            {
                SearchText = string.Empty;
            }
        }
    }

    public IReadOnlyList<SettingsNavigationItem> VisibleNavigationItems
    {
        get => _visibleNavigationItems;
        private set => SetProperty(ref _visibleNavigationItems, value);
    }

    public SettingsNavigationItem? SelectedNavigationItem
    {
        get => _selectedNavigationItem;
        set
        {
            if (SetProperty(ref _selectedNavigationItem, value))
            {
                if (value?.Profile is { } profile &&
                    value.ViewModel is ProfilesSettingsViewModel profilesPage)
                {
                    profilesPage.SelectedProfile = profile;
                    foreach (var item in _navigationItems)
                    {
                        if (item.ViewModel is ProfileAppearanceSettingsViewModel appearance) appearance.SelectedProfile = profile;
                        if (item.ViewModel is ProfileTerminalSettingsViewModel terminal) terminal.SelectedProfile = profile;
                        if (item.ViewModel is ProfileAdvancedSettingsViewModel advanced) advanced.SelectedProfile = profile;
                    }
                }

                OnPropertyChanged(nameof(CurrentPage));
            }
        }
    }

    public object? CurrentPage => SelectedNavigationItem?.ViewModel;
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
            {
                ApplyCommand.RaiseCanExecuteChanged();
                RevertCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public IReadOnlyList<SettingsDiagnosticViewModel> Diagnostics { get => _diagnostics; private set => SetProperty(ref _diagnostics, value); }
    public string SettingsPath => SettingsService.SettingsPath;
    public RelayCommand ApplyCommand { get; }
    public RelayCommand RevertCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand OpenJsonCommand { get; }
    public RelayCommand AddProfileCommand { get; }
    public RelayCommand AddPowerShellIseProfileCommand { get; }
    public bool SupportsPowerShellIse { get; }

    public void SelectPage(SettingsPage page)
    {
        var item = _navigationItems.FirstOrDefault(candidate => candidate.Page == page);
        if (item is not null)
        {
            SelectedNavigationItem = item;
        }
    }

    public void SelectProfile(string guid)
    {
        var item = _navigationItems.FirstOrDefault(candidate =>
            candidate.Profile is { IsNamedProfile: true } profile &&
            string.Equals(profile.Guid, guid, StringComparison.OrdinalIgnoreCase));
        if (item is not null) SelectedNavigationItem = item;
    }

    public void Apply()
    {
        if (!string.Equals(_loadedRevision, _getRevision(), StringComparison.Ordinal))
        {
            StatusMessage = "Settings changed on disk after this editor loaded them. Revert to reload before applying.";
            return;
        }

        if (!TryCommitEditors(out var error))
        {
            StatusMessage = error!;
            return;
        }

        try
        {
            _save(_settings);
            _settings = _load();
            _loadedRevision = _getRevision();
            var selectedPage = SelectedNavigationItem?.Page ?? SettingsPage.Startup;
            BuildPages(selectedPage);
            IsDirty = false;
            StatusMessage = $"Saved atomically to {SettingsService.SettingsPath}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusMessage = $"Save failed: {ex.Message}";
        }
    }

    public void Revert()
    {
        _settings = _load();
        _loadedRevision = _getRevision();
        var selectedPage = SelectedNavigationItem?.Page ?? SettingsPage.Startup;
        BuildPages(selectedPage);
        IsDirty = false;
        StatusMessage = "Unsaved changes reverted.";
    }

    public void ResetToDefaults()
    {
        _settings = _createDefault();
        var selectedPage = SelectedNavigationItem?.Page ?? SettingsPage.Startup;
        BuildPages(selectedPage);
        MarkDirty();
        StatusMessage = "Factory defaults loaded. Apply to save them.";
    }

    public void AddProfile() => AddProfile(new ProfileSettings
    {
        Guid = $"{{{Guid.NewGuid()}}}",
        Name = "New profile",
        Commandline = UnixShellCommandline.DefaultNewProfileCommandline(),
        Origin = SettingsOrigin.User,
    });

    public void AddPowerShellIseProfile()
    {
        if (SupportsPowerShellIse) AddProfile(ProfileSettings.CreatePowerShellIse());
    }

    private void AddProfile(ProfileSettings profile)
    {
        if (!TryCommitEditors(out var error))
        {
            StatusMessage = error!;
            return;
        }

        _settings.Profiles.Add(profile);
        MarkDirty();
        BuildPages(SettingsPage.Profiles);
        var created = _navigationItems.FirstOrDefault(item =>
            item.Profile is not null &&
            string.Equals(item.Profile.Guid, profile.Guid, StringComparison.OrdinalIgnoreCase));
        if (created is not null)
        {
            SelectedNavigationItem = created;
        }

        StatusMessage = "New profile added. Apply to save it.";
    }

    private void OpenJsonFile()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                _save(_load());
                _loadedRevision = _getRevision();
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = SettingsPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            StatusMessage = $"Could not open settings.json: {ex.Message}";
        }
    }

    private bool TryCommitEditors(out string? error)
    {
        if (!_actions!.TryCommit(out error) || !_newTabMenu!.TryCommit(out error))
        {
            return false;
        }

        foreach (var profile in _profiles)
        {
            if (!profile.TryCommitEnvironment(out error))
            {
                return false;
            }
        }
        if (_profileDefaults is not null && !_profileDefaults.TryCommitEnvironment(out error)) return false;

        error = null;
        return true;
    }

    private void BuildPages(SettingsPage selectedPage)
    {
        var selectedGuid = SelectedNavigationItem?.Profile?.Guid;
        _profiles = _settings.Profiles
            .Select(profile => new ProfileItemViewModel(profile, MarkDirty, SupportsPowerShellIse, PrepareProfileTypeChange))
            .ToArray();
        var defaults = new ProfileItemViewModel(_settings.ProfileDefaults, MarkDirty, SupportsPowerShellIse);
        _profileDefaults = defaults;
        var editableProfiles = new[] { defaults }.Concat(_profiles).ToArray();
        _actions = new(_settings, MarkDirty);
        _newTabMenu = new(_settings, MarkDirty);
        var profilesPage = new ProfilesSettingsViewModel(editableProfiles);
        var items = new List<SettingsNavigationItem>
        {
            Item(SettingsPage.Startup, "Startup", "launch default profile window position startup actions", new StartupSettingsViewModel(_settings, MarkDirty)),
            Item(SettingsPage.Interaction, "Interaction", "copy paste selection mouse urls focus", new InteractionSettingsViewModel(_settings, MarkDirty)),
            Item(SettingsPage.Appearance, "Appearance", "global themes tabs acrylic mica visual", new AppearanceSettingsViewModel(_settings, MarkDirty)),
            Item(SettingsPage.ColorSchemes, "Color schemes", "palette ansi foreground background cursor selection", new ColorSchemesSettingsViewModel(_settings, MarkDirty)),
            Item(SettingsPage.Rendering, "Rendering", "graphics api software invalidation", new RenderingSettingsViewModel(_settings, MarkDirty)),
            Item(SettingsPage.Compatibility, "Compatibility", "text measurement width input headless acrylic", new CompatibilitySettingsViewModel(_settings, MarkDirty)),
            Item(SettingsPage.Actions, "Actions", "keybindings key chord command json", _actions),
            Item(SettingsPage.NewTabMenu, "New Tab Menu", "menu folder separator profile action json", _newTabMenu),
            Item(SettingsPage.Extensions, "Extensions", "sources fragments experimental language notification", new ExtensionsSettingsViewModel(_settings, MarkDirty)),
            Item(SettingsPage.Profiles, "Defaults", "profile commandline directory icon tab title hidden", profilesPage, "Profiles", defaults),
        };
        var windows = OperatingSystem.IsWindows();
        foreach (var profile in _profiles)
        {
            items.Add(new SettingsNavigationItem
            {
                Page = SettingsPage.Profiles,
                IconFontFamily = windows ? "Segoe Fluent Icons" : "Cascadia Mono",
                Icon = NavigationIcon(profile.Icon, windows),
                Title = profile.Name,
                Keywords = $"profile {profile.Name} {profile.Commandline}",
                ViewModel = profilesPage,
                Profile = profile,
            });
        }

        items.Add(Item(SettingsPage.ProfileAppearance, "Profile appearance", "profile font colors opacity background image ise theme editor", new ProfileAppearanceSettingsViewModel(editableProfiles)));
        items.Add(Item(SettingsPage.ProfileTerminal, "Profile terminal", "profile scrollback cursor close antialiasing", new ProfileTerminalSettingsViewModel(editableProfiles)));
        items.Add(Item(SettingsPage.ProfileAdvanced, "Profile advanced", "profile vt environment kitty osc compatibility", new ProfileAdvancedSettingsViewModel(editableProfiles)));
        _navigationItems = items;
        Diagnostics = _settings.Diagnostics
            .Select(diagnostic => new SettingsDiagnosticViewModel(
                diagnostic.Severity.ToString(),
                diagnostic.Code,
                diagnostic.Message,
                diagnostic.Source))
            .ToArray();
        FilterNavigation();
        SelectedNavigationItem =
            _navigationItems.FirstOrDefault(item => item.Page == selectedPage && selectedGuid is not null &&
                string.Equals(item.Profile?.Guid, selectedGuid, StringComparison.OrdinalIgnoreCase)) ??
            _navigationItems.FirstOrDefault(item => item.Page == selectedPage) ??
            _navigationItems[0];
        OnPropertyChanged(nameof(SettingsPath));
    }

    private bool PrepareProfileTypeChange()
    {
        if (TryCommitEditors(out var error)) return true;
        StatusMessage = error!;
        return false;
    }

    private static string NavigationIcon(string? icon, bool windows)
    {
        if (string.IsNullOrWhiteSpace(icon) ||
            icon.Contains("://", StringComparison.Ordinal) ||
            icon.Contains('\\') ||
            icon.Contains('/') ||
            icon.Length > 4)
        {
            return windows ? "\uE756" : ">";
        }

        return icon;
    }

    private static SettingsNavigationItem Item(
        SettingsPage page,
        string title,
        string keywords,
        object viewModel,
        string groupHeader = "",
        ProfileItemViewModel? profile = null)
    {
        var windows = OperatingSystem.IsWindows();
        return new()
        {
            Page = page,
            Profile = profile,
            IconFontFamily = windows ? "Segoe Fluent Icons" : "Cascadia Mono",
            Icon = windows ? page switch
            {
                SettingsPage.Startup => "\uE7B5",
                SettingsPage.Interaction => "\uE8D4",
                SettingsPage.Appearance => "\uE790",
                SettingsPage.Profiles => "\uE77B",
                SettingsPage.ProfileAppearance => "\uE790",
                SettingsPage.ProfileTerminal => "\uE756",
                SettingsPage.ProfileAdvanced => "\uE90F",
                SettingsPage.ColorSchemes => "\uE2B1",
                SettingsPage.Actions => "\uE765",
                SettingsPage.NewTabMenu => "\uE8FD",
                SettingsPage.Rendering => "\uE7F8",
                SettingsPage.Compatibility => "\uE713",
                SettingsPage.Extensions => "\uE71B",
                _ => "\uE946",
            } : page switch
            {
                SettingsPage.Startup => ">",
                SettingsPage.Interaction => "<>",
                SettingsPage.Appearance => "*",
                SettingsPage.Profiles => "@",
                SettingsPage.ProfileAppearance => "+",
                SettingsPage.ProfileTerminal => "_",
                SettingsPage.ProfileAdvanced => "#",
                SettingsPage.ColorSchemes => "%",
                SettingsPage.Actions => "!",
                SettingsPage.NewTabMenu => "+",
                SettingsPage.Rendering => "=",
                SettingsPage.Compatibility => "?",
                SettingsPage.Extensions => "...",
                _ => ".",
            },
            Title = title,
            GroupHeader = groupHeader,
            Keywords = keywords,
            ViewModel = viewModel,
        };
    }

    private void FilterNavigation()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            VisibleNavigationItems = _navigationItems;
            return;
        }

        var terms = SearchText.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        VisibleNavigationItems = _navigationItems
            .Where(item => terms.All(term =>
                item.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.Keywords.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (VisibleNavigationItems.Count > 0 &&
            (SelectedNavigationItem is null || !VisibleNavigationItems.Contains(SelectedNavigationItem)))
        {
            SelectedNavigationItem = VisibleNavigationItems[0];
        }
    }

    private void MarkDirty()
    {
        IsDirty = true;
        StatusMessage = "Unsaved changes.";
    }

    private static string? ReadSettingsRevision()
    {
        try
        {
            return File.Exists(SettingsService.SettingsPath)
                ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(SettingsService.SettingsPath)))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"unavailable:{ex.HResult}";
        }
    }
}
