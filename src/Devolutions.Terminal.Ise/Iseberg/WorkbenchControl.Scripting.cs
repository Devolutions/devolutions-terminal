using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using Iseberg.Core;
using SessionState = Iseberg.Core.SessionState;

namespace Iseberg;

public sealed partial class WorkbenchControl
{
    private T IseInvoke<T>(Func<T> operation)
    {
        T Invoke()
        {
            if (windowClosed) throw new ObjectDisposedException(nameof(WorkbenchControl));
            if (closingInProgress || closePrepared) throw new InvalidOperationException("The workbench is closing.");
            return operation();
        }
        return Dispatcher.UIThread.CheckAccess() ? Invoke() : Dispatcher.UIThread.InvokeAsync(Invoke).GetAwaiter().GetResult();
    }

    private void RefreshIseMenus()
    {
        if (AddonsMenu is null || Scripting is null) return;
        foreach (var item in AddonsMenu.Items.OfType<MenuItem>().Where(item => item.Tag is IseMenuItem).ToArray())
            AddonsMenu.Items.Remove(item);
        if (Workbench.SelectedSession is not { } selected) return;
        foreach (var item in Scripting.Tab(selected).AddOnsMenu.Submenus.Items)
            AddonsMenu.Items.Add(CreateIseMenu(item));
    }

    private MenuItem CreateIseMenu(IseMenuItem item)
    {
        var menu = new MenuItem { Header = item.Name, InputGesture = item.Gesture, Tag = item };
        AutomationProperties.SetName(menu, item.Name.Replace("_", ""));
        menu.IsEnabled = item.Action is null || item.Tab.Model.Engine.State == SessionState.Ready && !item.Tab.Model.Engine.IsRemote;
        foreach (var child in item.Submenus.Items) menu.Items.Add(CreateIseMenu(child));
        menu.Click += async (_, e) =>
        {
            e.Handled = true;
            if (item.Action is not null) await GuardAsync(() => InvokeIseMenuAsync(item));
        };
        return menu;
    }

    private static void UpdateIseMenuState(MenuItem parent)
    {
        foreach (var menu in parent.Items.OfType<MenuItem>())
        {
            if (menu.Tag is IseMenuItem item)
                menu.IsEnabled = item.Action is null || item.Tab.Model.Engine.State == SessionState.Ready && !item.Tab.Model.Engine.IsRemote;
            UpdateIseMenuState(menu);
        }
    }

    private async Task InvokeIseMenuAsync(IseMenuItem item)
    {
        item.Tab.Check();
        if (!item.Tab.AddOnsMenu.Descendants().Contains(item))
            throw new InvalidOperationException("This Add-ons menu item has been removed.");
        if (item.Action is null) throw new InvalidOperationException("This menu is a submenu container, not an action.");
        await item.Tab.Model.Engine.ExecuteMenuActionAsync(item.Action);
        await RefreshDebuggerAsync(item.Tab.Model, reconcile: true);
        FlushOutput();
    }

    private async Task<bool> HandleIseShortcutAsync(KeyEventArgs e)
    {
        if (Workbench.SelectedSession is not { } session) return false;
        var item = Scripting.Tab(session).AddOnsMenu.Descendants().FirstOrDefault(item => item.Gesture?.Matches(e) == true);
        if (item is null) return false;
        e.Handled = true;
        await GuardAsync(() => InvokeIseMenuAsync(item));
        return true;
    }

    private bool IsIseShortcutReserved(KeyGesture gesture)
    {
        if (gesture.Key is Key.F1 or Key.F5 or Key.F6 or Key.F8 or Key.F9 or Key.F10 or Key.F11)
            return true;
        var control = (gesture.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        return ActionMenus().Any(item => item.Tag is string && item.InputGesture is { } existing &&
            (existing.Equals(gesture) || control && existing.Key == gesture.Key &&
                existing.KeyModifiers.HasFlag(KeyModifiers.Control))) ||
            control && gesture.Key is Key.Tab or Key.C or Key.V or Key.X or Key.Z or Key.Y or Key.Add or Key.Subtract;
    }

    public sealed class IseObjectModel : ObservableModel
    {
        internal WorkbenchControl Owner { get; }
        private readonly Dictionary<SessionModel, IsePowerShellTab> tabs = [];
        private readonly CancellationTokenRegistration lifetime;
        private bool detached;
        internal bool IsDetached => detached || Owner.windowClosed;
        internal IseObjectModel(WorkbenchControl owner)
        {
            Owner = owner;
            PowerShellTabs = new(this);
            Options = new(owner);
            owner.Workbench.Sessions.CollectionChanged += OnSessionsChanged;
            owner.Workbench.PropertyChanged += OnWorkbenchChanged;
            owner.ScriptEditor.TextArea.Caret.PositionChanged += OnEditorSelectionChanged;
            owner.ScriptEditor.TextArea.SelectionChanged += OnEditorSelectionChanged;
            owner.StatusText.PropertyChanged += OnStatusChanged;
            owner.ZoomSlider.PropertyChanged += OnZoomChanged;
            owner.ScriptPane.PropertyChanged += OnPaneChanged;
            owner.CommandsPane.PropertyChanged += OnPaneChanged;
            if (owner.SessionTabs.ItemTemplate is { } template)
                owner.SessionTabs.ItemTemplate = new IseSessionTemplate(template, this);
            // Workbench disposal cancels this lifetime even when Sessions retains its models.
            lifetime = owner.windowCancellation.Token.Register(Detach);
        }
        internal IsePowerShellTab Tab(SessionModel model)
        {
            ObjectDisposedException.ThrowIf(detached || Owner.windowClosed, this);
            if (!tabs.TryGetValue(model, out var tab)) tabs.Add(model, tab = new(Owner, model));
            return tab;
        }
        internal void Remove(SessionModel model)
        {
            if (tabs.Remove(model, out var tab)) tab.Detach();
        }
        private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs args)
        {
            if (IsDetached) return;
            PowerShellTabs.Notify(args, item => Tab((SessionModel)item));
            if (IsDetached) return;
            foreach (var model in tabs.Keys.Where(model => !Owner.Workbench.Sessions.Contains(model)).ToArray())
                Remove(model);
            foreach (var model in Owner.Workbench.Sessions) _ = Tab(model);
            NotifySelectionChanged();
        }
        private void OnWorkbenchChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (!IsDetached && args.PropertyName == nameof(WorkbenchModel.SelectedSession)) NotifySelectionChanged();
        }
        internal void NotifySelectionChanged()
        {
            if (IsDetached) return;
            Changed(nameof(CurrentPowerShellTab));
            Changed(nameof(CurrentFile));
            Changed(nameof(CurrentEditor));
        }
        private void OnEditorSelectionChanged(object? sender, EventArgs args)
        {
            if (!IsDetached && Owner.displayedSession is { } session && Owner.displayedFile is { } file)
                Tab(session).File(file).Editor.NotifyCaretChanged();
        }
        private void OnStatusChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs args)
        {
            if (!IsDetached && args.Property == TextBlock.TextProperty && Owner.displayedSession is { } session)
                Tab(session).NotifyStatusChanged();
        }
        private void OnZoomChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs args)
        {
            if (!IsDetached && args.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) NotifySettingsChanged();
        }
        private void OnPaneChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs args)
        {
            if (!IsDetached && args.Property == IsVisibleProperty) NotifySettingsChanged();
        }
        internal void NotifySettingsChanged()
        {
            if (IsDetached) return;
            Options.NotifyChanged();
            foreach (var tab in tabs.Values.ToArray()) tab.NotifySettingsChanged();
        }
        private void Detach()
        {
            if (detached) return;
            detached = true;
            Owner.Workbench.Sessions.CollectionChanged -= OnSessionsChanged;
            Owner.Workbench.PropertyChanged -= OnWorkbenchChanged;
            Owner.ScriptEditor.TextArea.Caret.PositionChanged -= OnEditorSelectionChanged;
            Owner.ScriptEditor.TextArea.SelectionChanged -= OnEditorSelectionChanged;
            Owner.StatusText.PropertyChanged -= OnStatusChanged;
            Owner.ZoomSlider.PropertyChanged -= OnZoomChanged;
            Owner.ScriptPane.PropertyChanged -= OnPaneChanged;
            Owner.CommandsPane.PropertyChanged -= OnPaneChanged;
            foreach (var tab in tabs.Values) tab.Detach();
            tabs.Clear();
            Owner.scriptingSettingsPersistence = null;
            lifetime.Unregister();
        }
        public IsePowerShellTab CurrentPowerShellTab => Owner.IseInvoke(() =>
            Tab(Owner.Workbench.SelectedSession ?? throw new InvalidOperationException("No PowerShell tab is selected.")));
        public IseFile? CurrentFile => CurrentPowerShellTab.SelectedFile;
        public IseEditor? CurrentEditor => CurrentFile?.Editor;
        public IsePowerShellTabCollection PowerShellTabs { get; }
        public IseOptions Options { get; }
        public object VisibleHorizontalAddOnTools => Owner.IseInvoke<object>(() => throw WpfNotSupported());
        public object VisibleVerticalAddOnTools => Owner.IseInvoke<object>(() => throw WpfNotSupported());
    }

    private sealed class IseSessionTemplate(IDataTemplate template, IseObjectModel root) : IDataTemplate
    {
        public bool Match(object? data) => template.Match(data);
        public Control? Build(object? data)
        {
            var control = template.Build(data);
            if (!root.IsDetached && data is SessionModel model &&
                control is StackPanel panel && panel.Children.FirstOrDefault() is TextBlock label)
                label.Bind(TextBlock.TextProperty, new Binding(nameof(IsePowerShellTab.DisplayName)) { Source = root.Tab(model) });
            return control;
        }
    }

    public abstract class IseCollection<T> : ObservableModel, IReadOnlyList<T>, INotifyCollectionChanged
    {
        protected abstract T[] Snapshot();
        public int Count => Snapshot().Length;
        public T this[int index] => Snapshot()[index];
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Snapshot()).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public event NotifyCollectionChangedEventHandler? CollectionChanged;
        internal void Notify(NotifyCollectionChangedEventArgs args, Func<object, T> wrap)
        {
            IList NewItems() => args.NewItems!.Cast<object>().Select(wrap).ToArray();
            IList OldItems() => args.OldItems!.Cast<object>().Select(wrap).ToArray();
            var translated = args.Action switch
            {
                NotifyCollectionChangedAction.Add => new(args.Action, NewItems(), args.NewStartingIndex),
                NotifyCollectionChangedAction.Remove => new(args.Action, OldItems(), args.OldStartingIndex),
                NotifyCollectionChangedAction.Replace => new(args.Action, NewItems(), OldItems(), args.NewStartingIndex),
                NotifyCollectionChangedAction.Move => new(args.Action, NewItems(), args.NewStartingIndex, args.OldStartingIndex),
                _ => new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset)
            };
            Notify(translated);
        }
        internal void Notify(NotifyCollectionChangedEventArgs args)
        {
            if (args.Action != NotifyCollectionChangedAction.Move) Changed(nameof(Count));
            Changed("Item[]");
            CollectionChanged?.Invoke(this, args);
        }
        internal void NotifySelectedFileChanged() => Changed(nameof(IseFileCollection.SelectedFile));
    }

    public sealed class IsePowerShellTabCollection : IseCollection<IsePowerShellTab>
    {
        private readonly IseObjectModel root;
        internal IsePowerShellTabCollection(IseObjectModel root) => this.root = root;
        protected override IsePowerShellTab[] Snapshot() => root.Owner.IseInvoke(() =>
            root.Owner.Workbench.Sessions.Select(root.Tab).ToArray());
        public void SetSelectedPowerShellTab(IsePowerShellTab tab) => root.Owner.IseInvoke(() =>
        {
            ArgumentNullException.ThrowIfNull(tab);
            if (tab.Owner != root.Owner) throw new ArgumentException("The PowerShell tab belongs to another window.", nameof(tab));
            tab.Activate();
            return true;
        });
        public IsePowerShellTab Add() => throw new PSNotSupportedException("Creating PowerShell tabs from scripts is not supported. Use File > New PowerShell Tab.");
        public void Remove(IsePowerShellTab tab) => throw new PSNotSupportedException("Closing PowerShell tabs from scripts is not supported. Use File > Close PowerShell Tab.");
    }

    public sealed class IsePowerShellTab : ObservableModel
    {
        internal WorkbenchControl Owner { get; }
        internal SessionModel Model { get; }
        private readonly Dictionary<ScriptTab, IseFile> files = [];
        private string? displayName;
        private bool detached;
        internal IsePowerShellTab(WorkbenchControl owner, SessionModel model)
        {
            Owner = owner;
            Model = model;
            Files = new(this);
            AddOnsMenu = new(this, "Add-ons", null, null);
            Snippets = new(this);
            model.PropertyChanged += OnModelChanged;
            model.Files.CollectionChanged += OnFilesChanged;
            model.Engine.StateChanged += OnStateChanged;
        }
        internal void Check()
        {
            if (detached || !Owner.Workbench.Sessions.Contains(Model) || Model.Engine.State == SessionState.Disposed)
                throw new ObjectDisposedException("PowerShell tab");
        }
        internal T Read<T>(Func<T> read) => Owner.IseInvoke(() => { Check(); return read(); });
        internal IseFile File(ScriptTab model)
        {
            ObjectDisposedException.ThrowIf(detached || Owner.windowClosed, this);
            if (!files.TryGetValue(model, out var file)) files.Add(model, file = new(this, model));
            return file;
        }
        internal void Forget(ScriptTab model)
        {
            if (files.Remove(model, out var file)) file.Detach();
        }
        private void OnFilesChanged(object? sender, NotifyCollectionChangedEventArgs args)
        {
            if (detached || Owner.windowClosed) return;
            Files.Notify(args, item => File((ScriptTab)item));
            foreach (var file in files.Keys.Where(file => !Model.Files.Contains(file)).ToArray()) Forget(file);
        }
        private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (detached || Owner.windowClosed) return;
            if (args.PropertyName == nameof(SessionModel.SelectedFile))
            {
                Changed(nameof(SelectedFile));
                Files.NotifySelectedFileChanged();
                if (Owner.Workbench.SelectedSession == Model) Owner.Scripting.NotifySelectionChanged();
            }
            if (args.PropertyName == nameof(SessionModel.DisplayName) && displayName is null) Changed(nameof(DisplayName));
        }
        private void OnStateChanged(SessionState state)
        {
            // Engine events may hold runspace locks: never synchronously dispatch back to the UI.
            Dispatcher.UIThread.Post(() =>
            {
                if (detached || Owner.windowClosed) return;
                Changed(nameof(CanInvoke));
                Changed(nameof(Prompt));
                NotifyStatusChanged();
                if (state == SessionState.Disposed) Detach();
            });
        }
        internal void NotifyStatusChanged() => Changed(nameof(StatusText));
        internal void NotifySettingsChanged()
        {
            if (detached || Owner.windowClosed) return;
            Changed(nameof(ExpandedScript));
            Changed(nameof(ShowCommands));
            Changed(nameof(Zoom));
            Snippets.NotifyDefaultsChanged();
        }
        internal void Detach()
        {
            if (detached) return;
            detached = true;
            Model.PropertyChanged -= OnModelChanged;
            Model.Files.CollectionChanged -= OnFilesChanged;
            Model.Engine.StateChanged -= OnStateChanged;
            foreach (var file in files.Values) file.Detach();
            files.Clear();
        }
        internal void Activate()
        {
            Check();
            Owner.Workbench.SelectedSession = Model;
            Owner.DisplaySession();
        }
        public string DisplayName
        {
            get => Read(() => displayName ?? Model.DisplayName);
            set => Read(() =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(value);
                if (displayName != value) { displayName = value; Changed(); }
                return true;
            });
        }
        public string Prompt => Read(() => Model.Engine.Prompt);
        public bool CanInvoke => Read(() => Model.Engine.State == SessionState.Ready && !Model.Engine.IsRemote);
        public string StatusText => Read(() => Owner.displayedSession == Model ? Owner.StatusText.Text ?? "" : Model.Engine.State switch
        {
            SessionState.Ready => UiText.Get("Ready"),
            SessionState.Running => UiText.Get("Running"),
            SessionState.Debugging => string.Format(UiText.Get("DebugStatus"), Model.DebugLocation?.Line),
            SessionState.NestedPrompt => UiText.Get("NestedPromptStatus"),
            SessionState.Disposed => UiText.Get("SessionClosed"),
            _ => UiText.Get("Starting")
        });
        public bool ExpandedScript
        {
            get => Read(() => Owner.hostingOptions.ShowScriptPane && Owner.ScriptPane.IsVisible);
            set => Read(() =>
            {
                if (value != (Owner.hostingOptions.ShowScriptPane && Owner.ScriptPane.IsVisible))
                    throw new PSNotSupportedException("Script pane visibility is owned by the host. Use Options.SelectedScriptPaneState to change its layout.");
                return true;
            });
        }
        public bool ShowCommands
        {
            get => Read(() => Owner.hostingOptions.EnableCommandsPane && Owner.settings.ShowCommands);
            set => Owner.ChangeIseSettings(s =>
            {
                if (value && !Owner.hostingOptions.EnableCommandsPane)
                    throw new PSNotSupportedException("The Commands pane is disabled by the workbench host.");
                s.ShowCommands = value;
            }, Check);
        }
        public double Zoom
        {
            get => Read(() => Owner.settings.Zoom);
            set => Owner.ChangeIseSettings(s =>
            {
                if (!double.IsFinite(value) || value < 20 || value > 400) throw new ArgumentOutOfRangeException(nameof(Zoom));
                s.Zoom = value;
            }, Check);
        }
        public bool HorizontalAddOnToolsPaneOpened => Read(() => false);
        public bool VerticalAddOnToolsPaneOpened => Read(() => false);
        public object ConsolePane => Read<object>(() => throw new PSNotSupportedException("The host owns console input and output; a mutable ISE console editor is not exposed."));
        public IseFile? SelectedFile => Read(() => Model.SelectedFile is { } file ? File(file) : null);
        public IseFileCollection Files { get; }
        public IseMenuItem AddOnsMenu { get; }
        public IseSnippetCollection Snippets { get; }
        public IseUnsupportedTools VerticalAddOnTools { get; } = new();
        public IseUnsupportedTools HorizontalAddOnTools { get; } = new();
        public void Invoke(string script) => throw new PSNotSupportedException("Cross-tab script invocation is not supported. Run the command in its owning PowerShell tab.");
        public object InvokeSynchronous(string script) => throw new PSNotSupportedException("Cross-tab synchronous invocation is not supported.");
        public object InvokeSynchronous(string script, bool useNewScope) => InvokeSynchronous(script);
        public object InvokeSynchronous(string script, bool useNewScope, int millisecondsTimeout) => InvokeSynchronous(script);
    }

    public sealed class IseSnippet
    {
        private readonly PowerShellSnippet snippet;
        internal IseSnippet(PowerShellSnippet snippet, string? fullPath = null, string? schemaVersion = null, bool? isTabSpecific = null)
        {
            this.snippet = snippet;
            FullPath = fullPath;
            SchemaVersion = schemaVersion;
            IsTabSpecific = snippet.IsBuiltIn ? false : isTabSpecific;
        }
        public string Title => snippet.Title;
        public string DisplayTitle => snippet.DisplayTitle;
        public string Description => snippet.Description;
        public string Author => snippet.Author;
        public string CodeFragment => snippet.Code;
        public string Code => snippet.Code;
        public string Text => snippet.Text;
        public int CaretOffset => snippet.CaretOffset;
        public bool Indent => snippet.Indent;
        public bool IsBuiltIn => snippet.IsBuiltIn;
        public bool IsDefault => snippet.IsBuiltIn;
        public string Compatibility => snippet.Compatibility;
        // Only explicit Load retains provenance; built-ins and catalog-only entries have no known path/schema.
        public string? FullPath { get; }
        public string? SchemaVersion { get; }
        public bool? IsTabSpecific { get; }
        public (string Text, int Caret) Expand(string indentation, string newLine) => snippet.Expand(indentation, newLine);
        public override string ToString() => snippet.Title;
    }

    public sealed class IseSnippetCollection : IseCollection<IseSnippet>
    {
        private readonly IsePowerShellTab tab;
        private readonly Dictionary<PowerShellSnippet, (string FullPath, string SchemaVersion)> loadedMetadata = [];
        private bool includeDefaults;
        internal IseSnippetCollection(IsePowerShellTab tab)
        {
            this.tab = tab;
            includeDefaults = tab.Owner.settings.UseDefaultSnippets;
        }
        internal void NotifyDefaultsChanged()
        {
            if (includeDefaults == tab.Owner.settings.UseDefaultSnippets) return;
            includeDefaults = tab.Owner.settings.UseDefaultSnippets;
            Notify(new(NotifyCollectionChangedAction.Reset));
        }
        protected override IseSnippet[] Snapshot()
        {
            var (service, includeDefaults) = tab.Read(() => (tab.Model.Engine.Snippets, tab.Owner.settings.UseDefaultSnippets));
            var result = Task.Run(service.LoadAsync).GetAwaiter().GetResult();
            if (result.Errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, result.Errors));
            return tab.Read(() => (includeDefaults ? SnippetCatalog.BuiltIns : []).Concat(result.Snippets).Distinct()
                .Select(snippet => loadedMetadata.TryGetValue(snippet, out var metadata)
                    ? new IseSnippet(snippet, metadata.FullPath, metadata.SchemaVersion, true) : new IseSnippet(snippet)).ToArray());
        }
        public void Load(string fullPath)
        {
            if (!Path.IsPathFullyQualified(fullPath)) throw new ArgumentException("Snippets.Load requires a fully qualified local path.", nameof(fullPath));
            var service = tab.Read(() => tab.Model.Engine.Snippets);
            var paths = Directory.Exists(fullPath)
                ? Directory.EnumerateFiles(fullPath, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => path.EndsWith(".snippets.ps1xml", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToArray()
                : [fullPath];
            var metadata = new Dictionary<PowerShellSnippet, (string FullPath, string SchemaVersion)>();
            foreach (var path in paths)
            {
                var xml = System.IO.File.ReadAllText(path);
                var snippets = SnippetCatalog.Parse(xml);
                using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000
                });
                var versions = XDocument.Load(reader).Root!.Elements(XName.Get("Snippet", "http://schemas.microsoft.com/PowerShell/Snippets"))
                    .Select(element => element.Attribute("Version")!.Value).ToArray();
                for (var index = 0; index < snippets.Count; index++)
                    metadata[snippets[index]] = (Path.GetFullPath(path), versions[index]);
            }
            service.Import(fullPath, false);
            tab.Read(() =>
            {
                foreach (var entry in metadata) loadedMetadata[entry.Key] = entry.Value;
                Notify(new(NotifyCollectionChangedAction.Reset));
                return true;
            });
        }
    }

    public sealed class IseFileCollection : IseCollection<IseFile>
    {
        private readonly IsePowerShellTab tab;
        internal IseFileCollection(IsePowerShellTab tab) => this.tab = tab;
        protected override IseFile[] Snapshot() => tab.Read(() => tab.Model.Files.Select(tab.File).ToArray());
        public IseFile? SelectedFile => tab.SelectedFile;
        public IseFile Add() => tab.Read(() =>
        {
            tab.Activate();
            tab.Owner.NewFile();
            return tab.File(tab.Model.SelectedFile!);
        });
        public IseFile Add(string fullPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
            if (!Path.IsPathFullyQualified(fullPath)) throw new ArgumentException("Files.Add requires a fully qualified local path.", nameof(fullPath));
            var file = ScriptFile.FromBytes(fullPath, System.IO.File.ReadAllBytes(fullPath));
            return tab.Read(() =>
            {
                if (tab.Model.Engine.IsRemote) throw new PSNotSupportedException("Files.Add does not open files in a remote runspace.");
                var existing = tab.Model.Files.FirstOrDefault(candidate => !candidate.File.IsRemote && SameScript(candidate.File.Path, file.Path));
                var model = existing ?? new ScriptTab(file);
                if (existing is null) tab.Model.Files.Add(model);
                SetSelectedFile(tab.File(model));
                return tab.File(model);
            });
        }
        public void SetSelectedFile(IseFile file) => tab.Read(() =>
        {
            ArgumentNullException.ThrowIfNull(file);
            if (file.Tab != tab) throw new ArgumentException("The file belongs to another PowerShell tab.", nameof(file));
            file.Check();
            tab.Activate();
            tab.Model.SelectedFile = file.Model;
            tab.Owner.DisplayFile();
            return true;
        });
        public void Remove(IseFile file) => Remove(file, false);
        public void Remove(IseFile file, bool force) => tab.Read(() =>
        {
            ArgumentNullException.ThrowIfNull(file);
            if (file.Tab != tab) throw new ArgumentException("The file belongs to another PowerShell tab.", nameof(file));
            file.Check();
            if (file.Model.File.IsDirty && !force) throw new InvalidOperationException("Save the file or use Remove(file, true) to discard edits.");
            if (tab.Model.Engine.State is SessionState.Debugging or SessionState.NestedPrompt ||
                file.Model.LineBreakpoints.Count > 0 || tab.Owner.autoSaving ||
                file.Model.File.Path is not null && tab.Model.Engine.State == SessionState.Running)
                throw new PSNotSupportedException("Close this file through the UI after stopping execution; breakpoint/recovery work is pending.");
            // Never acquire the executing runspace's gate to close a file from inside its script.
            tab.Owner.RemoveRecovery(file.Model.RecoveryId);
            tab.Model.Files.Remove(file.Model);
            tab.Forget(file.Model);
            if (tab.Model.SelectedFile == file.Model) tab.Model.SelectedFile = tab.Model.Files.LastOrDefault();
            if (tab.Owner.Workbench.SelectedSession == tab.Model) tab.Owner.DisplayFile();
            return true;
        });
    }

    public sealed class IseFile : ObservableModel
    {
        internal IsePowerShellTab Tab { get; }
        internal ScriptTab Model { get; }
        private ScriptFile observedFile;
        private bool detached;
        internal IseFile(IsePowerShellTab tab, ScriptTab model)
        {
            Tab = tab; Model = model; Editor = new(this);
            observedFile = model.File;
            observedFile.PropertyChanged += OnFileChanged;
            model.PropertyChanged += OnModelChanged;
            model.Document.TextChanged += OnTextChanged;
        }
        private void OnTextChanged(object? sender, EventArgs args)
        {
            if (!detached && !Tab.Owner.windowClosed) Editor.NotifyTextChanged();
        }
        private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (detached || Tab.Owner.windowClosed || args.PropertyName != nameof(ScriptTab.File)) return;
            observedFile.PropertyChanged -= OnFileChanged;
            observedFile = Model.File;
            observedFile.PropertyChanged += OnFileChanged;
            Changed("");
            Editor.NotifyTextChanged();
        }
        private void OnFileChanged(object? sender, PropertyChangedEventArgs args)
        {
            void Notify()
            {
                if (detached || Tab.Owner.windowClosed) return;
                switch (args.PropertyName)
                {
                    case nameof(ScriptFile.Path): Changed(nameof(FullPath)); Changed(nameof(IsUntitled)); break;
                    case nameof(ScriptFile.Title): Changed(nameof(DisplayName)); break;
                    case nameof(ScriptFile.IsDirty): Changed(nameof(IsSaved)); break;
                    case nameof(ScriptFile.EncodingChoice): Changed(nameof(Encoding)); break;
                }
            }
            if (Dispatcher.UIThread.CheckAccess()) Notify();
            else Dispatcher.UIThread.Post(Notify);
        }
        internal void Detach()
        {
            if (detached) return;
            detached = true;
            observedFile.PropertyChanged -= OnFileChanged;
            Model.PropertyChanged -= OnModelChanged;
            Model.Document.TextChanged -= OnTextChanged;
        }
        internal void Check()
        {
            Tab.Check();
            if (detached || !Tab.Model.Files.Contains(Model)) throw new ObjectDisposedException("ISE file");
        }
        internal T Read<T>(Func<T> operation) => Tab.Read(() => { Check(); return operation(); });
        internal void CanEdit()
        {
            Check();
            if (Tab.Owner.closingInProgress || Tab.Model.Engine.State == SessionState.Debugging)
                throw new InvalidOperationException("The script editor is read-only while debugging or closing.");
            if (Model.File.IsRemote) throw new PSNotSupportedException("ISE file scripting supports local documents only.");
        }
        public string DisplayName => Read(() => Model.File.Title);
        public string? FullPath => Read(() => Model.File.Path);
        public bool IsUntitled => Read(() => Model.File.Path is null);
        public bool IsSaved => Read(() => !Model.File.IsDirty);
        public Encoding Encoding => Read(() => Model.File.EncodingChoice.CreateEncoding());
        public IseEditor Editor { get; }
        public void Save() => SaveAs(FullPath ?? throw new InvalidOperationException("Use SaveAs with a fully qualified path for an untitled file."));
        public void Save(Encoding encoding) => SaveAs(FullPath ?? throw new InvalidOperationException("Use SaveAs for an untitled file."), encoding);
        public void SaveAs(string fullPath) => SaveAsCore(fullPath, null);
        public void SaveAs(string fullPath, Encoding encoding) => SaveAsCore(fullPath, encoding);
        private void SaveAsCore(string fullPath, Encoding? encoding)
        {
            if (!Path.IsPathFullyQualified(fullPath)) throw new ArgumentException("SaveAs requires a fully qualified local path.", nameof(fullPath));
            if (Dispatcher.UIThread.CheckAccess()) throw new InvalidOperationException("Call synchronous ISE Save methods from a PowerShell script, not the UI thread.");
            Read(() =>
            {
                CanEdit();
                if (Tab.Model.Files.Any(other => other != Model && !other.File.IsRemote && SameScript(other.File.Path, fullPath)))
                    throw new InvalidOperationException("The destination is already open in this PowerShell tab.");
                if (encoding is not null) Model.File.SetEncoding(new(encoding.CodePage, encoding.GetPreamble().Length > 0));
                return Model.File.SaveAsync(fullPath);
            }).GetAwaiter().GetResult();
        }
    }

    public sealed class IseEditor : ObservableModel
    {
        private readonly IseFile file;
        internal IseEditor(IseFile file) => this.file = file;
        private TextDocument Document => file.Model.Document;
        private TextEditor Activate()
        {
            file.Tab.Files.SetSelectedFile(file);
            return file.Tab.Owner.ScriptEditor;
        }
        private int Caret => file.Tab.Owner.displayedFile == file.Model
            ? file.Tab.Owner.ScriptEditor.CaretOffset : Math.Min(file.Model.File.CaretOffset, Document.TextLength);
        public string Text
        {
            get => file.Read(() => Document.Text);
            set => file.Read(() => { file.CanEdit(); ArgumentNullException.ThrowIfNull(value); Document.Text = value; return true; });
        }
        public int LineCount => file.Read(() => Document.LineCount);
        public bool CanGoToMatch => file.Read(() => Match() is not null);
        public int CaretLine => file.Read(() => Document.GetLocation(Caret).Line);
        public int CaretColumn => file.Read(() => Document.GetLocation(Caret).Column);
        public string CaretLineText => file.Read(() => Document.GetText(Document.GetLineByOffset(Caret)));
        public string SelectedText => file.Read(() => file.Tab.Owner.displayedFile == file.Model ? file.Tab.Owner.ScriptEditor.SelectedText : "");
        public int GetLineLength(int lineNumber) => file.Read(() => Document.GetLineByNumber(lineNumber).Length);
        internal void NotifyCaretChanged()
        {
            Changed(nameof(CaretLine));
            Changed(nameof(CaretColumn));
            Changed(nameof(CaretLineText));
            Changed(nameof(SelectedText));
            Changed(nameof(CanGoToMatch));
        }
        internal void NotifyTextChanged()
        {
            Changed(nameof(Text));
            Changed(nameof(LineCount));
            NotifyCaretChanged();
        }
        private (int Open, int Close)? Match()
        {
            var caret = Caret;
            var text = Document.Text;
            if (caret < text.Length && text[caret] is '(' or '[' or '{')
            {
                var match = EditorAnalysis.MatchingBrace(text, caret);
                if (match?.Open == caret) return match;
            }
            if (caret > 0 && text[caret - 1] is ')' or ']' or '}')
            {
                var match = EditorAnalysis.MatchingBrace(text, caret - 1);
                if (match?.Close == caret - 1) return match;
            }
            return null;
        }
        public void GoToMatch() => file.Read(() =>
        {
            if (Match() is not { } match) return false;
            var destination = Caret == match.Open ? match.Close + 1 : match.Open;
            var editor = Activate();
            foreach (var fold in file.Tab.Owner.folding?.AllFoldings ?? [])
                if (fold.StartOffset <= destination && fold.EndOffset >= destination) fold.IsFolded = false;
            editor.Select(destination, 0);
            editor.CaretOffset = destination;
            editor.ScrollTo(editor.TextArea.Caret.Line, editor.TextArea.Caret.Column);
            return true;
        });
        public void ToggleOutliningExpansion() => file.Read(() =>
        {
            if (!file.Tab.Owner.settings.ShowOutlining) return false;
            _ = Activate();
            var folds = file.Tab.Owner.folding?.AllFoldings.ToArray() ??
                throw new InvalidOperationException("Outlining is not available for this editor.");
            var collapse = folds.Any(fold => !fold.IsFolded);
            foreach (var fold in folds) fold.IsFolded = collapse;
            return true;
        });
        private int Offset(int line, int column)
        {
            var documentLine = Document.GetLineByNumber(line);
            if (column < 1 || column > documentLine.Length + 1) throw new ArgumentOutOfRangeException(nameof(column));
            return documentLine.Offset + column - 1;
        }
        public void SetCaretPosition(int lineNumber, int columnNumber) => file.Read(() =>
        {
            var offset = Offset(lineNumber, columnNumber);
            var editor = Activate();
            editor.Select(offset, 0);
            editor.CaretOffset = offset;
            return true;
        });
        public void Select(int startLine, int startColumn, int endLine, int endColumn) => file.Read(() =>
        {
            var start = Offset(startLine, startColumn);
            var end = Offset(endLine, endColumn);
            if (end < start) throw new ArgumentException("The selection end must not precede its start.");
            var editor = Activate();
            editor.Select(start, end - start);
            editor.CaretOffset = end;
            return true;
        });
        public void SelectCaretLine() => file.Read(() =>
        {
            var line = Document.GetLineByOffset(Caret);
            Activate().Select(line.Offset, line.Length);
            return true;
        });
        public void InsertText(string text) => file.Read(() =>
        {
            ArgumentNullException.ThrowIfNull(text);
            file.CanEdit();
            var editor = Activate();
            var start = editor.SelectionLength > 0 ? editor.SelectionStart : editor.CaretOffset;
            Document.Replace(start, editor.SelectionLength, text);
            editor.Select(start + text.Length, 0);
            editor.CaretOffset = start + text.Length;
            return true;
        });
        public void Clear() => Text = "";
        public void Focus() => file.Read(() => Activate().TextArea.Focus());
        public void EnsureVisible(int lineNumber) => file.Read(() =>
        {
            _ = Document.GetLineByNumber(lineNumber);
            Activate().ScrollToLine(lineNumber);
            return true;
        });
    }

    public sealed class IseMenuItem
    {
        internal IsePowerShellTab Tab { get; }
        internal string Name { get; }
        internal ScriptBlock? Action { get; }
        internal KeyGesture? Gesture { get; }
        internal IseMenuItem(IsePowerShellTab tab, string name, ScriptBlock? action, KeyGesture? gesture)
        {
            Tab = tab; Name = name; Action = action; Gesture = gesture; Submenus = new(this);
        }
        public string DisplayName => Tab.Read(() => Name);
        public IseMenuItemCollection Submenus { get; }
        internal IEnumerable<IseMenuItem> Descendants() => Submenus.Items.SelectMany(child => new[] { child }.Concat(child.Descendants()));
    }

    public sealed class IseMenuItemCollection : IseCollection<IseMenuItem>
    {
        private readonly IseMenuItem parent;
        internal List<IseMenuItem> Items { get; } = [];
        internal IseMenuItemCollection(IseMenuItem parent) => this.parent = parent;
        private void Check()
        {
            if (parent != parent.Tab.AddOnsMenu && !parent.Tab.AddOnsMenu.Descendants().Contains(parent))
                throw new ObjectDisposedException("ISE menu");
        }
        protected override IseMenuItem[] Snapshot() => parent.Tab.Read(() => { Check(); return Items.ToArray(); });
        public IseMenuItem Add(string displayName, ScriptBlock? action, string? shortcut)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
            var gesture = string.IsNullOrWhiteSpace(shortcut) ? null : KeyGesture.Parse(shortcut);
            if (action is null && gesture is not null) throw new ArgumentException("A submenu container cannot have a shortcut.");
            var caller = Runspace.DefaultRunspace?.InstanceId;
            return parent.Tab.Read(() =>
            {
                Check();
                if (caller != parent.Tab.Model.Engine.LocalRunspaceId)
                    throw new PSNotSupportedException("Register menu actions from their owning local PowerShell tab.");
                if (parent.Action is not null) throw new InvalidOperationException("An action menu cannot contain submenus.");
                if (Items.Any(item => item.Name == displayName)) throw new ArgumentException("A sibling menu already has this display name.");
                if (gesture is not null && (parent.Tab.Owner.IsIseShortcutReserved(gesture) ||
                    parent.Tab.AddOnsMenu.Descendants().Any(item => item.Gesture?.Equals(gesture) == true)))
                    throw new ArgumentException("This shortcut is already reserved by the workbench or this tab.");
                var item = new IseMenuItem(parent.Tab, displayName, action, gesture);
                Items.Add(item);
                parent.Tab.Owner.RefreshIseMenus();
                Notify(new(NotifyCollectionChangedAction.Add, item, Items.Count - 1));
                return item;
            });
        }
        public void Clear() => parent.Tab.Read(() =>
        {
            Check();
            Items.Clear();
            parent.Tab.Owner.RefreshIseMenus();
            Notify(new(NotifyCollectionChangedAction.Reset));
            return true;
        });
        public bool Remove(IseMenuItem item) => parent.Tab.Read(() =>
        {
            Check();
            ArgumentNullException.ThrowIfNull(item);
            if (item.Tab != parent.Tab) throw new ArgumentException("The menu item belongs to another PowerShell tab.", nameof(item));
            var index = Items.IndexOf(item);
            var removed = Items.Remove(item);
            parent.Tab.Owner.RefreshIseMenus();
            if (removed) Notify(new(NotifyCollectionChangedAction.Remove, item, index));
            return removed;
        });
    }

    public sealed class IseUnsupportedTools
    {
        public object Add(string name, Type controlType) => throw WpfNotSupported();
        public object Add(string name, Type controlType, bool isVisible) => throw WpfNotSupported();
        public void Clear() => throw WpfNotSupported();
    }

    private static PSNotSupportedException WpfNotSupported() => new(
        "WPF ISE add-on tools are not supported by Iseberg's Avalonia host, including on Windows. " +
        "Use script-based AddOnsMenu actions; WPF controls cannot run natively on Linux or macOS.");
}
