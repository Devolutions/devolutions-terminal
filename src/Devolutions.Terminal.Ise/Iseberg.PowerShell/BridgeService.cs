using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iseberg.Core;

namespace Iseberg.PowerShellHost;

[Cmdlet(VerbsLifecycle.Start, "IsebergBridge")]
public sealed class StartIsebergBridgeCommand : PSCmdlet
{
    private readonly CancellationTokenSource cancellation = new();
    [Parameter(Mandatory = true)] public string PipeName { get; set; } = "";
    [Parameter(Mandatory = true)] public int ParentProcessId { get; set; }
    [Parameter(Mandatory = true)] public long ParentStartTimeUtcTicks { get; set; }

    protected override void EndProcessing()
    {
        var authentication = Environment.GetEnvironmentVariable(IseBridgeProtocol.AuthenticationEnvironment);
        Environment.SetEnvironmentVariable(IseBridgeProtocol.AuthenticationEnvironment, null);
        if (authentication is null) throw new InvalidOperationException("The private DT bridge authentication is missing.");
        using var service = new BridgeService(authentication, ParentProcessId, ParentStartTimeUtcTicks);
        try { service.RunAsync(PipeName, cancellation.Token).GetAwaiter().GetResult(); }
        finally { cancellation.Dispose(); }
    }
    protected override void StopProcessing() => cancellation.Cancel();
}

internal sealed class BridgeService(string authentication, int parentProcessId, long parentStartTimeUtcTicks)
    : IIseScriptingBridge, IDisposable
{
    private readonly ConcurrentDictionary<string, ScriptBlock> callbacks = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private BridgeConnection? connection;
    private ManagedPowerShellSession? engine;
    private IseObjectProxy? proxy;
    private int authenticated;
    private int initialized;
    private int queuedNotifications;
    private bool disposed;

    public async Task RunAsync(string endpoint, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        using var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
        _ = WatchParentAsync(linked.Token);
        await pipe.ConnectAsync(15000, linked.Token).ConfigureAwait(false);
        connection = new(pipe) { Request = DispatchAsync };
        connection.Disconnected += exception =>
        {
            lifetime.Cancel();
            _ = ExitAfterDisconnectAsync();
        };
        connection.Start();
        try { await connection.Completion.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        finally
        {
            if (engine is not null)
            {
                try { await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (Exception) { }
            }
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task WatchParentAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            if (parent.StartTime.ToUniversalTime().Ticks != parentStartTimeUtcTicks) ExitOwnedChild();
            while (!cancellationToken.IsCancellationRequested)
            {
                if (parent.HasExited) ExitOwnedChild();
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception) { ExitOwnedChild(); }
    }

    private static async Task ExitAfterDisconnectAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        ExitOwnedChild();
    }
    private static void ExitOwnedChild()
    {
        using var process = Process.GetCurrentProcess();
        process.Kill(entireProcessTree: true);
    }

    private async Task<JsonElement> DispatchAsync(string operation, JsonElement payload, CancellationToken cancellationToken)
    {
        if (operation == "hello")
        {
            if (Interlocked.CompareExchange(ref authenticated, -1, 0) != 0)
                throw new InvalidOperationException("The private bridge has already authenticated.");
            var hello = BridgeJson.Read<BridgeHello>(payload);
            if (hello.Protocol != IseBridgeProtocol.Version || hello.Build != IseBridgeProtocol.BuildIdentity ||
                hello.ParentProcessId != parentProcessId || hello.ParentStartTimeUtcTicks != parentStartTimeUtcTicks ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(hello.Authentication),
                    Encoding.UTF8.GetBytes(IseBridgeProtocol.AuthenticationProof(authentication, hello.Challenge, "parent"))))
            {
                authenticated = 0;
                throw new InvalidOperationException("DT bridge authentication or build identity mismatch.");
            }
            var version = PSVersionInfo.PSVersion;
            if (!PowerShellCompatibility.IsSupportedRuntime(version, Environment.Version))
                throw new InvalidOperationException($"This DT bridge requires PowerShell {PowerShellCompatibility.SupportedVersions} with its bundled .NET runtime.");
            Volatile.Write(ref authenticated, 1);
            return BridgeJson.Element(new BridgeHelloResult(IseBridgeProtocol.Version, IseBridgeProtocol.BuildIdentity,
                version.ToString(), Environment.ProcessId, IseBridgeProtocol.AuthenticationProof(authentication, hello.Challenge, "child")));
        }
        if (Volatile.Read(ref authenticated) != 1)
            throw new InvalidOperationException("Authenticate the private DT bridge before submitting operations.");
        cancellationToken.ThrowIfCancellationRequested();
        if (operation == "initialize")
        {
            if (Interlocked.Exchange(ref initialized, 1) != 0)
                throw new InvalidOperationException("The private session is already initialized.");
            var initialize = BridgeJson.Read<SessionInitialize>(payload);
            engine = new(initialize.SnippetDirectory);
            WireEvents(engine);
            if (initialize.ScriptingEnabled)
            {
                proxy = new IseObjectProxy(this);
                engine.ConfigureIseObjectModel(proxy);
            }
            await engine.InitializeAsync().ConfigureAwait(false);
            return BridgeJson.Empty;
        }
        if (operation == "analyze")
        {
            var request = BridgeJson.Read<AnalyzeRequest>(payload);
            return BridgeJson.Element(PowerShellEditorAnalysis.Analyze(request.Text, request.DocumentPath));
        }
        var session = engine ?? throw new InvalidOperationException("Initialize the private PowerShell session first.");
        using var registration = cancellationToken.Register(() =>
        {
            // Completion and discovery use their own cancellation lane; do not resume a paused script.
            if (operation is "execute" or "menuAction" or "evaluate" or "evaluateNested" or "dispose")
            {
                try { _ = session.StopAsync(); }
                catch (Exception) { }
            }
        });
        switch (operation)
        {
            case "execute":
                var execute = BridgeJson.Read<ExecuteRequest>(payload);
                await session.ExecuteAsync(execute.Script, execute.FilePath).ConfigureAwait(false);
                break;
            case "menuAction":
                var handle = BridgeJson.Read<TextRequest>(payload).Text;
                if (!callbacks.TryGetValue(handle, out var callback))
                    throw new InvalidOperationException("This Add-ons menu action has expired.");
                await session.ExecuteMenuActionAsync(callback).ConfigureAwait(false);
                break;
            case "stop": await session.StopAsync().ConfigureAwait(false); break;
            case "resume":
                session.Resume((System.Management.Automation.DebuggerResumeAction)(long)BridgeJson.Read<ResumeRequest>(payload).Action);
                break;
            case "breakAll": session.BreakAll(); break;
            case "pauseForBreakpointEdit": await session.PauseForBreakpointEditAsync(cancellationToken).ConfigureAwait(false); break;
            case "workingDirectory":
                await session.SetWorkingDirectoryAsync(BridgeJson.Read<TextRequest>(payload).Text).ConfigureAwait(false);
                break;
            case "terminalSize":
                var size = BridgeJson.Read<TerminalSizeRequest>(payload);
                session.SetTerminalSize(size.Columns, size.Rows);
                break;
            case "evaluate": await session.EvaluateAsync(BridgeJson.Read<TextRequest>(payload).Text).ConfigureAwait(false); break;
            case "evaluateNested": await session.EvaluateNestedAsync(BridgeJson.Read<TextRequest>(payload).Text).ConfigureAwait(false); break;
            case "complete":
                var completion = BridgeJson.Read<CompleteRequest>(payload);
                return BridgeJson.Element(await session.CompleteAsync(completion.Text, completion.Cursor, cancellationToken).ConfigureAwait(false));
            case "commands": return BridgeJson.Element((await session.GetCommandsAsync(cancellationToken).ConfigureAwait(false)).ToArray());
            case "help": return BridgeJson.Element(await session.GetHelpAsync(BridgeJson.Read<TextRequest>(payload).Text).ConfigureAwait(false));
            case "helpDocument": return BridgeJson.Element(await session.GetHelpDocumentAsync(BridgeJson.Read<TextRequest>(payload).Text).ConfigureAwait(false));
            case "helpUri":
                return BridgeJson.Element(new HostResponse((await session.GetHelpUriAsync(BridgeJson.Read<TextRequest>(payload).Text).ConfigureAwait(false))?.AbsoluteUri));
            case "commandForm":
                var command = BridgeJson.Read<CommandFormRequest>(payload);
                return BridgeJson.Element(await session.GetCommandFormAsync(command.Name, command.Module, cancellationToken).ConfigureAwait(false));
            case "buildCommandForm": return BridgeJson.Element(BuildCommandForm(BridgeJson.Read<CommandFormBuildRequest>(payload)));
            case "validateBreakpoint":
                ManagedPowerShellSession.ValidateBreakpoint(BridgeJson.Read<BreakpointSpec>(payload));
                break;
            case "inspect":
                var inspect = BridgeJson.Read<InspectRequest>(payload);
                return BridgeJson.Element(await session.InspectAsync(inspect.Watches, inspect.FrameIndex).ConfigureAwait(false));
            case "valueChildren":
                var children = BridgeJson.Read<ValueChildrenRequest>(payload);
                return BridgeJson.Element(await session.GetValueChildrenAsync(children.Reference, children.Offset, children.Count).ConfigureAwait(false));
            case "breakpoints": return BridgeJson.Element((await session.GetBreakpointsAsync().ConfigureAwait(false)).ToArray());
            case "addBreakpoint": return BridgeJson.Element(await session.AddBreakpointAsync(BridgeJson.Read<BreakpointSpec>(payload)).ConfigureAwait(false));
            case "updateBreakpoint":
                var update = BridgeJson.Read<BreakpointUpdateRequest>(payload);
                return BridgeJson.Element(await session.UpdateBreakpointAsync(update.Id, update.Spec).ConfigureAwait(false));
            case "enableBreakpoint":
                var enable = BridgeJson.Read<BreakpointIdRequest>(payload);
                await session.SetBreakpointEnabledAsync(enable.Id, enable.Enabled).ConfigureAwait(false);
                break;
            case "removeBreakpoint":
                await session.RemoveBreakpointAsync(BridgeJson.Read<BreakpointIdRequest>(payload).Id).ConfigureAwait(false);
                break;
            case "removeAllBreakpoints": await session.RemoveAllBreakpointsAsync().ConfigureAwait(false); break;
            case "lineBreakpoints":
                var lines = BridgeJson.Read<LineBreakpointsRequest>(payload);
                await session.SetLineBreakpointsAsync(lines.Path, lines.Specs).ConfigureAwait(false);
                break;
            case "connect":
                await session.ConnectAsync(CreateConnection(BridgeJson.Read<RemoteConnectionInfo>(payload)), cancellationToken).ConfigureAwait(false);
                break;
            case "exitRemoteSession": await session.ExitRemoteSessionAsync().ConfigureAwait(false); break;
            case "openRemoteFile":
                var open = BridgeJson.Read<RemoteOpenRequest>(payload);
                var file = await session.OpenRemoteFileAsync(open.Path, open.Encoding).ConfigureAwait(false);
                return BridgeJson.Element(new RemoteFileData(file.Path!, ScriptFile.Encode(file.Text, file.EncodingChoice),
                    file.RemoteRunspaceId!.Value, file.RemoteComputerName!));
            case "saveRemoteFile": return BridgeJson.Element(await session.SaveRemoteDataAsync(BridgeJson.Read<RemoteSaveRequest>(payload)).ConfigureAwait(false));
            case "remoteFileExists": return BridgeJson.Element(await session.RemoteFileExistsAsync(BridgeJson.Read<TextRequest>(payload).Text).ConfigureAwait(false));
            case "remoteFileVersion":
                return BridgeJson.Element(new HostResponse(await session.ReadRemoteFileVersionAsync(BridgeJson.Read<TextRequest>(payload).Text).ConfigureAwait(false)));
            case "snippets": return BridgeJson.Element(await session.Snippets.LoadAsync().ConfigureAwait(false));
            case "createSnippet":
                var snippet = BridgeJson.Read<SnippetCreateRequest>(payload);
                session.Snippets.Create(snippet.Title, snippet.Description, snippet.Text, snippet.Author, snippet.CaretOffset, snippet.Force);
                break;
            case "importSnippet":
                var import = BridgeJson.Read<SnippetImportRequest>(payload);
                session.Snippets.Import(import.Path, import.Recurse);
                break;
            case "scriptingNotification":
                var notification = BridgeJson.Read<ScriptNotification>(payload);
                if (proxy is IIseObjectProxyNotifications notifications)
                    notifications.Notify(notification.ObjectId, notification.PropertyName);
                break;
            case "dispose":
                callbacks.Clear();
                await session.DisposeAsync().ConfigureAwait(false);
                break;
            default: throw new InvalidOperationException($"Unsupported private PowerShell operation '{operation}'.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return BridgeJson.Empty;
    }

    private void WireEvents(ManagedPowerShellSession session)
    {
        session.Output += entry =>
        {
            for (var offset = 0; offset < entry.Text.Length; offset += IseBridgeProtocol.MaximumOutputCharacters)
            {
                var length = Math.Min(entry.Text.Length - offset, IseBridgeProtocol.MaximumOutputCharacters);
                Publish("output", entry with { Text = entry.Text.Substring(offset, length), CodeStart = Math.Clamp(entry.CodeStart - offset, 0, length) });
            }
        };
        session.StateChanged += _ => Publish("status", session.Status());
        session.RunspaceChanged += () => Publish("runspace", session.Status());
        session.ProgressChanged += progress => Publish("progress", progress);
        session.ConsoleCleared += () => Publish("clear", true);
        session.DebuggerStopped += location => Publish("debugger", location);
        session.InputRequested += request => _ = CompleteInputAsync(request);
        session.ShowCommandRequested += request => _ = CompleteShowCommandAsync(request);
        session.CommandErrorRequested += request => _ = CompleteCommandErrorAsync(request);
    }
    private void Publish<T>(string operation, T value) => connection!.Publish(operation, BridgeJson.Element(value));

    private async Task CompleteInputAsync(InputRequest request)
    {
        try
        {
            var payload = new HostInput(request.Caption, request.Message, request.Secret, request.Choices,
                request.DefaultChoices, request.MultipleChoice);
            var response = await ReverseAsync<HostInput, HostResponse>("input", payload, request.Response.Task).ConfigureAwait(false);
            request.Response.TrySetResult(response.Text ?? "");
        }
        catch (Exception exception) { request.Response.TrySetException(exception); }
    }
    private async Task CompleteShowCommandAsync(ShowCommandRequest request)
    {
        try
        {
            var payload = new HostShowCommand(request.Command, request.Commands, request.HelpText, request.HelpDocument,
                request.HelpUri, request.PassThru, request.Width, request.Height);
            var response = await ReverseAsync<HostShowCommand, HostResponse>("showCommand", payload, request.Response.Task).ConfigureAwait(false);
            request.Response.TrySetResult(response.Text);
        }
        catch (Exception exception) { request.Response.TrySetException(exception); }
    }
    private async Task CompleteCommandErrorAsync(CommandErrorRequest request)
    {
        try
        {
            _ = await ReverseAsync<TextRequest, bool>("commandError", new(request.Message), request.Response.Task).ConfigureAwait(false);
            request.Response.TrySetResult();
        }
        catch (Exception exception) { request.Response.TrySetException(exception); }
    }
    private async Task<TResponse> ReverseAsync<TRequest, TResponse>(string operation, TRequest request, Task responseTask)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        _ = responseTask.ContinueWith(task =>
        {
            if (task.IsCanceled)
            {
                try { cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return BridgeJson.Read<TResponse>(await connection!.CallAsync(operation, BridgeJson.Element(request), cancellation.Token).ConfigureAwait(false));
    }
    public JsonElement Invoke(string operation, JsonElement arguments)
    {
        if (operation != "ise" && !operation.StartsWith("ise.", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid portable ISE operation name.");
        return connection!.CallAsync(operation, arguments, lifetime.Token).GetAwaiter().GetResult();
    }
    public string RegisterCallback(ScriptBlock action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (callbacks.Count >= IseBridgeProtocol.MaximumPendingRequests * 8)
            throw new InvalidOperationException("Too many portable ISE menu callbacks.");
        var handle = Guid.NewGuid().ToString("N");
        callbacks[handle] = action;
        return handle;
    }
    public void RemoveCallback(string handle) => callbacks.TryRemove(handle, out _);
    public void ScheduleNotification(Action notification)
    {
        if (Interlocked.Increment(ref queuedNotifications) > IseBridgeProtocol.MaximumPendingRequests)
        {
            Interlocked.Decrement(ref queuedNotifications);
            throw new InvalidOperationException("The portable ISE notification queue is full.");
        }
        _ = DeliverNotificationAsync(notification);
    }
    private async Task DeliverNotificationAsync(Action notification)
    {
        try
        {
            await (engine ?? throw new InvalidOperationException("The scripting session is not initialized."))
                .DispatchNotificationAsync(notification).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (!lifetime.IsCancellationRequested)
                Publish("output", new OutputEntry("Portable ISE notification failed: " + exception.Message + Environment.NewLine, OutputKind.Error));
        }
        finally { Interlocked.Decrement(ref queuedNotifications); }
    }

    private static CommandFormResult BuildCommandForm(CommandFormBuildRequest request)
    {
        var form = new CommandForm(request.Description);
        form.SelectSet(request.ParameterSet);
        var validation = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in request.Values)
        {
            var value = form.Value(entry.Key);
            value.Included = entry.Value.Included;
            value.Text = entry.Value.Text;
            value.IsExpression = entry.Value.IsExpression;
            value.Boolean = entry.Value.Boolean;
            if (value.Included && value.IsExpression)
            {
                System.Management.Automation.Language.Parser.ParseInput("(" + value.Text + "\n)", out _, out var errors);
                validation[entry.Key] = !string.IsNullOrWhiteSpace(value.Text) && errors.Length == 0;
            }
        }
        return form.Build(validation);
    }

    private static RunspaceConnectionInfo CreateConnection(RemoteConnectionInfo request)
    {
        if (request.Kind == RemoteConnectionKind.Ssh)
            return new SSHConnectionInfo(request.UserName ?? "", request.ComputerName, request.KeyFilePath,
                request.Port == 0 ? 22 : request.Port, request.Subsystem ?? "powershell");
        if (request.Kind != RemoteConnectionKind.WSMan)
            throw new ArgumentException("Unsupported remote connection kind.");
        PSCredential? credential = null;
        if (request.UserName is { Length: > 0 } user)
        {
            var password = new SecureString();
            foreach (var character in request.Password ?? "") password.AppendChar(character);
            password.MakeReadOnly();
            credential = new(user, password);
        }
        return new WSManConnectionInfo(new Uri(request.ConnectionUri ?? $"http://{request.ComputerName}:{(request.Port == 0 ? 5985 : request.Port)}/wsman"),
            request.ConfigurationName ?? "http://schemas.microsoft.com/powershell/Microsoft.PowerShell", credential);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        callbacks.Clear();
        lifetime.Dispose();
    }
}
