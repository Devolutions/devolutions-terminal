using System.IO.Pipes;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Devolutions.Terminal.Broker;

namespace Devolutions.Terminal.Automation;

public sealed record AutomationIpcRequest
{
    public required string Type { get; init; }
    public string? SessionId { get; init; }
    public string? CommandLine { get; init; }
    public string? WorkingDirectory { get; init; }
    public int? Columns { get; init; }
    public int? Rows { get; init; }
    public IReadOnlyDictionary<string, string?>? EnvironmentVariables { get; init; }
    public string? Text { get; init; }
    public string? Key { get; init; }
    public string? KeySymbol { get; init; }
    public string? Modifiers { get; init; }
    public int? Column { get; init; }
    public int? Row { get; init; }
    public int? Button { get; init; }
    public int? TimeoutMilliseconds { get; init; }
    public TerminalAutomationRegion? Region { get; init; }
    public bool? IncludeCursor { get; init; }
    public string? Hash { get; init; }
    public TerminalAutomationInput? Input { get; init; }
    public int? DurationMilliseconds { get; init; }
    public int? IntervalMilliseconds { get; init; }
    public string? Event { get; init; }
}

public sealed record AutomationIpcResponse(
    bool Success,
    string? Error = null,
    TerminalAutomationSessionInfo? Session = null,
    IReadOnlyList<TerminalAutomationSessionInfo>? Sessions = null,
    TerminalAutomationSnapshot? Snapshot = null,
    TerminalAutomationWaitResult? WaitResult = null,
    bool Completed = false,
    bool Stopping = false,
    TerminalAutomationCapture? Capture = null,
    TerminalAutomationVisualWaitResult? VisualWaitResult = null,
    TerminalAutomationPerformance? Performance = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AutomationIpcRequest))]
[JsonSerializable(typeof(AutomationIpcResponse))]
[JsonSerializable(typeof(TerminalAutomationSessionInfo))]
[JsonSerializable(typeof(IReadOnlyList<TerminalAutomationSessionInfo>))]
[JsonSerializable(typeof(TerminalAutomationSnapshot))]
[JsonSerializable(typeof(TerminalAutomationWaitResult))]
[JsonSerializable(typeof(TerminalAutomationCommandResult))]
[JsonSerializable(typeof(TerminalAutomationRow))]
[JsonSerializable(typeof(TerminalAutomationCell))]
[JsonSerializable(typeof(TerminalAutomationStyleRun))]
[JsonSerializable(typeof(TerminalAutomationCapture))]
[JsonSerializable(typeof(TerminalAutomationFrame))]
[JsonSerializable(typeof(TerminalAutomationVisualWaitResult))]
[JsonSerializable(typeof(TerminalAutomationPerformance))]
[JsonSerializable(typeof(TerminalAutomationRegion))]
[JsonSerializable(typeof(TerminalAutomationInput))]
public partial class AutomationJsonContext : JsonSerializerContext;

public static class AutomationIpcProtocol
{
    public const int MaximumMessageCharacters = 1_048_576;
    public const int MaximumCaptureMessageCharacters = 24 * 1024 * 1024;
    private const string PipePrefix = "Devolutions.Terminal.Automation.";

    public static string PipeName
    {
        get
        {
            var user = string.IsNullOrEmpty(Environment.UserDomainName)
                ? Environment.UserName
                : Environment.UserDomainName + "\\" + Environment.UserName;
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(user));
            return PipePrefix + Convert.ToHexString(digest.AsSpan(0, 12));
        }
    }

    public static string SerializeJson(TerminalAutomationSessionInfo value) =>
        JsonSerializer.Serialize(value, AutomationJsonContext.Default.TerminalAutomationSessionInfo);

    public static string SerializeJson(IReadOnlyList<TerminalAutomationSessionInfo> value) =>
        JsonSerializer.Serialize(value, AutomationJsonContext.Default.IReadOnlyListTerminalAutomationSessionInfo);

    public static string SerializeJson(TerminalAutomationSnapshot value) =>
        JsonSerializer.Serialize(value, AutomationJsonContext.Default.TerminalAutomationSnapshot);

    public static string SerializeJson(TerminalAutomationWaitResult value) =>
        JsonSerializer.Serialize(value, AutomationJsonContext.Default.TerminalAutomationWaitResult);

    public static string SerializeJson(TerminalAutomationCommandResult value) =>
        JsonSerializer.Serialize(value, AutomationJsonContext.Default.TerminalAutomationCommandResult);

    public static string SerializeJson(TerminalAutomationFrame value) =>
        JsonSerializer.Serialize(value, AutomationJsonContext.Default.TerminalAutomationFrame);

    public static string SerializeJson(TerminalAutomationVisualWaitResult value) =>
        JsonSerializer.Serialize(value, AutomationJsonContext.Default.TerminalAutomationVisualWaitResult);

    public static string SerializeJson(TerminalAutomationPerformance value) =>
        JsonSerializer.Serialize(value, AutomationJsonContext.Default.TerminalAutomationPerformance);
}

public sealed class AutomationIpcClient
{
    private readonly string _pipeName;
    private readonly Func<CancellationToken, Task>? _ensureHost;

    public AutomationIpcClient(Func<CancellationToken, Task>? ensureHost = null)
        : this(AutomationIpcProtocol.PipeName, ensureHost)
    {
    }

    internal AutomationIpcClient(string pipeName, Func<CancellationToken, Task>? ensureHost = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _ensureHost = ensureHost;
    }

    public async Task SendAsync(
        AutomationIpcRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await SendResponseAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.Success)
        {
            throw new InvalidOperationException(response.Error ?? "Automation request failed.");
        }
    }

    public async Task<TerminalAutomationSessionInfo> StartAsync(
        AutomationIpcRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await SendResponseAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return response.Session ?? throw new InvalidDataException("Automation host returned no session.");
    }

    public async Task<IReadOnlyList<TerminalAutomationSessionInfo>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await SendResponseAsync(
            new AutomationIpcRequest { Type = "list" },
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return response.Sessions ?? throw new InvalidDataException("Automation host returned no session list.");
    }

    public async Task<TerminalAutomationSnapshot> ObserveAsync(
        AutomationIpcRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await SendResponseAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return response.Snapshot ?? throw new InvalidDataException("Automation host returned no terminal snapshot.");
    }

    public async Task<TerminalAutomationWaitResult> WaitAsync(
        AutomationIpcRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await SendResponseAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return response.WaitResult ?? throw new InvalidDataException("Automation host returned no wait result.");
    }

    public async Task<AutomationIpcResponse> SendResponseAsync(
        AutomationIpcRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var pipe = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
        };
        var json = JsonSerializer.Serialize(request, AutomationJsonContext.Default.AutomationIpcRequest);
        if (json.Length > AutomationIpcProtocol.MaximumMessageCharacters)
        {
            throw new ArgumentException("Automation request exceeds the message limit.", nameof(request));
        }

        await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        var maximum = request.Type == "capture"
            ? AutomationIpcProtocol.MaximumCaptureMessageCharacters
            : AutomationIpcProtocol.MaximumMessageCharacters;
        var line = await ReadBoundedLineAsync(reader, cancellationToken, maximum).ConfigureAwait(false)
            ?? throw new EndOfStreamException("Automation host closed the pipe without a response.");

        return JsonSerializer.Deserialize(line, AutomationJsonContext.Default.AutomationIpcResponse)
            ?? throw new InvalidDataException("Automation host returned an invalid response.");
    }

    private async Task<NamedPipeClientStream> ConnectAsync(CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(2_000, cancellationToken).ConfigureAwait(false);
            return pipe;
        }
        catch (Exception exception)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            if (_ensureHost is null || exception is not (IOException or TimeoutException))
            {
                throw;
            }

            await _ensureHost(cancellationToken).ConfigureAwait(false);
            var retry = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await retry.ConnectAsync(2_000, cancellationToken).ConfigureAwait(false);
                return retry;
            }
            catch
            {
                await retry.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    internal static async Task<string?> ReadBoundedLineAsync(
        StreamReader reader,
        CancellationToken cancellationToken,
        int maximumCharacters = AutomationIpcProtocol.MaximumMessageCharacters)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.Length == 0 ? null : output.ToString();
            }

            var newline = Array.IndexOf(buffer, '\n', 0, read);
            var charsToAppend = newline >= 0 ? newline : read;
            if (output.Length + charsToAppend > maximumCharacters)
            {
                throw new InvalidDataException("Automation message exceeds the message limit.");
            }

            output.Append(buffer, 0, charsToAppend);
            if (newline >= 0)
            {
                if (output.Length > 0 && output[^1] == '\r')
                {
                    output.Length--;
                }

                return output.ToString();
            }
        }
    }

    private static void EnsureSuccess(AutomationIpcResponse response)
    {
        if (!response.Success)
        {
            throw new InvalidOperationException(response.Error ?? "Automation request failed.");
        }
    }

    public async Task<TerminalAutomationCapture> CaptureAsync(
        AutomationIpcRequest request, CancellationToken cancellationToken = default)
    {
        var response = await SendResponseAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return response.Capture ?? throw new InvalidDataException("Automation host returned no PNG capture.");
    }

    public async Task<TerminalAutomationVisualWaitResult> WaitForChangeAsync(
        AutomationIpcRequest request, CancellationToken cancellationToken = default)
    {
        var response = await SendResponseAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return response.VisualWaitResult ?? throw new InvalidDataException("Automation host returned no visual wait result.");
    }

    public Task<TerminalAutomationVisualWaitResult> ProbeAsync(
        AutomationIpcRequest request, CancellationToken cancellationToken = default) =>
        WaitForChangeAsync(request, cancellationToken);

    public async Task<TerminalAutomationPerformance> MeasureAsync(
        AutomationIpcRequest request, CancellationToken cancellationToken = default)
    {
        var response = await SendResponseAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return response.Performance ?? throw new InvalidDataException("Automation host returned no performance sample.");
    }
}

public sealed class AutomationIpcHost(TerminalAutomationService service)
{
    private const int MaximumClients = 64;
    private const int MaximumWaiters = 16;
    private readonly string _pipeName = AutomationIpcProtocol.PipeName;
    private int _activeWaiters;

    internal AutomationIpcHost(TerminalAutomationService service, string pipeName) : this(service)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        using var election = BrokerElection.TryAcquire($"Global\\{_pipeName}.owner")
            ?? throw new InvalidOperationException("Another automation host already owns this endpoint.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var capacity = new SemaphoreSlim(MaximumClients, MaximumClients);
        var clients = new ConcurrentDictionary<long, Task>();
        long nextClientId = 0;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await capacity.WaitAsync(lifetime.Token).ConfigureAwait(false);
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
                    var id = ++nextClientId;
                    var task = HandleConnectedClientAsync(pipe, lifetime, capacity);
                    clients[id] = task;
                    _ = task.ContinueWith(
                        completed =>
                        {
                            if (completed.Exception is { } exception)
                            {
                                Trace.TraceError("Terminal automation client failed: {0}", exception.GetBaseException());
                            }

                            clients.TryRemove(id, out _);
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    pipe = null;
                }
                finally
                {
                    if (pipe is not null)
                    {
                        await pipe.DisposeAsync().ConfigureAwait(false);
                        capacity.Release();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }

        await Task.WhenAll(clients.Values).ConfigureAwait(false);
    }

    private async Task HandleConnectedClientAsync(
        NamedPipeServerStream pipe,
        CancellationTokenSource lifetime,
        SemaphoreSlim capacity)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                if (await HandleClientAsync(pipe, lifetime.Token).ConfigureAwait(false))
                {
                    await lifetime.CancelAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (IOException exception)
            {
                Trace.TraceError("Terminal automation client disconnected: {0}", exception.Message);
            }
            finally
            {
                capacity.Release();
            }
        }
    }

    private async Task<bool> HandleClientAsync(Stream pipe, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
        };

        AutomationIpcResponse response;
        try
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            string? line;
            try
            {
                line = await AutomationIpcClient.ReadBoundedLineAsync(reader, readTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Timed out reading the automation request.");
            }

            if (line is null || line.Length == 0 || line.Length > AutomationIpcProtocol.MaximumMessageCharacters)
            {
                throw new InvalidDataException("Automation request is empty or exceeds the message limit.");
            }

            var request = JsonSerializer.Deserialize(line, AutomationJsonContext.Default.AutomationIpcRequest)
                ?? throw new InvalidDataException("Automation request is invalid.");
            (AutomationIpcResponse result, bool stop) dispatch;
            if (request.Type is "wait-text" or "wait-change" or "probe" or "measure" && pipe is NamedPipeServerStream)
            {
                using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var disconnectMonitor = CancelOnDisconnectAsync(reader, requestLifetime);
                try
                {
                    dispatch = await DispatchAsync(request, requestLifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    requestLifetime.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    return false;
                }
                finally
                {
                    await requestLifetime.CancelAsync().ConfigureAwait(false);
                    await disconnectMonitor.ConfigureAwait(false);
                }
            }
            else
            {
                dispatch = await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
            }

            var (result, stop) = dispatch;
            response = result;
            if (stop)
            {
                await writer.WriteLineAsync(
                    SerializeResponse(response).AsMemory(),
                    cancellationToken).ConfigureAwait(false);
                return true;
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException or
                                          InvalidOperationException or KeyNotFoundException or
                                          PlatformNotSupportedException or TimeoutException or IOException or
                                          System.ComponentModel.Win32Exception)
        {
            response = new AutomationIpcResponse(false, exception.Message);
        }

        var output = SerializeResponse(response);
        await writer.WriteLineAsync(output.AsMemory(), cancellationToken).ConfigureAwait(false);
        return false;
    }

    private static async Task CancelOnDisconnectAsync(
        StreamReader reader,
        CancellationTokenSource requestLifetime)
    {
        try
        {
            var buffer = new char[1];
            if (await reader.ReadAsync(buffer.AsMemory(), requestLifetime.Token).ConfigureAwait(false) != 0)
            {
                throw new InvalidDataException("Only one automation request is allowed per connection.");
            }
        }
        catch (OperationCanceledException) when (requestLifetime.IsCancellationRequested)
        {
            return;
        }
        catch (IOException)
        {
        }
        finally
        {
            if (!requestLifetime.IsCancellationRequested)
            {
                await requestLifetime.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    internal static string SerializeResponse(AutomationIpcResponse response)
    {
        var output = JsonSerializer.Serialize(response, AutomationJsonContext.Default.AutomationIpcResponse);
        var maximum = response.Capture is not null
            ? AutomationIpcProtocol.MaximumCaptureMessageCharacters
            : AutomationIpcProtocol.MaximumMessageCharacters;
        return output.Length <= maximum
            ? output
            : JsonSerializer.Serialize(
                new AutomationIpcResponse(false, "Automation response exceeds the message limit; resize the terminal or reduce its output."),
                AutomationJsonContext.Default.AutomationIpcResponse);
    }

    private async Task<(AutomationIpcResponse Response, bool Stop)> DispatchAsync(
        AutomationIpcRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Type == "stop")
        {
            return (new AutomationIpcResponse(true, Stopping: true), true);
        }

        var response = request.Type switch
        {
            "start" => new AutomationIpcResponse(true, Session: await service.StartAsync(
                new TerminalAutomationStartRequest(
                    Required(request.CommandLine, nameof(request.CommandLine)),
                    request.WorkingDirectory,
                    request.Columns ?? 120,
                    request.Rows ?? 30,
                    request.EnvironmentVariables),
                cancellationToken).ConfigureAwait(false)),
            "list" => new AutomationIpcResponse(true, Sessions: service.List()),
            "observe" => new AutomationIpcResponse(
                true,
                Snapshot: service.Observe(Required(request.SessionId, nameof(request.SessionId)))),
            "capture" => new AutomationIpcResponse(true, Capture: service.Capture(
                Required(request.SessionId, nameof(request.SessionId)), request.Region, request.IncludeCursor ?? false)),
            "wait-change" => await RunBoundedWaitAsync(async () => new AutomationIpcResponse(true,
                VisualWaitResult: await service.WaitForChangeAsync(
                    Required(request.SessionId, nameof(request.SessionId)), Required(request.Hash, nameof(request.Hash)),
                    TimeSpan.FromMilliseconds(request.TimeoutMilliseconds ?? 10_000), request.Region,
                    cancellationToken).ConfigureAwait(false))).ConfigureAwait(false),
            "probe" => await RunBoundedWaitAsync(async () => new AutomationIpcResponse(true,
                VisualWaitResult: await service.ProbeAsync(
                    Required(request.SessionId, nameof(request.SessionId)),
                    request.Input ?? throw new ArgumentException("Input is required."),
                    TimeSpan.FromMilliseconds(request.TimeoutMilliseconds ?? 10_000), request.Region,
                    cancellationToken).ConfigureAwait(false))).ConfigureAwait(false),
            "measure" => await RunBoundedWaitAsync(async () => new AutomationIpcResponse(true,
                Performance: await service.MeasureAsync(
                    Required(request.SessionId, nameof(request.SessionId)), request.DurationMilliseconds ?? 1_000,
                    request.IntervalMilliseconds ?? 16, request.Region, cancellationToken).ConfigureAwait(false))).ConfigureAwait(false),
            "mouse" => Complete(() => service.Mouse(
                Required(request.SessionId, nameof(request.SessionId)), Required(request.Event, nameof(request.Event)),
                request.Column ?? -1, request.Row ?? -1, request.Button ?? 0, request.Modifiers)),
            "key-event" => Complete(() => service.KeyEvent(
                Required(request.SessionId, nameof(request.SessionId)), Required(request.Key, nameof(request.Key)),
                Required(request.Event, nameof(request.Event)), request.Modifiers, request.KeySymbol)),
            "send-text" => Complete(() => service.SendText(
                Required(request.SessionId, nameof(request.SessionId)),
                RequiredText(request.Text))),
            "paste" => Complete(() => service.Paste(
                Required(request.SessionId, nameof(request.SessionId)),
                RequiredText(request.Text))),
            "send-key" => Complete(() => service.SendKey(
                Required(request.SessionId, nameof(request.SessionId)),
                Required(request.Key, nameof(request.Key)),
                request.Modifiers, request.KeySymbol)),
            "click" => Complete(() => service.Click(
                Required(request.SessionId, nameof(request.SessionId)),
                request.Column ?? -1,
                request.Row ?? -1,
                request.Button ?? 0)),
            "resize" => Complete(() => service.Resize(
                Required(request.SessionId, nameof(request.SessionId)),
                request.Columns ?? 0,
                request.Rows ?? 0)),
            "wait-text" => await WaitForTextAsync(request, cancellationToken).ConfigureAwait(false),
            "close" => await CloseAsync(Required(request.SessionId, nameof(request.SessionId))).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unsupported automation operation '{request.Type}'."),
        };

        return (response, false);
    }

    private async Task<AutomationIpcResponse> WaitForTextAsync(
        AutomationIpcRequest request,
        CancellationToken cancellationToken) =>
        await RunBoundedWaitAsync(async () =>
        {
            var result = await service.WaitForTextAsync(
                Required(request.SessionId, nameof(request.SessionId)),
                RequiredText(request.Text),
                TimeSpan.FromMilliseconds(request.TimeoutMilliseconds ?? 10_000),
                cancellationToken).ConfigureAwait(false);
            return new AutomationIpcResponse(true, WaitResult: result);
        }).ConfigureAwait(false);

    private async Task<AutomationIpcResponse> RunBoundedWaitAsync(Func<Task<AutomationIpcResponse>> operation)
    {
        if (Interlocked.Increment(ref _activeWaiters) > MaximumWaiters)
        {
            Interlocked.Decrement(ref _activeWaiters);
            throw new InvalidOperationException($"At most {MaximumWaiters} text waits or visual operations may run concurrently.");
        }

        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeWaiters);
        }
    }

    private async Task<AutomationIpcResponse> CloseAsync(string id)
    {
        await service.CloseAsync(id).ConfigureAwait(false);
        return new AutomationIpcResponse(true, Completed: true);
    }

    private static AutomationIpcResponse Complete(Action action)
    {
        action();
        return new AutomationIpcResponse(true, Completed: true);
    }

    private static string Required(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"{name} is required.");

    private static string RequiredText(string? value) =>
        value ?? throw new ArgumentException("Text is required.");
}
