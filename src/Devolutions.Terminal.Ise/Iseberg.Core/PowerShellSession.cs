using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Iseberg.Core;

/// <summary>Owns one installed pwsh subprocess. The desktop never loads the PowerShell engine.</summary>
public sealed class PowerShellSession : IAsyncDisposable
{
    private readonly string? snippetDirectory;
    private readonly object sync = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly StringBuilder diagnostics = new();
    private BridgeConnection? connection;
    private NamedPipeServerStream? pipe;
    private Process? child;
    private Task? outputDrain;
    private Task? errorDrain;
    private Task? exitObservation;
    private Task? disposal;
    private Task terminalSizeUpdate = Task.CompletedTask;
    private volatile SessionStatus status = new(SessionState.Starting, "PS> ", "", Guid.Empty, Guid.Empty,
        false, false, null, false, false, "[Nested 0]: PS> ");
    private int initializing;
    private int executionRequests;
    private int failed;
    private volatile bool authenticated;
    private bool initialized;
    private volatile bool closing;
    private int columns = 120;
    private int rows = 40;
    public IseSnippetService Snippets { get; }
    public Func<string, JsonElement, CancellationToken, Task<JsonElement>>? ScriptingCallback { get; set; }
    public string? ExecutablePath { get; init; }
    public string ModulePath { get; init; } = PowerShellProcessDiscovery.ModulePath;
    public int? ChildProcessId => child?.Id;
    public event Action<OutputEntry>? Output;
    public event Action<SessionState>? StateChanged;
    public event Action<InputRequest>? InputRequested;
    public event Action<ShowCommandRequest>? ShowCommandRequested;
    public event Action<CommandErrorRequest>? CommandErrorRequested;
    public event Action<ProgressUpdate>? ProgressChanged;
    public event Action<DebugLocation?>? DebuggerStopped;
    public event Action? ConsoleCleared;
    public event Action? RunspaceChanged;
    public SessionState State => status.State;
    /// <summary>True until an execution or connection request completes, including its final state events.</summary>
    public bool IsExecuting => Volatile.Read(ref executionRequests) != 0;
    public string Prompt => status.Prompt;
    public string Version => status.Version;
    public Guid LocalRunspaceId => status.LocalRunspaceId;
    public Guid RunspaceId => status.RunspaceId;
    public bool IsRunspacePushed => status.IsRunspacePushed;
    public bool IsRemote => status.IsRemote;
    public string? RemoteComputerName => status.RemoteComputerName;
    public string DebugPrompt => IsRemote ? $"[{RemoteComputerName}]: [DBG]: PS> " : "[DBG]: PS> ";
    public bool IsDebuggerPaused => State == SessionState.Debugging && status.IsDebuggerPaused;
    public bool IsNestedPromptActive => State == SessionState.NestedPrompt && status.IsNestedPromptActive;
    public string NestedPrompt => status.NestedPrompt;

    public PowerShellSession(string? snippetDirectory = null)
    {
        this.snippetDirectory = snippetDirectory;
        Snippets = new(snippetDirectory) { LoadFromSessionAsync = LoadSnippetsAsync };
    }

    public async Task InitializeAsync()
    {
        if (Interlocked.Exchange(ref initializing, 1) != 0)
            throw new InvalidOperationException("This PowerShell tab has already been initialized.");
        ObjectDisposedException.ThrowIf(closing, this);
        try
        {
            if (!Path.IsPathFullyQualified(ModulePath) || !File.Exists(ModulePath))
                throw new FileNotFoundException("The DT-owned Iseberg PowerShell bridge module is missing.", ModulePath);
            var contractPath = Path.Combine(Path.GetDirectoryName(ModulePath)!, "Iseberg.Contracts.dll");
            if (!File.Exists(contractPath))
                throw new FileNotFoundException("The DT-owned Iseberg PowerShell bridge payload is incomplete: Iseberg.Contracts.dll is missing.", contractPath);
            var executable = ExecutablePath ?? PowerShellProcessDiscovery.FindExecutable();
            var endpoint = "dt-iseberg-" + Guid.NewGuid().ToString("N");
            var authentication = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var challenge = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            pipe = new(endpoint, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using var parent = Process.GetCurrentProcess();
            var bootstrap = "Import-Module -Name '" + ModulePath.Replace("'", "''") +
                "' -ErrorAction Stop; Start-IsebergBridge -PipeName '" + endpoint +
                "' -ParentProcessId " + Environment.ProcessId +
                " -ParentStartTimeUtcTicks " + parent.StartTime.ToUniversalTime().Ticks;
            foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", bootstrap })
                start.ArgumentList.Add(argument);
            start.Environment[IseBridgeProtocol.AuthenticationEnvironment] = authentication;
            child = Process.Start(start) ?? throw new InvalidOperationException("The PowerShell bridge did not start.");
            outputDrain = DrainAsync(child.StandardOutput);
            errorDrain = DrainAsync(child.StandardError);
            exitObservation = ObserveExitAsync(child);
            await pipe.WaitForConnectionAsync(lifetime.Token).WaitAsync(TimeSpan.FromSeconds(30), lifetime.Token).ConfigureAwait(false);
            connection = new(pipe) { Request = HandleRequestAsync, Event = HandleEvent };
            connection.Disconnected += OnDisconnected;
            connection.Start();
            var hello = await CallAsync<BridgeHello, BridgeHelloResult>("hello",
                new(IseBridgeProtocol.Version, IseBridgeProtocol.BuildIdentity,
                    IseBridgeProtocol.AuthenticationProof(authentication, challenge, "parent"), challenge,
                    Environment.ProcessId, parent.StartTime.ToUniversalTime().Ticks))
                .WaitAsync(TimeSpan.FromSeconds(15), lifetime.Token).ConfigureAwait(false);
            if (hello.Protocol != IseBridgeProtocol.Version || hello.Build != IseBridgeProtocol.BuildIdentity ||
                hello.ProcessId != child.Id ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(hello.AuthenticationProof),
                    Encoding.UTF8.GetBytes(IseBridgeProtocol.AuthenticationProof(authentication, challenge, "child"))) ||
                !System.Version.TryParse(hello.PowerShellVersion, out var version) ||
                !PowerShellProcessDiscovery.IsSupportedVersion(version))
                throw new InvalidOperationException($"Iseberg requires the matching DT bridge build and PowerShell {PowerShellCompatibility.SupportedVersions}.");
            authenticated = true;
            await SendAsync("initialize", new SessionInitialize(snippetDirectory, ScriptingCallback is not null)).ConfigureAwait(false);
            Task resize;
            lock (sync)
            {
                initialized = true;
                resize = terminalSizeUpdate = UpdateTerminalSizeAsync(terminalSizeUpdate, columns, rows);
            }
            await resize.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LoseSession(exception);
            await TerminateChildAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void SetTerminalSize(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        Task update;
        lock (sync)
        {
            this.columns = columns;
            this.rows = rows;
            if (!initialized || State is SessionState.Failed or SessionState.Disposed) return;
            update = terminalSizeUpdate = UpdateTerminalSizeAsync(terminalSizeUpdate, columns, rows);
        }
        Observe(update);
    }

    private async Task UpdateTerminalSizeAsync(Task previous, int columns, int rows)
    {
        await previous.ConfigureAwait(false);
        await SendAsync("terminalSize", new TerminalSizeRequest(columns, rows)).ConfigureAwait(false);
    }

    public Task SetWorkingDirectoryAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute directory is required.", nameof(path));
        return SendAsync("workingDirectory", new TextRequest(path));
    }
    public Task ExecuteAsync(string script, string? filePath = null) =>
        SendExecutionAsync("execute", new ExecuteRequest(script, filePath));
    public Task ExecuteMenuActionAsync(string callbackHandle) =>
        SendExecutionAsync("menuAction", new TextRequest(callbackHandle));
    public Task StopAsync() => connection is null || State is SessionState.Failed or SessionState.Disposed
        ? Task.CompletedTask : SendAsync("stop", true);
    public void Resume(DebuggerResumeAction action)
    {
        if (!IsDebuggerPaused) throw new InvalidOperationException("The debugger is not paused.");
        status = status with { IsDebuggerPaused = false };
        Observe(SendAsync("resume", new ResumeRequest(action)));
    }
    public void BreakAll()
    {
        if (State != SessionState.Running) throw new InvalidOperationException("A script must be running to break execution.");
        Observe(SendAsync("breakAll", true));
    }
    public Task PauseForBreakpointEditAsync(CancellationToken cancellationToken = default) =>
        SendAsync("pauseForBreakpointEdit", true, cancellationToken);
    public Task EvaluateAsync(string script) => SendAsync("evaluate", new TextRequest(script));
    public Task EvaluateNestedAsync(string script) => SendAsync("evaluateNested", new TextRequest(script));
    public Task<CompletionSet> CompleteAsync(string text, int cursor, CancellationToken cancellationToken = default) =>
        CallAsync<CompleteRequest, CompletionSet>("complete", new(text, cursor), cancellationToken);
    public Task<ScriptAnalysis> AnalyzeAsync(string text, string? documentPath = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(closing || State == SessionState.Disposed, this);
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        return EditorAnalysis.IsXmlDocument(documentPath) ? Task.FromResult(EditorAnalysis.AnalyzeXml(text))
            : CallAsync<AnalyzeRequest, ScriptAnalysis>("analyze", new(text, documentPath), cancellationToken);
    }
    public async Task<IReadOnlyList<CommandDescription>> GetCommandsAsync(CancellationToken cancellationToken = default) =>
        await CallAsync<bool, CommandDescription[]>("commands", true, cancellationToken).ConfigureAwait(false);
    public Task<string> GetHelpAsync(string name) => CallAsync<TextRequest, string>("help", new(name));
    public Task<CommandHelpDocument> GetHelpDocumentAsync(string name) =>
        CallAsync<TextRequest, CommandHelpDocument>("helpDocument", new(name));
    public Task<CommandFormDescription> GetCommandFormAsync(string name, string? module = null,
        CancellationToken cancellationToken = default) =>
        CallAsync<CommandFormRequest, CommandFormDescription>("commandForm", new(name, module), cancellationToken);
    public async Task<Uri?> GetHelpUriAsync(string name)
    {
        var value = await CallAsync<TextRequest, HostResponse>("helpUri", new(name)).ConfigureAwait(false);
        return value.Text is null ? null : new Uri(value.Text, UriKind.Absolute);
    }
    public Task<CommandFormResult> BuildCommandFormAsync(CommandForm form, CancellationToken cancellationToken = default) =>
        CallAsync<CommandFormBuildRequest, CommandFormResult>("buildCommandForm", form.ToRequest(), cancellationToken);
    public Task ValidateBreakpointAsync(BreakpointSpec spec, CancellationToken cancellationToken = default) =>
        SendAsync("validateBreakpoint", spec, cancellationToken);
    public Task<DebugSnapshot> InspectAsync(IEnumerable<string> watches, int frameIndex = 0) =>
        CallAsync<InspectRequest, DebugSnapshot>("inspect", new(watches.ToArray(), frameIndex));
    public Task<DebugChildren> GetValueChildrenAsync(long reference, int offset = 0, int count = 100) =>
        CallAsync<ValueChildrenRequest, DebugChildren>("valueChildren", new(reference, offset, count));
    public async Task<IReadOnlyList<DebugBreakpoint>> GetBreakpointsAsync() =>
        await CallAsync<bool, DebugBreakpoint[]>("breakpoints", true).ConfigureAwait(false);
    public Task<DebugBreakpoint> AddBreakpointAsync(BreakpointSpec spec) =>
        CallAsync<BreakpointSpec, DebugBreakpoint>("addBreakpoint", spec);
    public Task<DebugBreakpoint> UpdateBreakpointAsync(int id, BreakpointSpec spec) =>
        CallAsync<BreakpointUpdateRequest, DebugBreakpoint>("updateBreakpoint", new(id, spec));
    public Task SetBreakpointEnabledAsync(int id, bool enabled) =>
        SendAsync("enableBreakpoint", new BreakpointIdRequest(id, enabled));
    public Task RemoveBreakpointAsync(int id) => SendAsync("removeBreakpoint", new BreakpointIdRequest(id));
    public Task RemoveAllBreakpointsAsync() => SendAsync("removeAllBreakpoints", true);
    public Task SetLineBreakpointsAsync(string path, IEnumerable<BreakpointSpec> specs) =>
        SendAsync("lineBreakpoints", new LineBreakpointsRequest(path, specs.ToArray()));
    public Task SetBreakpointsAsync(string path, IEnumerable<int> lines) =>
        SetLineBreakpointsAsync(path, lines.Select(line => new BreakpointSpec(BreakpointKind.Line, path, Line: line)));
    public Task ConnectAsync(RemoteConnectionInfo remote, CancellationToken cancellationToken = default) =>
        SendExecutionAsync("connect", remote, cancellationToken);
    public Task ExitRemoteSessionAsync() => SendExecutionAsync("exitRemoteSession", true);
    public Task<ScriptFile> OpenRemoteFileAsync(string path) => OpenRemoteFileAsync(path, null);
    public async Task<ScriptFile> OpenRemoteFileAsync(string path, ScriptEncoding? choice)
    {
        var file = await CallAsync<RemoteOpenRequest, RemoteFileData>("openRemoteFile", new(path, choice)).ConfigureAwait(false);
        return ScriptFile.FromRemoteBytes(file.Path, file.Bytes, file.RunspaceId, file.ComputerName, choice);
    }
    public Task SaveRemoteFileAsync(ScriptFile file, string? path = null) =>
        SaveRemoteFileCoreAsync(file, path, file.SavedVersion, true);
    public Task SaveRemoteFileAsync(ScriptFile file, string? path, string? expectedVersion) =>
        SaveRemoteFileCoreAsync(file, path, expectedVersion, false);
    private async Task SaveRemoteFileCoreAsync(ScriptFile file, string? path, string? expectedVersion, bool useSavedVersion)
    {
        if (file.IsRemote && (!IsRemote || file.RemoteRunspaceId != RunspaceId))
            throw new InvalidOperationException("This document belongs to another remote connection. Reconnect and reopen it before saving or running it.");
        if (!file.IsRemote && (!IsRemote || file.Path is not null))
            throw new InvalidOperationException("Only remote documents or untitled scripts can be saved to the remote session.");
        ArgumentException.ThrowIfNullOrWhiteSpace(path ?? file.Path);
        var snapshot = file.Text;
        var encoding = file.EncodingChoice;
        var bytes = ScriptFile.Encode(snapshot, encoding);
        var result = await CallAsync<RemoteSaveRequest, RemoteSaveResult>("saveRemoteFile",
            new(path ?? file.Path!, bytes, expectedVersion, file.IsRemote ? file.Path : null, useSavedVersion, RunspaceId)).ConfigureAwait(false);
        file.MarkRemoteSaved(result.Path, snapshot, encoding, ScriptFile.GetVersion(bytes), result.RunspaceId, result.ComputerName);
    }
    public Task<bool> RemoteFileExistsAsync(string path) => CallAsync<TextRequest, bool>("remoteFileExists", new(path));
    public Task<string?> GetRemoteFileVersionAsync(string path) => ReadRemoteFileVersionAsync(path);
    public async Task<string?> ReadRemoteFileVersionAsync(string path) =>
        (await CallAsync<TextRequest, HostResponse>("remoteFileVersion", new(path)).ConfigureAwait(false)).Text;
    public async Task<bool> HasRemoteFileChangesAsync(ScriptFile file)
    {
        if (!IsRemote || file.RemoteRunspaceId != RunspaceId)
            throw new InvalidOperationException("This document belongs to another remote connection.");
        return !ScriptFile.VersionsMatch(file.SavedVersion, await ReadRemoteFileVersionAsync(file.Path!).ConfigureAwait(false));
    }
    public Task NotifyScriptingAsync(string objectId, string propertyName) =>
        SendAsync("scriptingNotification", new ScriptNotification(objectId, propertyName));
    public Task<SnippetLoadResult> LoadSnippetsAsync() => CallAsync<bool, SnippetLoadResult>("snippets", true);
    public Task CreateSnippetAsync(string title, string description, string text, string author, int caretOffset, bool force) =>
        SendAsync("createSnippet", new SnippetCreateRequest(title, description, text, author, caretOffset, force));
    public Task ImportSnippetAsync(string path, bool recurse) => SendAsync("importSnippet", new SnippetImportRequest(path, recurse));

    private async Task<TResponse> CallAsync<TRequest, TResponse>(string operation, TRequest payload,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(State == SessionState.Disposed || closing && operation != "dispose", this);
        if (State == SessionState.Failed)
            throw new InvalidOperationException("This PowerShell session is unavailable. Documents remain open; reopen the tab to create a new session.");
        var bridge = connection ?? throw new InvalidOperationException("Initialize this PowerShell tab first.");
        try
        {
            return BridgeJson.Read<TResponse>(await bridge.CallAsync(operation, BridgeJson.Element(payload), cancellationToken).ConfigureAwait(false));
        }
        catch (BridgeRemoteException exception) when (exception.Fault.Code == "FileConflict")
        {
            throw new FileConflictException(exception.Fault.FilePath!, exception.Fault.ExpectedVersion, exception.Fault.ActualVersion);
        }
        catch (BridgeRemoteException exception) when (exception.Fault.Code is "PSNotSupportedException" or "NotSupportedException")
        {
            throw new NotSupportedException(exception.Message);
        }
        catch (BridgeRemoteException exception) when (exception.Fault.Code is "ArgumentException" or "ArgumentNullException" or "ArgumentOutOfRangeException")
        {
            throw new ArgumentException(exception.Message);
        }
        catch (BridgeRemoteException exception) when (exception.Fault.Code == "ParseException")
        {
            throw new ArgumentException(exception.Message);
        }
        catch (BridgeRemoteException exception) when (exception.Fault.Code == "ObjectDisposedException")
        {
            throw new ObjectDisposedException(nameof(PowerShellSession), exception.Message);
        }
        catch (BridgeRemoteException exception) when (exception.Fault.Code == "InvalidOperationException")
        {
            throw new InvalidOperationException(exception.Message);
        }
    }
    private async Task SendAsync<T>(string operation, T value, CancellationToken cancellationToken = default) =>
        _ = await CallAsync<T, bool>(operation, value, cancellationToken).ConfigureAwait(false);

    private async Task SendExecutionAsync<T>(string operation, T value, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref executionRequests);
        try
        {
            Task resize;
            lock (sync) resize = terminalSizeUpdate;
            await resize.WaitAsync(cancellationToken).ConfigureAwait(false);
            await SendAsync(operation, value, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // A Ready/runspace event can precede the correlated execution reply.
            if (Interlocked.Decrement(ref executionRequests) == 0 && !closing && State == SessionState.Ready)
                StateChanged?.Invoke(SessionState.Ready);
        }
    }

    private void HandleEvent(string operation, JsonElement payload)
    {
        if (closing || Volatile.Read(ref failed) != 0) return;
        if (!authenticated) throw new InvalidDataException("The private PowerShell bridge sent events before authenticating.");
        switch (operation)
        {
            case "status":
                status = BridgeJson.Read<SessionStatus>(payload);
                StateChanged?.Invoke(status.State);
                break;
            case "output": Output?.Invoke(BridgeJson.Read<OutputEntry>(payload)); break;
            case "progress": ProgressChanged?.Invoke(BridgeJson.Read<ProgressUpdate>(payload)); break;
            case "debugger": DebuggerStopped?.Invoke(payload.ValueKind == JsonValueKind.Null ? null : BridgeJson.Read<DebugLocation>(payload)); break;
            case "clear": ConsoleCleared?.Invoke(); break;
            case "runspace":
                status = BridgeJson.Read<SessionStatus>(payload);
                RunspaceChanged?.Invoke();
                break;
            default: throw new InvalidDataException("Unknown private PowerShell event.");
        }
    }
    private async Task<JsonElement> HandleRequestAsync(string operation, JsonElement payload, CancellationToken cancellationToken)
    {
        if (!authenticated) throw new InvalidOperationException("The private PowerShell bridge has not authenticated.");
        switch (operation)
        {
            case "input":
                var data = BridgeJson.Read<HostInput>(payload);
                var input = new InputRequest(data.Caption, data.Message, data.Secret)
                { Choices = data.Choices, DefaultChoices = data.DefaultChoices, MultipleChoice = data.MultipleChoice };
                using (cancellationToken.Register(() => input.Response.TrySetCanceled(cancellationToken)))
                {
                    if (InputRequested is null) throw new InvalidOperationException("No input handler is attached to this host.");
                    InputRequested.Invoke(input);
                    return BridgeJson.Element(new HostResponse(await input.Response.Task.ConfigureAwait(false)));
                }
            case "showCommand":
                var show = BridgeJson.Read<HostShowCommand>(payload);
                var request = new ShowCommandRequest
                { Command = show.Command, Commands = show.Commands, HelpText = show.HelpText, HelpDocument = show.HelpDocument,
                    HelpUri = show.HelpUri, PassThru = show.PassThru, Width = show.Width, Height = show.Height };
                using (cancellationToken.Register(() => request.Response.TrySetCanceled(cancellationToken)))
                {
                    if (ShowCommandRequested is null) throw new InvalidOperationException("No Show-Command handler is attached to this host.");
                    ShowCommandRequested.Invoke(request);
                    return BridgeJson.Element(new HostResponse(await request.Response.Task.ConfigureAwait(false)));
                }
            case "commandError":
                var error = new CommandErrorRequest(BridgeJson.Read<TextRequest>(payload).Text);
                using (cancellationToken.Register(() => error.Response.TrySetCanceled(cancellationToken)))
                {
                    if (CommandErrorRequested is null) throw new InvalidOperationException("No command error popup handler is attached to this host.");
                    CommandErrorRequested.Invoke(error);
                    await error.Response.Task.ConfigureAwait(false);
                    return BridgeJson.Empty;
                }
            default:
                if ((operation != "ise" && !operation.StartsWith("ise.", StringComparison.Ordinal)) || ScriptingCallback is null)
                    throw new InvalidOperationException($"Unsupported parent scripting operation '{operation}'.");
                return await ScriptingCallback(operation, payload, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DrainAsync(StreamReader reader)
    {
        try
        {
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer, lifetime.Token).ConfigureAwait(false)) != 0)
            {
                // Raw stdout/stderr is diagnostics, never a second script-output transport.
                lock (sync)
                {
                    if (diagnostics.Length + read > 16384) diagnostics.Remove(0, Math.Min(diagnostics.Length, read));
                    diagnostics.Append(buffer, 0, read);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
    private async Task ObserveExitAsync(Process process)
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (!closing) LoseSession(new IOException($"PowerShell child exited ({process.ExitCode}). {StartupDiagnostics()}"));
    }
    private string StartupDiagnostics() { lock (sync) return diagnostics.ToString().Trim(); }
    private void OnDisconnected(Exception exception)
    {
        if (!closing) LoseSession(exception);
    }
    private void LoseSession(Exception exception)
    {
        if (closing || Interlocked.Exchange(ref failed, 1) != 0) return;
        status = status with { State = SessionState.Failed, IsDebuggerPaused = false, IsNestedPromptActive = false };
        lifetime.Cancel();
        pipe?.Dispose();
        DebuggerStopped?.Invoke(null);
        StateChanged?.Invoke(SessionState.Failed);
        Output?.Invoke(new("PowerShell session state was lost: " + exception.Message +
            Environment.NewLine + "Editor documents are preserved. Reopen this tab to start a new session; commands are not replayed." +
            Environment.NewLine, OutputKind.Error));
        _ = TerminateChildAsync();
    }
    private async void Observe(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception exception)
        {
            if (!closing && State != SessionState.Failed)
                Output?.Invoke(new(exception.Message + Environment.NewLine, OutputKind.Error));
        }
    }
    private async Task TerminateChildAsync()
    {
        var process = child;
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    public ValueTask DisposeAsync()
    {
        lock (sync) return new(disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        closing = true;
        try
        {
            if (connection is not null && State != SessionState.Failed)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await SendAsync("dispose", true, deadline.Token).ConfigureAwait(false); }
                catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException) { }
            }
        }
        finally
        {
            lifetime.Cancel();
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
            pipe?.Dispose();
            await TerminateChildAsync().ConfigureAwait(false);
            if (outputDrain is not null) await outputDrain.ConfigureAwait(false);
            if (errorDrain is not null) await errorDrain.ConfigureAwait(false);
            if (exitObservation is not null) await exitObservation.ConfigureAwait(false);
            child?.Dispose();
            lifetime.Dispose();
            ScriptingCallback = null;
            status = status with { State = SessionState.Disposed, IsDebuggerPaused = false, IsNestedPromptActive = false };
            StateChanged?.Invoke(SessionState.Disposed);
        }
    }
}
