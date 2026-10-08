#if POWERSHELL_ISE
using Avalonia.Controls;
using Avalonia.Styling;
using Devolutions.Terminal.Settings;
using Iseberg;
using Iseberg.Core;

namespace Devolutions.Terminal.App.Views;

public sealed class PowerShellIseTab : Decorator, IHostedTerminalContent
{
    private readonly string? profileId;
    private Task? initializationTask;
    public PowerShellIseTab(ProfileSettings profile, TermControl? terminal = null, Control? terminalView = null,
        Guid? workspaceId = null, string? stateDirectory = null)
    {
        ValidateProfile(profile);
        profileId = profile.Guid;
        var themeName = IsebergThemes.Canonicalize(profile.IseColorTheme);
        WorkspaceId = workspaceId ?? Guid.NewGuid();
        if (WorkspaceId == Guid.Empty) throw new ArgumentException("A nonempty workspace identity is required.", nameof(workspaceId));
        var root = stateDirectory ?? Path.Combine(SettingsService.SettingsDirectory, "PowerShellIse");
        if (!Path.IsPathFullyQualified(root))
            throw new ArgumentException("An absolute state directory is required.", nameof(stateDirectory));
        StateDirectory = Path.Combine(root, "Workspaces", WorkspaceId.ToString("N"));

        Terminal = terminal ?? new TermControl();
        var console = new DtIsebergConsole(Terminal, terminalView ?? Terminal, profile);
        WorkbenchControl? control = null;
        Workbench = control = new WorkbenchControl(new WorkbenchOptions
        {
            Console = console,
            ThemeProvider = dark => ResolveTheme(control?.SelectedThemePreset ?? themeName, dark),
            ThemeChoices = EditorThemePresets.OriginalBuiltIns().Concat(
                new[] { EditorThemePresets.Classic(), EditorThemePresets.Dark(), EditorThemePresets.Light(),
                    new EditorTheme { Name = IsebergThemes.FollowDt } }).ToArray(),
            EnablePersistence = true,
            SettingsPath = Path.Combine(StateDirectory, "settings.json"),
            ShowSessionTabs = false,
            FontSizesInDips = true,
            EnableUpdateChecks = false,
            EnableCommandsPane = true,
            EnableDebuggerPane = true,
            StartingDirectory = profile.ExpandStartingDirectory(),
            SnippetDirectory = Path.Combine(root, "Snippets"),
            ApplyHostPreferences = settings =>
            {
                settings.ThemePreset = themeName;
                settings.FontFamily = profile.FontFace;
                settings.FontSize = profile.FontSize;
                settings.LoadProfiles = profile.IseLoadProfiles;
                settings.ShowLineNumbers = profile.IseShowLineNumbers;
                settings.WordWrap = profile.IseWordWrap;
                settings.PromptToSaveBeforeRun = profile.IsePromptToSaveBeforeRun;
                settings.AutoSaveMinutes = profile.IseAutoSaveMinutes;
                settings.ConsoleIntelliSense = profile.IseConsoleIntelliSense;
                settings.ConsoleCompletionOnEnter = profile.IseConsoleCompletionOnEnter;
                settings.ScriptIntelliSense = profile.IseScriptIntelliSense;
                settings.ScriptCompletionOnEnter = profile.IseScriptCompletionOnEnter;
                settings.IntelliSenseTimeoutSeconds = profile.IseIntelliSenseTimeoutSeconds;
                settings.ShowOutlining = profile.IseShowOutlining;
                settings.WarnDuplicateFiles = profile.IseWarnDuplicateFiles;
                settings.UseLocalHelp = profile.IseUseLocalHelp;
                settings.UseDefaultSnippets = profile.IseUseDefaultSnippets;
                settings.RecentFileCount = profile.IseRecentFileCount;
                settings.ShowToolbar = profile.IseShowToolbar;
            },
            Preferences = new UserSettings
            {
                ThemePreset = themeName,
                LoadProfiles = profile.IseLoadProfiles,
                CheckForUpdates = false,
                ShowCommands = false,
                FontFamily = profile.FontFace,
                FontSize = profile.FontSize,
                ShowLineNumbers = profile.IseShowLineNumbers,
                WordWrap = profile.IseWordWrap,
                PromptToSaveBeforeRun = profile.IsePromptToSaveBeforeRun,
                AutoSaveMinutes = profile.IseAutoSaveMinutes,
            },
        });
        var scope = new ThemeVariantScope
        {
            RequestedThemeVariant = themeName == IsebergThemes.FollowDt ? ThemeVariant.Default :
                ResolveTheme(themeName, false).Colors["Script.Background"] is "#012456" or "#000000" or "#1E1E1E"
                    ? ThemeVariant.Dark : ThemeVariant.Light,
            Child = Workbench,
        };
        Child = scope;
        Workbench.ThemeSelectionChanged += (_, _) =>
        {
            var selected = Workbench.SelectedThemePreset ?? themeName;
            scope.RequestedThemeVariant = selected == IsebergThemes.FollowDt ? ThemeVariant.Default :
                ResolveTheme(selected, false).Colors["Script.Background"] is "#012456" or "#000000" or "#1E1E1E"
                    ? ThemeVariant.Dark : ThemeVariant.Light;
        };
    }

    internal static void ValidateProfile(ProfileSettings profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Kind != ProfileKind.PowerShellIse)
            throw new ArgumentException("A PowerShell ISE profile is required.", nameof(profile));
        if (!IsebergThemes.IsSupported(IsebergThemes.Canonicalize(profile.IseColorTheme)))
            throw new NotSupportedException($"Unsupported Iseberg color theme '{profile.IseColorTheme}'. Select a supported theme in profile settings.");
        if (profile.Elevate)
            throw new NotSupportedException("PowerShell ISE runs with DT's permissions. Start DT as administrator to use an elevated workbench.");
        if (!string.IsNullOrWhiteSpace(profile.Commandline) || profile.ConnectionType is not null || profile.Environment.Count > 0)
            throw new NotSupportedException("PowerShell ISE profiles do not support terminal command lines, connection types, or process environment overrides.");
    }

    public static EditorTheme ResolveTheme(string name, bool dark) => IsebergThemes.Canonicalize(name) switch
    {
        IsebergThemes.Classic => EditorThemePresets.Classic(),
        IsebergThemes.Dark => EditorThemePresets.Dark(),
        IsebergThemes.Light => EditorThemePresets.Light(),
        IsebergThemes.FollowDt => dark ? EditorThemePresets.Dark() : EditorThemePresets.Light(),
        var original when EditorThemePresets.OriginalNames.Contains(original) => EditorThemePresets.Original(original),
        _ => throw new NotSupportedException($"Unsupported Iseberg color theme '{name}'.")
    };

    public WorkbenchControl Workbench { get; }
    public Guid WorkspaceId { get; }
    public string StateDirectory { get; }
    public TermControl Terminal { get; }
    public bool IsTerminalActive => Workbench.IsNativeConsoleActive;
    public bool IsDisposed => Workbench.IsDisposed;
    public Task InitializeAsync()
    {
        var initialization = Workbench.InitializeAsync();
        return initializationTask ??= InitializeCoreAsync(initialization);
    }
    private async Task InitializeCoreAsync(Task initialization)
    {
        await initialization;
        await PowerShellIseWorkspaceCatalog.RecordProfileAsync(StateDirectory, profileId);
    }
    public Task<bool> PrepareCloseAsync() => Workbench.PrepareCloseAsync();
    public void CancelClosePreparation() => Workbench.CancelClosePreparation();
    public Task<bool> RequestCloseAsync() => Workbench.RequestCloseAsync();
    public ValueTask DisposeAsync() => Workbench.DisposeAsync();
    public void FocusContent()
    {
        if (Workbench.IsStarted) Workbench.ScriptEditorView.FocusEditor();
    }
}
#endif
