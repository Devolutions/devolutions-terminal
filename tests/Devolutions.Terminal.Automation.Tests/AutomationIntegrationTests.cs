using System.Reflection;
using System.Text;
using System.Text.Json;
using Devolutions.Terminal.Automation;
using Devolutions.Terminal.Automation.Mcp;
using ModelContextProtocol.Server;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Devolutions.Terminal.Automation.Tests;

public sealed class AutomationIntegrationTests
{
    [Fact]
    public async Task IpcHost_ValidatesAndDispatchesJsonOperationsWithoutOpeningSharedPipe()
    {
        var connection = new TerminalAutomationServiceTests.FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var host = new AutomationIpcHost(service);

        var (startStopped, start) = await HandleAsync(host, new AutomationIpcRequest
        {
            Type = "start",
            CommandLine = "fake-command",
            Columns = 12,
            Rows = 4,
        });
        var session = Assert.IsType<TerminalAutomationSessionInfo>(start.Session);
        Assert.False(startStopped);
        Assert.True(start.Success);
        Assert.True(session.IsRunning);
        Assert.Equal((12, 4), (session.Columns, session.Rows));

        var (_, list) = await HandleAsync(host, new AutomationIpcRequest { Type = "list" });
        Assert.True(list.Success);
        Assert.Equal(session, Assert.Single(list.Sessions!));

        connection.EmitOutput("\u001b[?1h\u001b[?2004h\u001b[?1000h\u001b[?1006hREADY A");
        var (_, observe) = await HandleAsync(host, new AutomationIpcRequest
        {
            Type = "observe",
            SessionId = session.Id,
        });
        Assert.True(observe.Success);
        Assert.Contains("READY", observe.Snapshot!.Text, StringComparison.Ordinal);
        Assert.True(observe.Snapshot.ApplicationCursorKeys);
        Assert.True(observe.Snapshot.BracketedPaste);
        Assert.True(observe.Snapshot.MouseTracking);
        Assert.True(observe.Snapshot.SgrMouse);

        foreach (var request in new[]
        {
            new AutomationIpcRequest { Type = "send-text", SessionId = session.Id, Text = "literal" },
            new AutomationIpcRequest { Type = "send-text", SessionId = session.Id, Text = " " },
            new AutomationIpcRequest { Type = "send-text", SessionId = session.Id, Text = "\t" },
            new AutomationIpcRequest { Type = "paste", SessionId = session.Id, Text = "paste" },
            new AutomationIpcRequest { Type = "send-key", SessionId = session.Id, Key = "Up" },
            new AutomationIpcRequest { Type = "click", SessionId = session.Id, Column = 1, Row = 1 },
            new AutomationIpcRequest { Type = "resize", SessionId = session.Id, Columns = 20, Rows = 5 },
        })
        {
            var (_, response) = await HandleAsync(host, request);
            Assert.True(response.Success, response.Error);
            Assert.True(response.Completed);
        }

        Assert.Equal(
            [
                "literal",
                " ",
                "\t",
                "\u001b[200~paste\u001b[201~",
                "\u001bOA",
                "\u001b[<0;2;2M\u001b[<0;2;2m",
            ],
            connection.Writes);

        var (_, resized) = await HandleAsync(host, new AutomationIpcRequest
        {
            Type = "observe",
            SessionId = session.Id,
        });
        Assert.Equal((20, 5), (resized.Snapshot!.Columns, resized.Snapshot.Rows));

        var (_, wait) = await HandleAsync(host, new AutomationIpcRequest
        {
            Type = "wait-text",
            SessionId = session.Id,
            Text = "READY",
            TimeoutMilliseconds = 1_000,
        });
        Assert.True(wait.Success);
        Assert.True(wait.WaitResult!.Matched);

        var (_, waitForSpace) = await HandleAsync(host, new AutomationIpcRequest
        {
            Type = "wait-text",
            SessionId = session.Id,
            Text = " ",
            TimeoutMilliseconds = 1_000,
        });
        Assert.True(waitForSpace.Success);
        Assert.True(waitForSpace.WaitResult!.Matched);

        var (_, close) = await HandleAsync(host, new AutomationIpcRequest
        {
            Type = "close",
            SessionId = session.Id,
        });
        Assert.True(close.Success);
        Assert.True(close.Completed);
        Assert.Equal(1, connection.DisposeCalls);
        Assert.Empty(service.List());

        var (_, missingCommand) = await HandleAsync(host, new AutomationIpcRequest { Type = "start" });
        Assert.False(missingCommand.Success);
        Assert.Contains("CommandLine is required", missingCommand.Error, StringComparison.Ordinal);

        var (_, unsupported) = await HandleAsync(host, new AutomationIpcRequest { Type = "not-a-tool" });
        Assert.False(unsupported.Success);
        Assert.Contains("Unsupported automation operation", unsupported.Error, StringComparison.Ordinal);

        var (_, unknownSession) = await HandleAsync(host, new AutomationIpcRequest
        {
            Type = "observe",
            SessionId = "not-a-session",
        });
        Assert.False(unknownSession.Success);
        Assert.Contains("was not found", unknownSession.Error, StringComparison.Ordinal);

        var (stopped, stop) = await HandleAsync(host, new AutomationIpcRequest { Type = "stop" });
        Assert.True(stopped);
        Assert.True(stop.Success);
        Assert.True(stop.Stopping);
    }

    [Fact]
    public void SerializeResponse_OversizedSnapshotReturnsBoundedError()
    {
        var snapshot = new TerminalAutomationSnapshot(
            "session", 0, new string('x', AutomationIpcProtocol.MaximumMessageCharacters),
            [], "", null, true, null, 1, 1, 0, 0, true, false, false, false, false, false);

        var json = AutomationIpcHost.SerializeResponse(new AutomationIpcResponse(true, Snapshot: snapshot));
        var response = JsonSerializer.Deserialize(json, AutomationJsonContext.Default.AutomationIpcResponse);

        Assert.True(json.Length < AutomationIpcProtocol.MaximumMessageCharacters);
        Assert.False(response!.Success);
        Assert.Contains("exceeds the message limit", response.Error, StringComparison.Ordinal);
        Assert.Null(response.Snapshot);
    }

    [Fact]
    public async Task ReadBoundedLineAsync_AcceptsLimitAndRejectsOneCharacterMore()
    {
        var text = new string('x', AutomationIpcProtocol.MaximumMessageCharacters);
        using var validStream = new MemoryStream(Encoding.UTF8.GetBytes(text + "\n"));
        using var validReader = new StreamReader(validStream);
        Assert.Equal(text, await AutomationIpcClient.ReadBoundedLineAsync(validReader, CancellationToken.None));

        using var oversizedStream = new MemoryStream(Encoding.UTF8.GetBytes(text + "x\n"));
        using var oversizedReader = new StreamReader(oversizedStream);
        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => AutomationIpcClient.ReadBoundedLineAsync(oversizedReader, CancellationToken.None));
        Assert.Contains("exceeds the message limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IpcHost_RejectsAnotherHostForTheSameEndpoint()
    {
        var pipeName = $"dt-automation-test-{Guid.NewGuid():N}";
        await using var firstService = new TerminalAutomationService();
        await using var secondService = new TerminalAutomationService();
        using var cancellation = new CancellationTokenSource();
        var first = new AutomationIpcHost(firstService, pipeName).RunAsync(cancellation.Token);
        var second = new AutomationIpcHost(secondService, pipeName).RunAsync(cancellation.Token);

        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await second.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("already owns", error.Message, StringComparison.Ordinal);
            Assert.False(first.IsCompleted);
        }
        finally
        {
            await cancellation.CancelAsync();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                await second.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    [Fact]
    public async Task IpcClient_UsesNamedPipeForPersistentSessionAndConcurrentInput()
    {
        var pipeName = $"dt-automation-test-{Guid.NewGuid():N}";
        var connection = new TerminalAutomationServiceTests.FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        using var cancellation = new CancellationTokenSource();
        var host = new AutomationIpcHost(service, pipeName).RunAsync(cancellation.Token);

        try
        {
            var client = new AutomationIpcClient(pipeName);
            var session = await client.StartAsync(new AutomationIpcRequest
            {
                Type = "start",
                CommandLine = "fake-command",
                Columns = 20,
                Rows = 4,
            });
            var waiting = client.WaitAsync(new AutomationIpcRequest
            {
                Type = "wait-text",
                SessionId = session.Id,
                Text = "ready",
                TimeoutMilliseconds = 5_000,
            });

            await client.SendAsync(new AutomationIpcRequest
            {
                Type = "send-text",
                SessionId = session.Id,
                Text = " ",
            });
            Assert.Equal([" "], connection.Writes);
            connection.EmitOutput("ready");
            var result = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(result.Matched);
            Assert.Equal(session.Id, result.Snapshot.Id);
            Assert.Equal(session.Id, Assert.Single(await client.ListAsync()).Id);

            await client.SendAsync(new AutomationIpcRequest { Type = "close", SessionId = session.Id });
            Assert.Empty(await client.ListAsync());
        }
        finally
        {
            await cancellation.CancelAsync();
            await host.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task IpcHost_ReservesCapacityForInputWhenWaitLimitIsReached()
    {
        var connection = new TerminalAutomationServiceTests.FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake-command"));
        var host = new AutomationIpcHost(service);
        var request = new AutomationIpcRequest
        {
            Type = "wait-text",
            SessionId = session.Id,
            Text = "ready",
            TimeoutMilliseconds = 5_000,
        };
        var waiters = Enumerable.Range(0, 16).Select(_ => HandleAsync(host, request)).ToArray();
        Assert.All(waiters, waiter => Assert.False(waiter.IsCompleted));

        var (_, rejected) = await HandleAsync(host, request);
        Assert.False(rejected.Success);
        Assert.Contains("16 text waits", rejected.Error, StringComparison.Ordinal);
        var (_, input) = await HandleAsync(host, new AutomationIpcRequest
        {
            Type = "send-text",
            SessionId = session.Id,
            Text = " ",
        });
        Assert.True(input.Success);
        Assert.Equal([" "], connection.Writes);

        connection.EmitOutput("ready");
        var responses = await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(responses, response => Assert.True(response.Response.WaitResult!.Matched));
    }

    [Fact]
    public async Task IpcHost_CancelledClientsReleaseWaitCapacity()
    {
        var pipeName = $"dt-automation-test-{Guid.NewGuid():N}";
        var connection = new TerminalAutomationServiceTests.FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        using var hostCancellation = new CancellationTokenSource();
        var ipcHost = new AutomationIpcHost(service, pipeName);
        var host = ipcHost.RunAsync(hostCancellation.Token);
        using var clientCancellation = new CancellationTokenSource();

        try
        {
            var client = new AutomationIpcClient(pipeName);
            var session = await client.StartAsync(new AutomationIpcRequest
            {
                Type = "start",
                CommandLine = "fake-command",
            });
            var request = new AutomationIpcRequest
            {
                Type = "wait-text",
                SessionId = session.Id,
                Text = "ready",
                TimeoutMilliseconds = 300_000,
            };
            var pending = Enumerable.Range(0, 16)
                .Select(_ => client.WaitAsync(request, clientCancellation.Token))
                .ToArray();

            var activeWaiters = typeof(AutomationIpcHost).GetField(
                "_activeWaiters", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(typeof(AutomationIpcHost).FullName, "_activeWaiters");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && (int)activeWaiters.GetValue(ipcHost)! < 16)
            {
                await Task.Delay(10);
            }

            Assert.Equal(16, (int)activeWaiters.GetValue(ipcHost)!);
            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.WaitAsync(request with { TimeoutMilliseconds = 1 }));
            Assert.Contains("16 text waits", rejected.Message, StringComparison.Ordinal);
            await clientCancellation.CancelAsync();
            await Task.WhenAll(pending.Select(static async operation =>
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation)));

            deadline = DateTime.UtcNow.AddSeconds(5);
            TerminalAutomationWaitResult? result = null;
            while (DateTime.UtcNow < deadline && result is null)
            {
                try
                {
                    result = await client.WaitAsync(request with { Text = "", TimeoutMilliseconds = 1_000 });
                }
                catch (InvalidOperationException exception) when (
                    exception.Message.Contains("16 text waits", StringComparison.Ordinal))
                {
                    await Task.Delay(10);
                }
            }

            Assert.NotNull(result);
            Assert.True(result.Matched);
            Assert.DoesNotContain("ready", result.Snapshot.Text, StringComparison.Ordinal);
        }
        finally
        {
            await clientCancellation.CancelAsync();
            await hostCancellation.CancelAsync();
            await host.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void TerminalAutomationTools_AllPublicOperationsAreRegisteredAsMcpTools()
    {
        var toolType = typeof(TerminalAutomationTools);
        Assert.NotNull(toolType.GetCustomAttribute<McpServerToolTypeAttribute>());
        var methods = toolType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

        Assert.Equal(
            ["Capture", "Click", "Close", "KeyEvent", "List", "Measure", "Mouse", "Observe", "Paste",
                "ProbeResponse", "Resize", "SendKey", "SendText", "Start", "WaitForChange", "WaitForText"],
            methods.Select(static method => method.Name).Order(StringComparer.Ordinal));
        Assert.All(methods, method =>
            Assert.NotNull(method.GetCustomAttribute<McpServerToolAttribute>()));
        Assert.Equal(typeof(int), methods.Single(static method => method.Name == "Click")
            .GetParameters()[1].ParameterType);
        Assert.Equal(10_000, methods.Single(static method => method.Name == "WaitForText")
            .GetParameters()[2].DefaultValue);
    }

    [Fact]
    public async Task TerminalAutomationTools_UseSharedHostForInputWaitAndSnapshots()
    {
        var pipeName = $"dt-automation-test-{Guid.NewGuid():N}";
        var connection = new TerminalAutomationServiceTests.FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        using var cancellation = new CancellationTokenSource();
        var host = new AutomationIpcHost(service, pipeName).RunAsync(cancellation.Token);

        try
        {
            var tools = new TerminalAutomationTools(new AutomationIpcClient(pipeName));
            var session = await tools.Start("fake-command", columns: 20, rows: 4);
            Assert.Equal((20, 4), (session.Columns, session.Rows));
            Assert.Equal(session.Id, Assert.Single(await tools.List()).Id);

            await tools.SendText(session.Id, " ");
            await tools.SendKey(session.Id, "Enter");
            connection.EmitOutput("\u001b[?2004h\u001b[?1000h\u001b[?1006hMCP READY");
            await tools.Paste(session.Id, "paste");
            await tools.Click(session.Id, 1, 1, button: 2);
            await tools.Resize(session.Id, 21, 5);
            Assert.Equal(
                [" ", "\r", "\u001b[200~paste\u001b[201~", "\u001b[<2;2;2M\u001b[<2;2;2m"],
                connection.Writes);

            var wait = await tools.WaitForText(session.Id, "READY", timeoutMilliseconds: 1_000);
            var snapshot = await tools.Observe(session.Id);
            Assert.True(wait.Matched);
            Assert.Contains("MCP READY", snapshot.Text, StringComparison.Ordinal);
            Assert.Equal((21, 5), (snapshot.Columns, snapshot.Rows));
            Assert.Equal(new TerminalAutomationCell(0, "M", 1), snapshot.Screen[0].Cells[0]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => tools.Resize(session.Id, 0, 4));

            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.List(cancelled.Token));

            await tools.Close(session.Id);
            Assert.Empty(await tools.List());
        }
        finally
        {
            await cancellation.CancelAsync();
            await host.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task AutomationIpcClient_RejectsNullRequestBeforeConnecting()
    {
        var client = new AutomationIpcClient();

        await Assert.ThrowsAsync<ArgumentNullException>(() => client.SendResponseAsync(null!));
    }

    private static async Task<(bool Stopped, AutomationIpcResponse Response)> HandleAsync(
        AutomationIpcHost host,
        AutomationIpcRequest request)
    {
        var method = typeof(AutomationIpcHost).GetMethod(
            "HandleClientAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(AutomationIpcHost).FullName, "HandleClientAsync");

        using var stream = new MemoryStream();
        var json = JsonSerializer.Serialize(request, AutomationJsonContext.Default.AutomationIpcRequest);
        var input = Encoding.UTF8.GetBytes(json + "\n");
        await stream.WriteAsync(input);
        stream.Position = 0;
        var operation = (Task<bool>?)method.Invoke(host, [stream, CancellationToken.None])
            ?? throw new InvalidOperationException("IPC host did not return a completion task.");
        var stopped = await operation;

        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        _ = await reader.ReadLineAsync();
        var responseJson = await reader.ReadLineAsync()
            ?? throw new InvalidDataException("IPC host did not write a response.");
        var response = JsonSerializer.Deserialize(
            responseJson,
            AutomationJsonContext.Default.AutomationIpcResponse)
            ?? throw new InvalidDataException("IPC host returned an invalid response.");
        return (stopped, response);
    }
}
