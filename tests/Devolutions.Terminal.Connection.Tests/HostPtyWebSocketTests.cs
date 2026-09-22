using System.Net;
using System.Text;
using Devolutions.Terminal.Connection;
using Xunit;

namespace Devolutions.Terminal.Connection.Tests;

public sealed class HostPtyWebSocketTests
{
    [Fact]
    public void FramesRoundTrip()
    {
        var data = HostPtyProtocol.EncodeData("hi"u8);
        Assert.Equal(HostPtyProtocol.Data, data[0]);
        Assert.Equal("hi", Encoding.UTF8.GetString(data.AsSpan(1)));

        var resize = HostPtyProtocol.EncodeResize(100, 40);
        Assert.True(HostPtyProtocol.TryReadResize(resize, out var columns, out var rows));
        Assert.Equal(100, columns);
        Assert.Equal(40, rows);

        var exit = HostPtyProtocol.EncodeExit(7);
        Assert.True(HostPtyProtocol.TryReadExit(exit, out var code));
        Assert.Equal(7, code);
        Assert.Equal(2, HostPtyProtocol.SplitData(new byte[HostPtyProtocol.MaxPayload + 1]).Count);
    }

    [Theory]
    [InlineData(null, 5235, true)]
    [InlineData("http://127.0.0.1:5235", 5235, true)]
    [InlineData("http://localhost:5235", 5235, true)]
    [InlineData("https://evil.example:5235", 5235, false)]
    [InlineData("http://127.0.0.1:9", 5235, false)]
    public void OriginAllowListIsLoopbackOnly(string? origin, int port, bool allowed)
    {
        Assert.Equal(allowed, HostPtyProtocol.IsAllowedOrigin(origin, port));
        Assert.True(HostPtyProtocol.IsLoopback(IPAddress.Loopback));
        Assert.True(HostPtyProtocol.IsLoopback(IPAddress.IPv6Loopback));
    }

    [Fact]
    public async Task BrowserShellEchoesAcrossWebSocket()
    {
        BrowserShellConnection? shell = null;
        await using var server = LoopbackPtyServer.Start(
            () => shell = new BrowserShellConnection(),
            (columns, rows) => new TerminalLaunchOptions
            {
                CommandLine = BrowserShellConnection.ShellName,
                Columns = columns,
                Rows = rows,
            });

        await using var connection = new HostPtyWebSocketConnection(server.HttpBase);
        var output = new StringBuilder();
        connection.OutputReceived += (_, data) => output.Append(Encoding.UTF8.GetString(data.Span));
        await connection.StartAsync(BrowserShellConnection.ShellName, null, 80, 24);

        await WaitForAsync(() => output.ToString().Contains("dt-wasm", StringComparison.Ordinal));
        connection.Write("echo hello-host\r");
        await WaitForAsync(() => output.ToString().Contains("hello-host", StringComparison.Ordinal));
        connection.Resize(100, 40);
        await WaitForAsync(() => shell is { Columns: 100, Rows: 40 });
        Assert.True(await HostPtyWebSocketConnection.IsBridgeAvailableAsync(server.HttpBase));
    }

    [Fact]
    public async Task RealHostShellEchoesAcrossWebSocket()
    {
        await using var server = LoopbackPtyServer.Start(connectionFactory: null, launchOptions: null);
        await using var connection = new HostPtyWebSocketConnection(server.HttpBase);
        var output = new StringBuilder();
        connection.OutputReceived += (_, data) => output.Append(Encoding.UTF8.GetString(data.Span));
        await connection.StartAsync(HostPtyWebSocketConnection.ShellName, null, 80, 24);

        connection.Write("echo host-pty-ok\r");
        await WaitForAsync(
            () => output.ToString().Contains("host-pty-ok", StringComparison.Ordinal),
            TimeSpan.FromSeconds(30));
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        var started = DateTime.UtcNow;
        while (!predicate())
        {
            if (DateTime.UtcNow - started > limit)
            {
                throw new TimeoutException("Timed out waiting for host PTY output.");
            }

            await Task.Delay(25);
        }
    }

    private sealed class LoopbackPtyServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<IRestartableTerminalConnection>? _connectionFactory;
        private readonly Func<int, int, TerminalLaunchOptions>? _launchOptions;
        private readonly Task _acceptLoop;

        private LoopbackPtyServer(
            HttpListener listener,
            int port,
            Func<IRestartableTerminalConnection>? connectionFactory,
            Func<int, int, TerminalLaunchOptions>? launchOptions)
        {
            _listener = listener;
            _connectionFactory = connectionFactory;
            _launchOptions = launchOptions;
            HttpBase = new Uri($"http://127.0.0.1:{port}/");
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public Uri HttpBase { get; }

        public static LoopbackPtyServer Start(
            Func<IRestartableTerminalConnection>? connectionFactory,
            Func<int, int, TerminalLaunchOptions>? launchOptions)
        {
            var port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            return new LoopbackPtyServer(listener, port, connectionFactory, launchOptions);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Close();
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
                    context.Response.ContentLength64 = payload.Length;
                    await context.Response.OutputStream.WriteAsync(payload);
                    return;
                }

                if (!context.Request.IsWebSocketRequest ||
                    !path.Equals(HostPtyProtocol.SocketPath, StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 404;
                    return;
                }

                var columns = QueryInt(context.Request.Url, "cols", 80);
                var rows = QueryInt(context.Request.Url, "rows", 24);
                var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
                upgraded = true;
                try
                {
                    if (_connectionFactory is null || _launchOptions is null)
                    {
                        await HostPtyWebSocketSession.RunAsync(socket, columns, rows, _stop.Token);
                    }
                    else
                    {
                        await HostPtyWebSocketSession.RunAsync(
                            socket,
                            columns,
                            rows,
                            _connectionFactory,
                            _launchOptions,
                            _stop.Token);
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
                    catch (HttpListenerException)
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
                    split[0].Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(split[1], out var value))
                {
                    return value;
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
