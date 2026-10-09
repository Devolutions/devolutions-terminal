using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Iseberg.Core;

namespace Iseberg;

/// <summary>
/// An embeddable PowerShell workbench. Attach to a shown Avalonia Window, initialize on the UI
/// thread, and await disposal before removing it permanently. The host owns its window's lifetime.
/// </summary>
public sealed partial class WorkbenchControl
{
    private readonly WorkbenchOptions hostingOptions;
    private readonly bool managesWindow;
    private readonly bool persistBeforeInitialization;
    private Window? hostWindow;
    private Window? dialogOwner;
    private Task? initializationTask;
    private Task? disposalTask;
    private bool closePrepared;
    private WorkbenchPersistenceLease? persistenceLease;
    private bool discardRecoveryOnDispose;
    private readonly Dictionary<SessionModel, (Action Clear, Action<ProgressUpdate> Progress)> sessionUiHandlers = [];

    private Window HostWindow => hostWindow ?? dialogOwner ?? TopLevel.GetTopLevel(this) as Window
        ?? throw new InvalidOperationException("Attach the workbench to an Avalonia Window before using dialog or platform operations.");
    private IStorageProvider StorageProvider => HostWindow.StorageProvider;
    private ILauncher Launcher => HostWindow.Launcher;

    /// <summary>True after initialization completes and until disposal starts.</summary>
    public bool IsStarted => startupComplete && !windowClosed;
    /// <summary>True once irreversible disposal has begun, including a failed disposal.</summary>
    public bool IsDisposed => windowClosed;
    /// <summary>Host theme selection, also readable during cancelable close preparation.</summary>
    public string? SelectedThemePreset => settings.ThemePreset;
    public bool IsNativeConsoleActive => hostingOptions.Console is { } console &&
        (console.View.IsKeyboardFocusWithin || ReferenceEquals(editTarget, console.View));
    /// <summary>The same packaged, engine-independent script editor used by external hosts.</summary>
    public PowerShellEditorControl ScriptEditorView => ScriptEditorControl;
    /// <summary>Raised by Exit; the host decides whether and what to close.</summary>
    public event EventHandler? CloseRequested;
    public event EventHandler? OptionsRequested;
    /// <summary>Raised for failures in UI or automatic operations. Public awaited operations throw instead.</summary>
    public event EventHandler<WorkbenchErrorEventArgs>? ErrorOccurred;
    public event EventHandler? ThemeSelectionChanged;

    /// <summary>
    /// Loads configured state and creates the initial session. Must run on the UI thread after
    /// attachment to a shown Window. Repeated calls await the same result, including any failure.
    /// </summary>
    public Task InitializeAsync()
    {
        if (initializationTask is { IsFaulted: true }) return initializationTask;
        VerifyAvailable();
        if (!HostWindow.IsVisible) throw new InvalidOperationException("Show the host Window before initializing the workbench.");
        return initializationTask ??= StartAsync();
    }

    /// <summary>Creates and selects a new independent PowerShell session with an untitled document.</summary>
    public async Task<SessionModel> CreateSessionAsync()
    {
        VerifyInitialized();
        return await NewSessionAsync();
    }

    /// <summary>Creates and selects an untitled document. Nonempty text is unsaved.</summary>
    public ScriptTab CreateDocument(string text = "")
    {
        VerifyInitialized();
        ArgumentNullException.ThrowIfNull(text);
        if (Workbench.SelectedSession is null) throw new InvalidOperationException("Create a PowerShell session first.");
        NewFile();
        var document = Workbench.SelectedSession.SelectedFile!;
        document.Document.Text = text;
        return document;
    }

    /// <summary>Selects a session belonging to this workbench and displays its selected document.</summary>
    public void SelectSession(SessionModel session)
    {
        VerifyInitialized();
        ArgumentNullException.ThrowIfNull(session);
        if (!Workbench.Sessions.Contains(session)) throw new ArgumentException("The session belongs to another workbench.", nameof(session));
        Workbench.SelectedSession = session;
        DisplaySession();
    }

    /// <summary>Selects a document and its owning session. The document must belong to this workbench.</summary>
    public void SelectDocument(ScriptTab document)
    {
        VerifyInitialized();
        var session = DocumentOwner(document);
        Workbench.SelectedSession = session;
        session.SelectedFile = document;
        DisplaySession();
        DisplayFile();
    }

    /// <summary>Executes text in the selected session and flushes output to the console. This is not a sandbox.</summary>
    public async Task ExecuteAsync(string script)
    {
        VerifyInitialized();
        ArgumentNullException.ThrowIfNull(script);
        var session = Workbench.SelectedSession ?? throw new InvalidOperationException("Create a PowerShell session first.");
        try { await session.Engine.ExecuteAsync(script); }
        finally
        {
            if (!windowClosed) { FlushOutput(); RefreshState(); }
        }
    }

    /// <summary>Submits terminal input to the selected persistent runspace, including debugger/nested prompts.</summary>
    public Task SubmitConsoleInputAsync(string text)
    {
        VerifyInitialized();
        ArgumentNullException.ThrowIfNull(text);
        return ExecuteConsoleTextAsync(
            Workbench.SelectedSession ?? throw new InvalidOperationException("No PowerShell session is selected."), text);
    }

    private async Task ExecuteConsoleTextAsync(SessionModel session, string text)
    {
        if (session.IsConsoleSubmissionPending && !session.Engine.IsDebuggerPaused && !session.Engine.IsNestedPromptActive)
            throw new InvalidOperationException("This PowerShell tab is busy.");
        session.Input = "";
        session.Console.HidePrompt();
        session.Completion = null;
        if (session.History.Count == 0 || session.History[^1] != text) session.History.Add(text);
        if (session.History.Count > 1000) session.History.RemoveAt(0);
        session.HistoryIndex = session.History.Count;
        session.DraftInput = "";
        if (session.Engine.IsDebuggerPaused) await EvaluateConsoleAsync(session, text);
        else if (session.Engine.IsNestedPromptActive) await EvaluateNestedConsoleAsync(session, text);
        else
        {
            session.IsConsoleSubmissionPending = true;
            RefreshState();
            try
            {
                await session.Engine.ExecuteAsync(text);
                if (!windowClosed) await RefreshDebuggerAsync(session, reconcile: true);
            }
            finally
            {
                session.IsConsoleSubmissionPending = false;
                if (!windowClosed) RefreshState();
            }
        }
        if (!windowClosed) FlushOutput();
    }

    public Task ReportConsoleErrorAsync(Exception error)
    {
        Dispatcher.UIThread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(error);
        return ReportErrorAsync("Iseberg PowerShell terminal", error);
    }

    /// <summary>Persists snippets in the selected session's host-owned catalog.</summary>
    public async Task SaveSnippetsAsync(IEnumerable<PowerShellSnippet> snippets)
    {
        VerifyInitialized();
        ArgumentNullException.ThrowIfNull(snippets);
        var values = snippets.ToArray();
        if (values.Length == 0) throw new ArgumentException("At least one snippet is required.", nameof(snippets));
        var session = Workbench.SelectedSession ?? throw new InvalidOperationException("Select a PowerShell session first.");
        await SnippetCatalog.SaveAsync(Path.Combine(session.Engine.Snippets.UserDirectory,
            Guid.NewGuid().ToString("N") + ".snippets.ps1xml"), values);
    }

    /// <summary>Writes current dirty documents and metadata without waiting for the autosave timer.</summary>
    public async Task SaveRecoveryAsync()
    {
        VerifyInitialized();
        if (!hostingOptions.EnablePersistence) throw new NotSupportedException("Recovery is disabled by the host.");
        foreach (var session in Workbench.Sessions.ToArray())
            foreach (var document in session.Files.ToArray())
                await recovery.SaveAsync(document.RecoveryId, document.File, session.Name);
        await SaveWorkbenchAsync();
    }

    /// <summary>Stops the selected session's active command, debugger, or nested prompt.</summary>
    public async Task StopAsync()
    {
        VerifyInitialized();
        var session = Workbench.SelectedSession ?? throw new InvalidOperationException("No PowerShell session is selected.");
        await session.Engine.StopAsync();
        FlushOutput();
        RefreshState();
    }

    /// <summary>Runs the selected script, or its selection/current line, using normal save and debugger behavior.</summary>
    public Task RunDocumentAsync(bool selection = false)
    {
        VerifyInitialized();
        if (displayedFile is null) throw new InvalidOperationException("Select a script document first.");
        return RunScriptAsync(selection);
    }

    /// <summary>Closes an owned document after its save prompt. Returns false on cancellation.</summary>
    public Task<bool> CloseDocumentAsync(ScriptTab document)
    {
        VerifyInitialized();
        return CloseFileAsync(DocumentOwner(document), document);
    }

    /// <summary>Stops and closes an owned session after confirmation. Returns false on cancellation.</summary>
    public Task<bool> CloseSessionAsync(SessionModel session)
    {
        VerifyInitialized();
        ArgumentNullException.ThrowIfNull(session);
        if (!Workbench.Sessions.Contains(session)) throw new ArgumentException("The session belongs to another workbench.", nameof(session));
        return CloseSessionCoreAsync(session);
    }

    /// <summary>
    /// Saves an owned document. An absolute local path can avoid the save picker; remote documents
    /// use remote paths. Returns false when the user cancels a picker or conflict prompt.
    /// </summary>
    public Task<bool> SaveDocumentAsync(ScriptTab document, string? path = null)
    {
        VerifyInitialized();
        var session = DocumentOwner(document);
        var remote = document.File.IsRemote || document.File.Path is null && session.Engine.IsRemote;
        if (path is not null && (string.IsNullOrWhiteSpace(path) || !remote && !Path.IsPathFullyQualified(path)))
            throw new ArgumentException("Specify an absolute local file path.", nameof(path));
        return SaveFileAsync(document, targetPath: path);
    }

    /// <summary>
    /// Confirms stopping and saving without disposing, so a host can prepare an entire group of tabs.
    /// On approval the control is disabled until RequestCloseAsync or CancelClosePreparation.
    /// </summary>
    public async Task<bool> PrepareCloseAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (closingApproved || closePrepared) return true;
        ObjectDisposedException.ThrowIf(windowClosed, this);
        if (closingInProgress) return false;
        closingInProgress = true;
        RefreshState();
        try
        {
            if (initializationTask is { IsCompleted: false })
            {
                var running = Workbench.Sessions.Where(session =>
                    session.Engine.State is SessionState.Running or SessionState.Debugging or SessionState.NestedPrompt).ToArray();
                if (running.Length > 0)
                {
                    if (await Dialogs.ChooseAsync(HostWindow, ActualThemeVariant, "Stop initialization",
                        "Stop the running initialization command and close this workbench?", "Stop", "Cancel") != "Stop")
                        return false;
                    foreach (var session in running) await session.Engine.StopAsync();
                }
                await initializationTask;
            }
            foreach (var session in Workbench.Sessions.ToArray())
            {
                if (session.Engine.State is SessionState.Running or SessionState.Debugging or SessionState.NestedPrompt)
                {
                    if (await Dialogs.ChooseAsync(HostWindow, ActualThemeVariant, "Stop execution", "Stop the running command and close this workbench?", "Stop", "Cancel") != "Stop")
                        return false;
                    await session.Engine.StopAsync();
                }
                foreach (var document in session.Files.ToArray())
                    if (!await ConfirmSaveAsync(document)) return false;
                if (session.Engine.State == SessionState.Ready) await RefreshDebuggerAsync(session, reconcile: true);
                await SaveDebuggerSettingsAsync(session);
            }
            await autoSaveTask;
            await DrainScriptingSettingsPersistenceAsync();
            CapturePaneGeometry();
            CaptureWindowGeometry();
            await SaveSettingsAsync();
            await SaveWorkbenchAsync();
            closePrepared = true;
            IsEnabled = false;
            return true;
        }
        finally
        {
            closingInProgress = false;
            if (!windowClosed) RefreshState();
        }
    }

    /// <summary>Restores an approved but not yet disposed workbench when a group close is canceled.</summary>
    public void CancelClosePreparation()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (windowClosed) return;
        closePrepared = false;
        RefreshState();
        IsEnabled = startupComplete || hostingOptions.Console?.HasPendingInput == true;
    }

    /// <summary>Confirms close if needed, persists enabled state, and disposes without closing the host.</summary>
    public async Task<bool> RequestCloseAsync()
    {
        if (!await PrepareCloseAsync()) return false;
        if (closingApproved) return true;
        try
        {
            foreach (var session in Workbench.Sessions)
                foreach (var document in session.Files) RemoveRecovery(document.RecoveryId);
            discardRecoveryOnDispose = true;
            await DisposeAsync();
            closingApproved = true;
            return true;
        }
        catch
        {
            CancelClosePreparation();
            throw;
        }
    }

    /// <summary>
    /// Stops and disposes all owned sessions without save prompts, releases timers and handlers,
    /// and retains recovery data. Use RequestCloseAsync for normal user-driven shutdown.
    /// Must be awaited on the UI thread. Repeated calls await the same result.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        return new ValueTask(disposalTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync(bool waitForInitialization = true)
    {
        windowClosed = true;
        ScriptEditorControl.AnalysisProvider = null;
        ScriptEditorControl.CompletionProvider = null;
        foreach (var session in Workbench.Sessions) session.Console.CancelAnalysis();
        hostingOptions.Console?.RefreshState(acceptsCommands: false, inputDisabled: true);
        FileTabs.SelectionChanged -= OnFileChanged;
        SessionTabs.SelectionChanged -= OnSessionChanged;
        Workbench.Sessions.CollectionChanged -= workbenchSessionsChanged;
        ZoomSlider.ValueChanged -= OnZoomChanged;
        IsEnabled = false;
        ++commandRequestVersion;
        windowCancellation.Cancel();
        outputTimer.Stop(); autoSaveTimer.Stop(); completionTimer.Stop(); persistenceTimer.Stop();
        progressTrackers.Clear();
        ProgressPanel.IsVisible = false;
        ProgressText.Text = "";
        Completion?.Close();
        commandFormCancellation?.Cancel();
        commandFormCancellation?.Dispose();
        commandFormCancellation = null;
        DesktopTheme.Changed -= ApplyAppearance;
        DetachHostWindow();
        dialogOwner = null;
        var failures = new List<Exception>();
        foreach (var session in Workbench.Sessions.ToArray())
        {
            try { await session.Engine.StopAsync(); }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError("Could not stop PowerShell during cleanup: {0}", error);
                failures.Add(error);
            }
        }
        if (waitForInitialization && initializationTask is { IsCompleted: false })
        {
            try { await initializationTask; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                ArgumentException or System.Text.Json.JsonException or OperationCanceledException or NotSupportedException)
            { System.Diagnostics.Trace.TraceError("Initialization failed during disposal: {0}", exception); }
        }
        try { await autoSaveTask; }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError("Could not finish autosave during cleanup: {0}", error);
            failures.Add(error);
        }
        try { await DrainScriptingSettingsPersistenceAsync(); }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError("Could not finish ISE option saves: {0}", error);
            failures.Add(error);
        }
        if (hostingOptions.EnablePersistence && persistenceLease is not null && startupComplete)
        {
            try
            {
                if (!discardRecoveryOnDispose)
                    foreach (var session in Workbench.Sessions)
                        foreach (var document in session.Files)
                            await recovery.SaveAsync(document.RecoveryId, document.File, session.Name, released: true);
                await SaveSettingsAsync();
                await SaveWorkbenchAsync(released: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {
                System.Diagnostics.Trace.TraceError("Could not persist workbench during cleanup: {0}", error);
                failures.Add(error);
            }
        }
        foreach (var session in Workbench.Sessions.ToArray())
        {
            if (showCommandHandlers.Remove(session, out var commandHandler)) session.Engine.ShowCommandRequested -= commandHandler;
            if (inputHandlers.Remove(session, out var inputHandler)) session.Engine.InputRequested -= inputHandler;
            if (commandErrorHandlers.Remove(session, out var errorHandler)) session.Engine.CommandErrorRequested -= errorHandler;
            DetachDebugger(session);
            DetachSessionUi(session);
            session.Console.CancelAnalysis();
            try { await session.Engine.DisposeAsync(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                OperationCanceledException)
            {
                System.Diagnostics.Trace.TraceError("Could not dispose PowerShell session {0}: {1}", session.Name, exception);
                failures.Add(exception);
            }
        }
        windowCancellation.Dispose();
        if (folding is not null) AvaloniaEdit.Folding.FoldingManager.Uninstall(folding);
        folding = null;
        DisposePortableEditing();
        ScriptEditorControl.Dispose();
        ConsoleEditor.Document = new();
        if (hostingOptions.Console is { } console)
        {
            try { await console.DisposeAsync(); }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError("Could not dispose host terminal: {0}", error);
                failures.Add(error);
            }
        }
        foreach (var path in printPreviews)
        {
            try { File.Delete(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.TraceError("Could not remove print preview: {0}", exception); }
        }
        persistenceLease?.Dispose();
        persistenceLease = null;
        if (failures.Count > 0) throw new AggregateException("Could not dispose all PowerShell sessions.", failures);
    }

    private void VerifyAvailable()
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(windowClosed, this);
        if (closingInProgress) throw new InvalidOperationException("The workbench is closing.");
        if (closePrepared) throw new InvalidOperationException("The workbench is prepared for closing.");
    }

    private void VerifyInitialized()
    {
        VerifyAvailable();
        if (!IsStarted) throw new InvalidOperationException("Await InitializeAsync before using the workbench.");
    }

    private SessionModel DocumentOwner(ScriptTab document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Workbench.Sessions.FirstOrDefault(session => session.Files.Contains(document))
            ?? throw new ArgumentException("The document belongs to another workbench.", nameof(document));
    }

    private Task SaveSettingsAsync() => hostingOptions.EnablePersistence
        ? settings.SaveAsync(settingsFilePath) : Task.CompletedTask;

    private void RemoveRecovery(Guid id)
    {
        if (hostingOptions.EnablePersistence) recovery.Remove(id);
    }

    private void DetachSessionUi(SessionModel session)
    {
        progressTrackers.Remove(session);
        if (!sessionUiHandlers.Remove(session, out var handlers)) return;
        session.Engine.ConsoleCleared -= handlers.Clear;
        session.Engine.ProgressChanged -= handlers.Progress;
    }

    internal void HandleHostKeyDown(KeyEventArgs args) => OnWindowKeyDown(this, args);

    internal void AttachHostWindow(Window? window = null)
    {
        window ??= TopLevel.GetTopLevel(this) as Window;
        if (window is null || hostWindow == window || windowClosed) return;
        DetachHostWindow();
        hostWindow = window;
        dialogOwner = window;
        window.Activated += OnHostActivated;
        if (managesWindow)
        {
            window.PropertyChanged += OnHostPropertyChanged;
            window.PositionChanged += OnHostPositionChanged;
            if (hostingOptions.Preferences is not null) ApplyWindowGeometry();
        }
    }

    private void DetachHostWindow()
    {
        if (hostWindow is null) return;
        hostWindow.Activated -= OnHostActivated;
        hostWindow.PropertyChanged -= OnHostPropertyChanged;
        hostWindow.PositionChanged -= OnHostPositionChanged;
        hostWindow = null;
    }

    private async void OnHostActivated(object? sender, EventArgs args)
    {
        if (initialized && !windowClosed) await GuardAsync(CheckExternalFilesAsync);
    }

    private void OnHostPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (initialized && !closingInProgress && (args.Property == Window.BoundsProperty || args.Property == Window.WindowStateProperty))
            CaptureWindowGeometry();
    }

    private void OnHostPositionChanged(object? sender, PixelPointEventArgs args)
    {
        if (initialized && !closingInProgress) CaptureWindowGeometry();
    }
}
