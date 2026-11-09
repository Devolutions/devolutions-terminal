using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Devolutions.Terminal.Automation;
using Devolutions.Terminal.Automation.Mcp;
using ModelContextProtocol.Protocol;
using SkiaSharp;
using Xunit;
using FakeConnection = Devolutions.Terminal.Automation.Tests.TerminalAutomationServiceTests.FakeConnection;

namespace Devolutions.Terminal.Automation.Tests;

public sealed class AutomationVisualIntegrationTests
{
    [Fact]
    public async Task IpcClient_VisualOperationsRoundTripTypedFieldsAndGraphics()
    {
        await using var host = new IsolatedHost();
        var session = await host.Client.StartAsync(new()
        {
            Type = "start",
            CommandLine = "fake",
            Columns = 8,
            Rows = 3,
        });
        var region = new TerminalAutomationRegion(0, 0, 2, 1);
        var captureRequest = new AutomationIpcRequest { Type = "capture", SessionId = session.Id, Region = region };
        var initial = await host.Client.CaptureAsync(captureRequest);
        Assert.Equal(region, initial.Frame.Region);
        Assert.False(initial.Frame.IncludesCursor);
        var pending = host.Client.WaitForChangeAsync(new()
        {
            Type = "wait-change",
            SessionId = session.Id,
            Hash = initial.Frame.Hash,
            TimeoutMilliseconds = 2000,
            Region = region,
        });
        host.Connection.EmitOutput(TerminalAutomationVisualTests.KittyRed);
        var changed = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(changed.Matched);
        Assert.NotEqual(initial.Frame.Hash, changed.Frame.Hash);
        Assert.Equal(region, changed.Frame.Region);
        var red = await host.Client.CaptureAsync(captureRequest);
        using (var bitmap = TerminalAutomationVisualTests.Decode(red))
        {
            Assert.Equal(SKColors.Red, bitmap.GetPixel(2, 2));
        }

        Assert.Equal(red.Frame.Hash, changed.Frame.Hash);
        host.Connection.Writes.Clear(); // Kitty protocol acknowledgement is separate from the input under test.
        host.Connection.OnWrite = text =>
        {
            if (text == "go")
            {
                host.Connection.EmitOutput(TerminalAutomationVisualTests.KittyGreen);
            }
        };
        var probe = await host.Client.ProbeAsync(new()
        {
            Type = "probe",
            SessionId = session.Id,
            Input = new() { Type = "text", Text = "go" },
            TimeoutMilliseconds = 2000,
            Region = region,
        });
        Assert.True(probe.Matched);
        Assert.NotEqual(red.Frame.Hash, probe.Frame.Hash);
        Assert.Contains("go", host.Connection.Writes);
        var performance = await host.Client.MeasureAsync(new()
        {
            Type = "measure",
            SessionId = session.Id,
            DurationMilliseconds = 16,
            IntervalMilliseconds = 16,
            Region = region,
        });
        Assert.Equal(0, performance.ChangedFrames);
        Assert.Equal(0, performance.OutputBytes);
        Assert.Equal(0, performance.ParseMilliseconds);
        Assert.Null(performance.ChangeIntervalMilliseconds);
        Assert.True(performance.Samples.Count >= 2);
        Assert.All(performance.Samples, sample =>
        {
            Assert.Equal(region, sample.Frame.Region);
            Assert.Equal(probe.Frame.Hash, sample.Frame.Hash);
        });

        var withCursor = await host.Client.CaptureAsync(captureRequest with { IncludeCursor = true });
        Assert.True(withCursor.Frame.IncludesCursor);
        Assert.NotEqual(probe.Frame.Hash, withCursor.Frame.Hash);
    }

    [Fact]
    public async Task IpcClient_MouseAndKeyEventsDispatchExactSequences()
    {
        await using var host = new IsolatedHost();
        var session = await host.Service.StartAsync(new("fake", Columns: 8, Rows: 3));
        host.Connection.EmitOutput("\u001b[?1002h\u001b[?1006h\u001b[>3u");
        foreach (var request in new[]
        {
            new AutomationIpcRequest { Type = "mouse", Event = "press", Column = 1, Row = 1, Button = 2, Modifiers = "Control" },
            new AutomationIpcRequest { Type = "mouse", Event = "move", Column = 2, Row = 1 },
            new AutomationIpcRequest { Type = "mouse", Event = "release", Column = 2, Row = 1, Button = 2 },
            new AutomationIpcRequest { Type = "key-event", Event = "release", Key = "Up", Modifiers = "Alt" },
        })
        {
            await host.Client.SendAsync(request with { SessionId = session.Id });
        }

        Assert.Equal(
            ["\u001b[<18;2;2M", "\u001b[<34;3;2M", "\u001b[<2;3;2m", "\u001b[1;3:3A"],
            host.Connection.Writes);
    }

    [Fact]
    public async Task TerminalAutomationTools_ForwardExplicitSymbolsThroughIpcAndProbeInput()
    {
        await using var host = new IsolatedHost();
        var session = await host.Service.StartAsync(new("fake", Columns: 8, Rows: 3));
        var tools = new TerminalAutomationTools(host.Client);

        await tools.SendKey(session.Id, "A", keySymbol: "€");
        await tools.KeyEvent(session.Id, "A", "press", keySymbol: "@");
        Assert.Equal(["€", "@"], host.Connection.Writes);

        host.Connection.Writes.Clear();
        host.Connection.EmitOutput("\u001b[>24u");
        var baseline = host.Service.Capture(session.Id).Frame.Hash;
        host.Connection.OnWrite = _ => host.Connection.EmitOutput(TerminalAutomationVisualTests.SixelRed);
        var result = await tools.ProbeResponse(session.Id, new()
        {
            Type = "key",
            Key = "A",
            KeySymbol = "@",
            Event = "press",
        }, 2000);

        Assert.True(result.Matched);
        Assert.NotEqual(baseline, result.Frame.Hash);
        Assert.Equal(["\u001b[97;;64u"], host.Connection.Writes);
    }

    [Theory]
    [InlineData("wait-change", "Hash")]
    [InlineData("probe", "Input")]
    [InlineData("mouse", "Event")]
    [InlineData("key-event", "Key")]
    public async Task IpcClient_MissingVisualArgumentsReturnErrorsAndReleaseCapacity(string type, string field)
    {
        await using var host = new IsolatedHost();
        var session = await host.Service.StartAsync(new("fake", Columns: 4, Rows: 2));
        var response = await host.Client.SendResponseAsync(new() { Type = type, SessionId = session.Id });
        Assert.False(response.Success);
        Assert.Contains(field + " is required", response.Error, StringComparison.Ordinal);
        Assert.Equal(0, host.ActiveWaiters);
        Assert.Empty(host.Connection.Writes);
    }

    [Theory]
    [InlineData("wait-change")]
    [InlineData("probe")]
    [InlineData("measure")]
    public async Task IpcHost_VisualOperationsShareWaitCapacityAndDisconnectReleasesIt(string type)
    {
        await using var host = new IsolatedHost();
        var session = await host.Service.StartAsync(new("fake", Columns: 4, Rows: 2));
        var hash = host.Service.Capture(session.Id).Frame.Hash;
        using var cancellation = new CancellationTokenSource();
        var request = new AutomationIpcRequest
        {
            Type = type,
            SessionId = session.Id,
            Hash = hash,
            Input = new() { Type = "text", Text = "go" },
            TimeoutMilliseconds = 300000,
            DurationMilliseconds = 30000,
            IntervalMilliseconds = 1000,
        };
        var pending = Enumerable.Range(0, 16)
            .Select(_ => host.Client.SendResponseAsync(request, cancellation.Token)).ToArray();
        try
        {
            await host.WaitForWaiterCountAsync(16);
            var rejected = await host.Client.SendResponseAsync(new()
            {
                Type = "wait-text",
                SessionId = session.Id,
                Text = "absent",
                TimeoutMilliseconds = 1,
            });
            Assert.False(rejected.Success);
            Assert.Contains("16 text waits or visual operations", rejected.Error, StringComparison.Ordinal);
            await host.Client.SendAsync(new() { Type = "send-text", SessionId = session.Id, Text = "still responsive" });
            Assert.Equal("still responsive", host.Connection.Writes[^1]);
            var capture = await host.Client.CaptureAsync(new() { Type = "capture", SessionId = session.Id });
            Assert.Equal(hash, capture.Frame.Hash);
        }
        finally
        {
            await cancellation.CancelAsync();
            await Task.WhenAll(pending.Select(static async operation =>
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation)));
        }

        await host.WaitForWaiterCountAsync(0);
        var available = await host.Client.MeasureAsync(new()
        {
            Type = "measure",
            SessionId = session.Id,
            DurationMilliseconds = 16,
            IntervalMilliseconds = 16,
        });
        Assert.Equal(0, available.ChangedFrames);
    }

    [Fact]
    public void SerializeResponse_CaptureAloneCanExceedNormalBoundButNotCaptureBound()
    {
        var frame = new TerminalAutomationFrame(1, new string('A', 64), 1, 1, 1, 1, new(0, 0, 1, 1), 0);
        var large = new TerminalAutomationCapture(frame, new string('A', AutomationIpcProtocol.MaximumMessageCharacters));
        var json = AutomationIpcHost.SerializeResponse(new(true, Capture: large));
        Assert.True(json.Length > AutomationIpcProtocol.MaximumMessageCharacters);
        var response = JsonSerializer.Deserialize(json, AutomationJsonContext.Default.AutomationIpcResponse)!;
        Assert.True(response.Success);
        Assert.Equal(large, response.Capture);
        var oversized = large with { PngBase64 = new string('A', AutomationIpcProtocol.MaximumCaptureMessageCharacters) };
        var bounded = AutomationIpcHost.SerializeResponse(new(true, Capture: oversized));
        var rejected = JsonSerializer.Deserialize(bounded, AutomationJsonContext.Default.AutomationIpcResponse)!;
        Assert.True(bounded.Length < AutomationIpcProtocol.MaximumMessageCharacters);
        Assert.False(rejected.Success);
        Assert.Contains("exceeds the message limit", rejected.Error, StringComparison.Ordinal);
        Assert.Null(rejected.Capture);
    }

    [Theory]
    [InlineData("capture", true)]
    [InlineData("measure", false)]
    public async Task IpcClient_LargerResponseLimitIsSelectedOnlyForCapture(string type, bool accepted)
    {
        var pipeName = $"dt-automation-test-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var frame = new TerminalAutomationFrame(1, new string('A', 64), 1, 1, 1, 1, new(0, 0, 1, 1), 0);
        var capture = new TerminalAutomationCapture(frame, new string('A', AutomationIpcProtocol.MaximumMessageCharacters));
        var json = AutomationIpcHost.SerializeResponse(new(true, Capture: capture));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exchange = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(cancellation.Token);
            using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var requestJson = await reader.ReadLineAsync(cancellation.Token);
            Assert.NotNull(requestJson);
            using var request = JsonDocument.Parse(requestJson);
            Assert.Equal(type, request.RootElement.GetProperty("type").GetString());
            try
            {
                await writer.WriteLineAsync(json.AsMemory(), cancellation.Token);
            }
            catch (IOException) when (!accepted)
            {
                // Rejection may close the reader while the final chunk is being written.
            }
        }, cancellation.Token);
        var client = new AutomationIpcClient(pipeName);
        if (accepted)
        {
            var response = await client.SendResponseAsync(new() { Type = type }, cancellation.Token);
            Assert.True(response.Success);
            Assert.Equal(capture, response.Capture);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                client.SendResponseAsync(new() { Type = type }, cancellation.Token));
            Assert.Contains("exceeds the message limit", error.Message, StringComparison.Ordinal);
        }

        await exchange;
    }

    [Fact]
    public async Task IpcClient_EnsuresHostAfterPreConnectFailureAndDeliversRequestOnce()
    {
        var pipeName = $"dt-automation-test-{Guid.NewGuid():N}";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 8, Rows: 3));
        var ensureCalls = 0;
        var deliveredRequests = 0;
        AutomationIpcRequest? received = null;
        NamedPipeServerStream? server = null;
        Task? serverTask = null;
        var client = new AutomationIpcClient(pipeName, token =>
        {
            Interlocked.Increment(ref ensureCalls);
            var listeningServer = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            server = listeningServer;
            serverTask = Task.Run(async () =>
            {
                await listeningServer.WaitForConnectionAsync(token);
                using var reader = new StreamReader(listeningServer, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(listeningServer, new UTF8Encoding(false), leaveOpen: true)
                {
                    AutoFlush = true,
                };
                var line = await reader.ReadLineAsync(token);
                Assert.NotNull(line);
                received = JsonSerializer.Deserialize(line, AutomationJsonContext.Default.AutomationIpcRequest);
                service.SendKey(received!.SessionId!, received.Key!, received.Modifiers, received.KeySymbol);
                Interlocked.Increment(ref deliveredRequests);
                await writer.WriteLineAsync(AutomationIpcHost.SerializeResponse(new(true)).AsMemory(), token);
            }, token);
            return Task.CompletedTask;
        });

        try
        {
            await client.SendAsync(new()
            {
                Type = "send-key",
                SessionId = session.Id,
                Key = "A",
                KeySymbol = "€",
            }, cancellation.Token);
            await serverTask!.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (server is not null)
            {
                await server.DisposeAsync();
            }
        }

        Assert.Equal(1, ensureCalls);
        Assert.Equal(1, deliveredRequests);
        Assert.Equal(("send-key", "A", "€"), (received!.Type, received.Key, received.KeySymbol));
        Assert.Equal(["€"], connection.Writes);
    }

    [Fact]
    public async Task IpcClient_DoesNotRetryWhenConnectionFailsAfterRequestDelivery()
    {
        var pipeName = $"dt-automation-test-{Guid.NewGuid():N}";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 8, Rows: 3));
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var ensureCalls = 0;
        var deliveredRequests = 0;
        AutomationIpcRequest? received = null;
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(cancellation.Token);
            using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
            var line = await reader.ReadLineAsync(cancellation.Token);
            Assert.NotNull(line);
            received = JsonSerializer.Deserialize(line, AutomationJsonContext.Default.AutomationIpcRequest);
            service.SendKey(received!.SessionId!, received.Key!, received.Modifiers, received.KeySymbol);
            Interlocked.Increment(ref deliveredRequests);
            await server.DisposeAsync();
        }, cancellation.Token);
        var client = new AutomationIpcClient(pipeName, _ =>
        {
            Interlocked.Increment(ref ensureCalls);
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<EndOfStreamException>(() => client.SendResponseAsync(new()
        {
            Type = "send-key",
            SessionId = session.Id,
            Key = "A",
            KeySymbol = "€",
        }, cancellation.Token));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, ensureCalls);
        Assert.Equal(1, deliveredRequests);
        Assert.Equal(("send-key", "A", "€"), (received!.Type, received.Key, received.KeySymbol));
        Assert.Equal(["€"], connection.Writes);
    }

    [Fact]
    public async Task ReadBoundedLineAsync_CaptureBoundAcceptsExactLimitAndRejectsNextCharacter()
    {
        var text = new string('x', AutomationIpcProtocol.MaximumCaptureMessageCharacters);
        using var valid = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(text + "\n")));
        Assert.Equal(text, await AutomationIpcClient.ReadBoundedLineAsync(
            valid, CancellationToken.None, AutomationIpcProtocol.MaximumCaptureMessageCharacters));
        using var invalid = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(text + "x\n")));
        await Assert.ThrowsAsync<InvalidDataException>(() => AutomationIpcClient.ReadBoundedLineAsync(
            invalid, CancellationToken.None, AutomationIpcProtocol.MaximumCaptureMessageCharacters));
    }

    [Fact]
    public async Task TerminalAutomationTools_ReturnNativePngMetadataAndRoundTripVisualInput()
    {
        await using var host = new IsolatedHost();
        var session = await host.Service.StartAsync(new("fake", Columns: 8, Rows: 3));
        var tools = new TerminalAutomationTools(host.Client);
        var region = new TerminalAutomationRegion(0, 0, 2, 1);
        host.Connection.EmitOutput(TerminalAutomationVisualTests.KittyRed);
        var content = (await tools.Capture(session.Id, region)).ToArray();
        Assert.Equal(2, content.Length);
        var metadata = Assert.IsType<TextContentBlock>(content[0]);
        var image = Assert.IsType<ImageContentBlock>(content[1]);
        Assert.Equal("image/png", image.MimeType);
        var frame = JsonSerializer.Deserialize(metadata.Text, AutomationJsonContext.Default.TerminalAutomationFrame)!;
        Assert.Equal(region, frame.Region);
        Assert.False(frame.IncludesCursor);
        using (var bitmap = SKBitmap.Decode(image.DecodedData.ToArray()))
        {
            Assert.Equal(SKColors.Red, bitmap.GetPixel(2, 2));
            Assert.Equal((frame.PixelWidth, frame.PixelHeight), (bitmap.Width, bitmap.Height));
        }

        var unchanged = await tools.WaitForChange(session.Id, frame.Hash, 20, region);
        Assert.False(unchanged.Matched);
        Assert.Equal(frame.Hash, unchanged.Frame.Hash);
        host.Connection.Writes.Clear();
        host.Connection.OnWrite = text =>
        {
            if (text == "go")
            {
                host.Connection.EmitOutput(TerminalAutomationVisualTests.KittyGreen);
            }
        };
        var probe = await tools.ProbeResponse(session.Id, new() { Type = "text", Text = "go" }, 1000, region);
        Assert.True(probe.Matched);
        Assert.NotEqual(frame.Hash, probe.Frame.Hash);
        Assert.Contains("go", host.Connection.Writes);
        var performance = await tools.Measure(session.Id, 16, 16, region);
        Assert.Equal(0, performance.ChangedFrames);
        Assert.Equal(0, performance.OutputBytes);
        Assert.Equal(0, performance.ParseMilliseconds);
        Assert.All(performance.Samples, sample => Assert.Equal(region, sample.Frame.Region));
        host.Connection.OnWrite = null;
        host.Connection.Writes.Clear();
        host.Connection.EmitOutput("\u001b[?1003h\u001b[?1006h\u001b[>3u");
        await tools.Mouse(session.Id, "move", 1, 1, modifiers: "Shift");
        await tools.KeyEvent(session.Id, "Up", "release", "Control");
        Assert.Equal(["\u001b[<39;2;2M", "\u001b[1;5:3A"], host.Connection.Writes);
    }

    private sealed class IsolatedHost : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly AutomationIpcHost _ipcHost;
        private readonly Task _running;
        private static readonly FieldInfo Waiters = typeof(AutomationIpcHost).GetField(
            "_activeWaiters", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("AutomationIpcHost._activeWaiters");

        public FakeConnection Connection { get; } = new();
        public TerminalAutomationService Service { get; }
        public AutomationIpcClient Client { get; }
        public int ActiveWaiters => (int)Waiters.GetValue(_ipcHost)!;

        public IsolatedHost()
        {
            var pipeName = $"dt-automation-test-{Guid.NewGuid():N}";
            Service = new(() => Connection);
            _ipcHost = new(Service, pipeName);
            Client = new(pipeName);
            _running = _ipcHost.RunAsync(_cancellation.Token);
        }

        public async Task WaitForWaiterCountAsync(int expected)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (ActiveWaiters != expected)
            {
                await Task.Delay(1, deadline.Token);
            }

            Assert.Equal(expected, ActiveWaiters);
        }

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();
            await _running.WaitAsync(TimeSpan.FromSeconds(5));
            await Service.DisposeAsync();
            _cancellation.Dispose();
        }
    }
}
