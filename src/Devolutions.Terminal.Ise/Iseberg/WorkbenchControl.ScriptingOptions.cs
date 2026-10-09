using Avalonia.Threading;
using Iseberg.Core;

namespace Iseberg;

public sealed partial class WorkbenchControl
{
    private readonly SemaphoreSlim scriptingSettingsGate = new(1, 1);
    private readonly HashSet<Task> scriptingSettingsSaves = [];
    private Func<UserSettings, CancellationToken, Task>? scriptingSettingsPersistence;

    /// <summary>
    /// Host-owned persistence for scripting preference changes. Receives a detached snapshot on
    /// the UI thread; must not invoke the executing runspace. Script setters await failures;
    /// UI setters report asynchronous failures through the workbench's error handling.
    /// When absent, enabled workbench persistence is used; otherwise preferences are in-memory.
    /// </summary>
    public Func<UserSettings, CancellationToken, Task>? ScriptingSettingsPersistence
    {
        get => IseInvoke(() => scriptingSettingsPersistence);
        set => IseInvoke(() => { scriptingSettingsPersistence = value; return true; });
    }

    /// <summary>Detached current preferences for host-owned settings wiring.</summary>
    public UserSettings GetScriptingSettings() => IseInvoke(() => settings.Copy());

    /// <summary>
    /// Await on the UI thread during disposal, after rejecting new mutations and before releasing
    /// the host's storage lease. Waits for every pending save, including canceled/faulted saves.
    /// </summary>
    public Task DrainScriptingSettingsPersistenceAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        return Task.WhenAll(scriptingSettingsSaves.ToArray());
    }

    /// <summary>Call after host/UI preference changes to notify scripting observers.</summary>
    public void NotifyScriptingSettingsChanged() => IseInvoke(() =>
    {
        Scripting.NotifySettingsChanged();
        return true;
    });

    private void ChangeIseSettings(Action<UserSettings> change, Action? validateOwner = null)
    {
        var callerIsUi = Dispatcher.UIThread.CheckAccess();
        var task = IseInvoke(() =>
        {
            VerifyAvailable();
            validateOwner?.Invoke();
            var updated = settings.Copy();
            change(updated);
            updated.Normalize();
            settings = updated;
            ApplySettings();
            PopulateRecentMenu();
            NotifyScriptingSettingsChanged();
            var cancellation = windowCancellation.Token;
            var pending = TrackIseSettingsSaveAsync(settings.Copy(), cancellation);
            if (!callerIsUi) return pending;
            // UI setters apply synchronously, but cannot wait on a save's UI continuation.
            _ = ObserveIseSettingsSaveAsync(pending, cancellation);
            return Task.CompletedTask;
        });
        task.GetAwaiter().GetResult();
    }

    private Task ObserveIseSettingsSaveAsync(Task pending, CancellationToken cancellation) => GuardAsync(async () =>
    {
        try { await pending; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    });

    private async Task TrackIseSettingsSaveAsync(UserSettings snapshot, CancellationToken cancellation)
    {
        var pending = PersistIseSettingsAsync(snapshot, cancellation);
        scriptingSettingsSaves.Add(pending);
        try { await pending; }
        finally { scriptingSettingsSaves.Remove(pending); }
    }

    private async Task PersistIseSettingsAsync(UserSettings snapshot, CancellationToken cancellation)
    {
        await scriptingSettingsGate.WaitAsync(cancellation);
        try
        {
            VerifyAvailable();
            if (scriptingSettingsPersistence is { } persist)
                await persist(snapshot, cancellation);
            else
                await SaveSettingsAsync();
            cancellation.ThrowIfCancellationRequested();
        }
        finally { scriptingSettingsGate.Release(); }
    }

    /// <summary>
    /// Portable ISE preferences using Iseberg's units and ranges. Autosave preferences do not
    /// enable host-disabled recovery. Themes and profile loading remain host-owned.
    /// </summary>
    public sealed class IseOptions : ObservableModel
    {
        private readonly WorkbenchControl owner;
        internal IseOptions(WorkbenchControl owner) => this.owner = owner;
        private T Read<T>(Func<UserSettings, T> read) => owner.IseInvoke(() => read(owner.settings));
        private void Write(Action<UserSettings> change) => owner.ChangeIseSettings(change);
        internal void NotifyChanged() => Changed("");

        private static void Range(double value, double minimum, double maximum, string name)
        {
            if (!double.IsFinite(value) || value < minimum || value > maximum)
                throw new ArgumentOutOfRangeException(name, value, $"Specify a value from {minimum} through {maximum}.");
        }

        public string FontName
        {
            get => Read(s => s.FontFamily);
            set => Write(s => { ArgumentException.ThrowIfNullOrWhiteSpace(value); s.FontFamily = value; });
        }
        public double FontSize
        {
            get => Read(s => s.FontSize);
            set => Write(s => { Range(value, 6, 72, nameof(FontSize)); s.FontSize = value; });
        }
        public double Zoom
        {
            get => Read(s => s.Zoom);
            set => Write(s => { Range(value, 20, 400, nameof(Zoom)); s.Zoom = value; });
        }
        public string SelectedScriptPaneState
        {
            get => Read(s => s.Layout);
            set => Write(s =>
            {
                if (value is not ("Top" or "Right" or "Maximized"))
                    throw new ArgumentException("Specify Top, Right, or Maximized.", nameof(SelectedScriptPaneState));
                if (!owner.hostingOptions.ShowScriptPane ||
                    value != "Maximized" && !owner.hostingOptions.ShowConsolePane)
                    throw new NotSupportedException("This pane layout is disabled by the workbench host.");
                s.Layout = value;
            });
        }
        public bool ShowLineNumbers { get => Read(s => s.ShowLineNumbers); set => Write(s => s.ShowLineNumbers = value); }
        public bool ShowOutlining { get => Read(s => s.ShowOutlining); set => Write(s => s.ShowOutlining = value); }
        public bool WordWrap { get => Read(s => s.WordWrap); set => Write(s => s.WordWrap = value); }
        public bool ShowToolBar
        {
            get => Read(s => owner.hostingOptions.ShowToolbar && s.ShowToolbar);
            set => Write(s =>
            {
                if (value && !owner.hostingOptions.ShowToolbar)
                    throw new NotSupportedException("The toolbar is disabled by the workbench host.");
                s.ShowToolbar = value;
            });
        }
        public bool ShowWarningBeforeSavingOnRun { get => Read(s => s.PromptToSaveBeforeRun); set => Write(s => s.PromptToSaveBeforeRun = value); }
        public bool ShowWarningForDuplicateFiles { get => Read(s => s.WarnDuplicateFiles); set => Write(s => s.WarnDuplicateFiles = value); }
        public bool ShowDefaultSnippets { get => Read(s => s.UseDefaultSnippets); set => Write(s => s.UseDefaultSnippets = value); }
        public bool ShowIntellisenseInScriptPane { get => Read(s => s.ScriptIntelliSense); set => Write(s => s.ScriptIntelliSense = value); }
        public bool ShowIntellisenseInConsolePane
        {
            get => Read(s => s.ConsoleIntelliSense);
            set => Write(s => s.ConsoleIntelliSense = value);
        }
        public bool UseEnterToSelectInScriptPaneIntellisense { get => Read(s => s.ScriptCompletionOnEnter); set => Write(s => s.ScriptCompletionOnEnter = value); }
        public bool UseEnterToSelectInConsolePaneIntellisense
        {
            get => Read(s => s.ConsoleCompletionOnEnter);
            set => Write(s => s.ConsoleCompletionOnEnter = value);
        }
        public int IntellisenseTimeoutInSeconds
        {
            get => Read(s => s.IntelliSenseTimeoutSeconds);
            set => Write(s => { Range(value, 1, 30, nameof(IntellisenseTimeoutInSeconds)); s.IntelliSenseTimeoutSeconds = value; });
        }
        public int AutoSaveMinuteInterval
        {
            get => Read(s => s.AutoSaveMinutes);
            set => Write(s => { Range(value, 0, 120, nameof(AutoSaveMinuteInterval)); s.AutoSaveMinutes = value; });
        }
        public int MruCount
        {
            get => Read(s => s.RecentFileCount);
            set => Write(s => { Range(value, 0, 100, nameof(MruCount)); s.RecentFileCount = value; });
        }
        public bool UseLocalHelp { get => Read(s => s.UseLocalHelp); set => Write(s => s.UseLocalHelp = value); }
        public bool FixedWidthFontsOnly { get => Read(s => s.FixedWidthFontsOnly); set => Write(s => s.FixedWidthFontsOnly = value); }
        public bool LoadProfiles
        {
            get => Read(s => s.LoadProfiles);
            set => owner.IseInvoke<bool>(() => throw new NotSupportedException("Profile loading is owned by DT's PowerShell ISE profile."));
        }
        public EditorTheme Theme
        {
            get => Read(s => s.Copy().Theme);
            set => owner.IseInvoke<bool>(() => throw new NotSupportedException("Choose the theme through the host's settings UI."));
        }
        public object TokenColors
        {
            get => UnsupportedTokenColors();
            set => RejectThemeChange();
        }
        public object ConsoleTokenColors
        {
            get => UnsupportedTokenColors();
            set => RejectThemeChange();
        }
        public object XmlTokenColors
        {
            get => UnsupportedTokenColors();
            set => RejectThemeChange();
        }
        public void RestoreDefaults() => Write(s =>
        {
            var defaults = new UserSettings();
            // Theme, ThemePreset, and LoadProfiles remain host-owned, including during this reset.
            s.FontFamily = defaults.FontFamily;
            s.FontSize = defaults.FontSize;
            s.Zoom = defaults.Zoom;
            s.ShowLineNumbers = defaults.ShowLineNumbers;
            s.ShowOutlining = defaults.ShowOutlining;
            s.WordWrap = defaults.WordWrap;
            s.ShowToolbar = defaults.ShowToolbar;
            s.WarnDuplicateFiles = defaults.WarnDuplicateFiles;
            s.PromptToSaveBeforeRun = defaults.PromptToSaveBeforeRun;
            s.UseDefaultSnippets = defaults.UseDefaultSnippets;
            s.ConsoleIntelliSense = defaults.ConsoleIntelliSense;
            s.ConsoleCompletionOnEnter = defaults.ConsoleCompletionOnEnter;
            s.ScriptIntelliSense = defaults.ScriptIntelliSense;
            s.ScriptCompletionOnEnter = defaults.ScriptCompletionOnEnter;
            s.IntelliSenseTimeoutSeconds = defaults.IntelliSenseTimeoutSeconds;
            s.AutoSaveMinutes = defaults.AutoSaveMinutes;
            s.RecentFileCount = defaults.RecentFileCount;
            s.UseLocalHelp = defaults.UseLocalHelp;
            s.FixedWidthFontsOnly = defaults.FixedWidthFontsOnly;
        });

        public void RestoreDefaultTokenColors() => RejectThemeChange();
        public void RestoreDefaultConsoleTokenColors() => RejectThemeChange();
        public void RestoreDefaultXmlTokenColors() => RejectThemeChange();
        private void RejectThemeChange() => owner.IseInvoke<bool>(() => throw new NotSupportedException("Token colors are owned by the host's theme settings."));
        private object UnsupportedTokenColors() => owner.IseInvoke<object>(() =>
            throw new NotSupportedException("ISE token-color dictionaries are not exposed; choose colors through the host's theme settings."));
    }
}
