using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Devolutions.Terminal.Connection;
using Xunit;

namespace Devolutions.Terminal.Connection.Tests;

public sealed class HostControlTests
{
    [Fact]
    public void ProfileMessageOmitsLocalCommandLine()
    {
        var message = new HostControlMessage
        {
            Type = HostControlProtocol.Profiles,
            Profiles =
            [
                new HostProfileInfo
                {
                    Id = "bash",
                    Name = "Bash",
                    CommandLine = "/bin/bash -l",
                    StartingDirectory = "/home/secret",
                    Launchable = true,
                },
            ],
        };

        var json = JsonSerializer.Serialize(message, HostControlJsonContext.Default.HostControlMessage);
        Assert.DoesNotContain("commandLine", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/bin/bash", json, StringComparison.Ordinal);
        Assert.DoesNotContain("startingDirectory", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/home/secret", json, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"Bash\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionTransportFollowsPageScheme()
    {
        var id = new string('a', 32);
        var ws = HostPtyProtocol.CreateSessionWebSocketUri(new Uri("http://127.0.0.1:5235/"), "/pty/" + id, 80, 24);
        var wss = HostPtyProtocol.CreateSessionWebSocketUri(new Uri("https://127.0.0.1:5235/"), "/PTY/" + id, 90, 30);

        Assert.Equal("ws", ws.Scheme);
        Assert.Equal("/pty/" + id, ws.AbsolutePath);
        Assert.Equal("wss", wss.Scheme);
        Assert.Equal("/pty/" + id, wss.AbsolutePath);
        Assert.Equal("wss", HostPtyProtocol.CreateControlUri(new Uri("https://localhost:5235/")).Scheme);
        Assert.False(HostPtyProtocol.TryParseSessionId("wss://evil.example/pty/" + id, out _));
        Assert.False(HostPtyProtocol.TryParseSessionId("/pty/../" + id, out _));
        Assert.False(HostControlProtocol.IsSessionTransport("https://evil/pty/" + id, id));
    }

    [Fact]
    public async Task ControlChannelLaunchesProfilesOnSeparateSockets()
    {
        var powershell = new HostProfileInfo
        {
            Id = "{61c54bbd-c2c6-5271-96e7-009a87ff44bf}",
            Name = "Windows PowerShell",
            CommandLine = "do-not-trust-client",
            Launchable = true,
        };
        var cmd = new HostProfileInfo
        {
            Id = "{0caa0dad-35be-5f56-a8ff-afceeeaa6101}",
            Name = "Command Prompt",
            Launchable = true,
        };
        var azure = new HostProfileInfo
        {
            Id = "{azure}",
            Name = "Azure Cloud Shell",
            Launchable = false,
            Reason = "not bridged",
        };
        await using var server = ControlServer.Start(
            [powershell, cmd, azure],
            (profile, columns, rows) => new TerminalLaunchOptions
            {
                CommandLine = profile.Id == powershell.Id ? "server-powershell" : "server-cmd",
                Columns = columns,
                Rows = rows,
            },
            () => new LaunchProbeConnection());

        Assert.True(await HostControlClient.IsAvailableAsync(server.HttpBase));
        await using var control = await HostControlClient.ConnectAsync(server.HttpBase);
        var catalog = await control.ListProfilesAsync();
        Assert.Equal(3, catalog.Profiles?.Count);
        Assert.Equal(powershell.Id, catalog.DefaultProfileId);
        await Assert.ThrowsAsync<HostControlException>(() => control.LaunchAsync(azure.Id, 80, 24));
        await Assert.ThrowsAsync<HostControlException>(() => control.LaunchAsync("missing", 80, 24));

        var first = await control.LaunchAsync(powershell.Id, 80, 24);
        var second = await control.LaunchAsync(cmd.Id, 100, 40);
        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.True(HostControlProtocol.IsSessionTransport(first.Transport, first.SessionId!));
        Assert.StartsWith("/pty/", first.Transport, StringComparison.Ordinal);

        await using var left = new HostPtyWebSocketConnection(server.HttpBase, first.Transport);
        await using var right = new HostPtyWebSocketConnection(server.HttpBase, second.Transport);
        var leftOutput = new StringBuilder();
        var rightOutput = new StringBuilder();
        left.OutputReceived += (_, data) => leftOutput.Append(Encoding.UTF8.GetString(data.Span));
        right.OutputReceived += (_, data) => rightOutput.Append(Encoding.UTF8.GetString(data.Span));
        await left.StartAsync("client-must-not-win", null, 80, 24);
        await right.StartAsync("client-must-not-win", null, 100, 40);

        await WaitForAsync(() => leftOutput.ToString().Contains("server-powershell", StringComparison.Ordinal));
        await WaitForAsync(() => rightOutput.ToString().Contains("server-cmd", StringComparison.Ordinal));
        left.Write("alpha\r");
        right.Write("beta\r");
        await WaitForAsync(() => leftOutput.ToString().Contains("alpha", StringComparison.Ordinal));
        await WaitForAsync(() => rightOutput.ToString().Contains("beta", StringComparison.Ordinal));
        Assert.DoesNotContain("beta", leftOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("client-must-not-win", leftOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-trust-client", leftOutput.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchIgnoresClientCommandLine()
    {
        var profile = new HostProfileInfo { Id = "safe", Name = "Safe", Launchable = true };
        await using var server = ControlServer.Start(
            [profile],
            (_, columns, rows) => new TerminalLaunchOptions
            {
                CommandLine = "server-chosen",
                Columns = columns,
                Rows = rows,
            },
            () => new LaunchProbeConnection());

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(HostPtyProtocol.CreateControlUri(server.HttpBase), CancellationToken.None);
        var request = """{"type":"launch","requestId":"r1","profileId":"safe","commandLine":"calc.exe","cols":80,"rows":24}""";
        await socket.SendAsync(Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, true, CancellationToken.None);
        var response = await ReadTextAsync(socket);
        Assert.Contains("\"transport\":\"/pty/", response, StringComparison.Ordinal);
        Assert.DoesNotContain("calc.exe", response, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(response);
        var transport = document.RootElement.GetProperty("transport").GetString();
        await using var terminal = new HostPtyWebSocketConnection(server.HttpBase, transport);
        var output = new StringBuilder();
        terminal.OutputReceived += (_, data) => output.Append(Encoding.UTF8.GetString(data.Span));
        await terminal.StartAsync("calc.exe", null, 80, 24);
        await WaitForAsync(() => output.ToString().Contains("server-chosen", StringComparison.Ordinal));
        Assert.DoesNotContain("calc.exe", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ControlLaunchStartsRealShellOnItsOwnSocket()
    {
        var profile = new HostProfileInfo
        {
            Id = "{61c54bbd-c2c6-5271-96e7-009a87ff44bf}",
            Name = "Windows PowerShell",
            Launchable = true,
        };
        var command = HostPtyWebSocketSession.DefaultCommandLine();
        await using var server = ControlServer.Start(
            [profile],
            (_, columns, rows) => new TerminalLaunchOptions
            {
                CommandLine = command,
                WorkingDirectory = HostPtyWebSocketSession.DefaultWorkingDirectory(),
                Columns = columns,
                Rows = rows,
                InheritEnvironment = true,
            },
            connectionFactory: null);

        await using var control = await HostControlClient.ConnectAsync(server.HttpBase);
        var launched = await control.LaunchAsync(profile.Id, 80, 24);
        await using var connection = new HostPtyWebSocketConnection(server.HttpBase, launched.Transport);
        var output = new StringBuilder();
        connection.OutputReceived += (_, data) => output.Append(Encoding.UTF8.GetString(data.Span));
        await connection.StartAsync(HostPtyWebSocketConnection.ShellName, null, 80, 24);
        connection.Write("echo control-tab-ok\r");
        await WaitForAsync(
            () => output.ToString().Contains("control-tab-ok", StringComparison.Ordinal),
            TimeSpan.FromSeconds(30));
    }

    private static async Task<string> ReadTextAsync(ClientWebSocket socket)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.ToArray());
            }
        }
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        var started = DateTime.UtcNow;
        while (!predicate())
        {
            if (DateTime.UtcNow - started > limit)
            {
                throw new TimeoutException("Timed out waiting for host control output.");
            }

            await Task.Delay(25);
        }
    }

    private sealed class LaunchProbeConnection : IRestartableTerminalConnection
    {
        public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;

#pragma warning disable CS0067
        public event EventHandler<int>? Exited;
        public event EventHandler<Exception>? Faulted;
        public event EventHandler<TerminalExitInfo>? SessionExited;
#pragma warning restore CS0067
        public bool IsRunning { get; private set; }
        public int Columns { get; private set; }
        public int Rows { get; private set; }
        public TerminalConnectionCapabilities Capabilities => TerminalConnectionCapabilities.Resize;
        public TerminalConnectionState State { get; private set; }
        public TerminalProcessMetadata? ProcessMetadata { get; private set; }
        public TerminalExitInfo? LastExitInfo { get; private set; }

        public Task StartAsync(TerminalLaunchOptions options, CancellationToken cancellationToken = default)
        {
            Columns = options.Columns;
            Rows = options.Rows;
            IsRunning = true;
            State = TerminalConnectionState.Connected;
            OutputReceived?.Invoke(this, Encoding.UTF8.GetBytes("started:" + options.CommandLine));
            return Task.CompletedTask;
        }

        public Task StartAsync(string commandLine, string? workingDirectory, int columns, int rows, CancellationToken cancellationToken = default) =>
            StartAsync(new TerminalLaunchOptions
            {
                CommandLine = commandLine,
                WorkingDirectory = workingDirectory,
                Columns = columns,
                Rows = rows,
            }, cancellationToken);

        public void Write(ReadOnlySpan<byte> data) =>
            OutputReceived?.Invoke(this, data.ToArray());

        public void Write(string text) =>
            OutputReceived?.Invoke(this, Encoding.UTF8.GetBytes(text));

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            Write(data.Span);
            return ValueTask.CompletedTask;
        }

        public void Resize(int columns, int rows)
        {
            Columns = columns;
            Rows = rows;
        }

        public Task RestartAsync(TerminalLaunchOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ControlServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly HostSessionBroker _broker;
        private readonly Task _acceptLoop;

        private ControlServer(HttpListener listener, int port, HostSessionBroker broker)
        {
            _listener = listener;
            _broker = broker;
            HttpBase = new Uri($"http://127.0.0.1:{port}/");
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public Uri HttpBase { get; }

        public static ControlServer Start(
            IReadOnlyList<HostProfileInfo> profiles,
            Func<HostProfileInfo, int, int, TerminalLaunchOptions> launchOptions,
            Func<IRestartableTerminalConnection>? connectionFactory)
        {
            var port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            var broker = new HostSessionBroker(
                profiles,
                launchOptions,
                connectionFactory,
                profiles.FirstOrDefault(profile => profile.Launchable)?.Id);
            return new ControlServer(listener, port, broker);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Close();
            await _broker.DisposeAsync();
            try
            {
                await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception) when (_stop.IsCancellationRequested || !_listener.IsListening)
                {
                    break;
                }

                _ = Task.Run(() => HandleAsync(context));
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            var upgraded = false;
            try
            {
                var path = context.Request.Url?.AbsolutePath ?? "/";
                if (path.Equals(HostPtyProtocol.HealthPath, StringComparison.OrdinalIgnoreCase))
                {
                    var payload = "ok"u8.ToArray();
                    context.Response.StatusCode = 200;
                    context.Response.Headers[HostPtyProtocol.HostHeaderName] = HostPtyProtocol.HostHeaderValue;
                    context.Response.Headers[HostPtyProtocol.ControlHeaderName] = HostPtyProtocol.ControlHeaderValue;
                    context.Response.ContentLength64 = payload.Length;
                    await context.Response.OutputStream.WriteAsync(payload);
                    return;
                }

                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 404;
                    return;
                }

                var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
                upgraded = true;
                try
                {
                    if (path.Equals(HostPtyProtocol.ControlPath, StringComparison.OrdinalIgnoreCase))
                    {
                        await _broker.RunControlAsync(socket, "ws", _stop.Token);
                        return;
                    }

                    if (HostPtyProtocol.TryParseSessionId(path, out var sessionId))
                    {
                        var columns = QueryInt(context.Request.Url, "cols", 80);
                        var rows = QueryInt(context.Request.Url, "rows", 24);
                        await _broker.TryAttachAsync(sessionId, socket, columns, rows, _stop.Token);
                    }
                }
                finally
                {
                    socket.Dispose();
                }
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
            }
            finally
            {
                if (!upgraded)
                {
                    try
                    {
                        context.Response.Close();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        private static int QueryInt(Uri? url, string name, int fallback)
        {
            var query = url?.Query;
            if (string.IsNullOrEmpty(query))
            {
                return fallback;
            }

            foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var split = part.Split('=', 2);
                if (split.Length == 2 &&
                    split[0] == name &&
                    int.TryParse(split[1], out var parsed) &&
                    parsed is >= 1 and <= short.MaxValue)
                {
                    return parsed;
                }
            }

            return fallback;
        }

        private static int FreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
