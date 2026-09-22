using System.Net;
using System.Net.WebSockets;
using Devolutions.Terminal.Connection;

namespace Devolutions.Terminal.Browser.Host;

public sealed record HostPtyStaticServerOptions
{
    public required string WebRoot { get; init; }

    public int Port { get; init; } = 5235;

    public HostSessionBroker? Broker { get; init; }
}

public static class HostPtyStaticServer
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json",
        [".wasm"] = "application/wasm",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".ico"] = "image/x-icon",
        [".txt"] = "text/plain; charset=utf-8",
        [".map"] = "application/json",
        [".dat"] = "application/octet-stream",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
    };

    public static async Task RunAsync(HostPtyStaticServerOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Port must be between 1 and 65535.");
        }

        var webRoot = Path.GetFullPath(options.WebRoot);
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{options.Port}/");
        listener.Start();
        using var stop = cancellationToken.Register(() =>
        {
            try
            {
                listener.Stop();
            }
            catch (ObjectDisposedException)
            {
            }
        });

        Console.WriteLine($"page    http://127.0.0.1:{options.Port}/");
        Console.WriteLine($"control ws://127.0.0.1:{options.Port}{HostPtyProtocol.ControlPath}");
        Console.WriteLine($"shell   {HostPtyWebSocketSession.DefaultCommandLine()}");
        Console.WriteLine($"files   {webRoot}");

        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested || !listener.IsListening)
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(context, webRoot, options.Port, options.Broker), CancellationToken.None);
        }
    }

    private static async Task HandleAsync(
        HttpListenerContext context,
        string webRoot,
        int port,
        HostSessionBroker? broker)
    {
        var upgraded = false;
        try
        {
            var remote = context.Request.RemoteEndPoint?.Address;
            if (remote is null || !HostPtyProtocol.IsLoopback(remote))
            {
                context.Response.StatusCode = 403;
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (path.Equals(HostPtyProtocol.HealthPath, StringComparison.OrdinalIgnoreCase))
            {
                await WriteHealthAsync(context, broker is not null).ConfigureAwait(false);
                return;
            }

            if (broker is not null && path.Equals(HostPtyProtocol.ControlPath, StringComparison.OrdinalIgnoreCase))
            {
                upgraded = await AcceptControlAsync(context, port, broker).ConfigureAwait(false);
                return;
            }

            if (broker is not null && HostPtyProtocol.TryParseSessionId(path, out var sessionId))
            {
                upgraded = await AcceptSessionAsync(context, port, broker, sessionId).ConfigureAwait(false);
                return;
            }

            if (path.Equals(HostPtyProtocol.SocketPath, StringComparison.OrdinalIgnoreCase))
            {
                upgraded = await AcceptShellAsync(context, port).ConfigureAwait(false);
                return;
            }

            await ServeFileAsync(context, webRoot, path).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"request failed: {ex.Message}");
            try
            {
                context.Response.StatusCode = 500;
            }
            catch (HttpListenerException)
            {
            }
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

    private static async Task WriteHealthAsync(HttpListenerContext context, bool control)
    {
        var payload = "ok"u8.ToArray();
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.Headers[HostPtyProtocol.HostHeaderName] = HostPtyProtocol.HostHeaderValue;
        if (control)
        {
            context.Response.Headers[HostPtyProtocol.ControlHeaderName] = HostPtyProtocol.ControlHeaderValue;
        }

        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.ContentLength64 = payload.Length;
        await context.Response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
    }

    private static async Task<bool> AcceptControlAsync(HttpListenerContext context, int port, HostSessionBroker broker)
    {
        if (!context.Request.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return false;
        }

        if (!HostPtyProtocol.IsAllowedOrigin(context.Request.Headers["Origin"], port))
        {
            context.Response.StatusCode = 403;
            return false;
        }

        HttpListenerWebSocketContext socketContext;
        try
        {
            socketContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"control upgrade failed: {ex.Message}");
            return false;
        }

        var scheme = context.Request.Url?.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) == true
            ? "wss"
            : "ws";
        Console.WriteLine("control connected");
        try
        {
            await broker.RunControlAsync(socketContext.WebSocket, scheme).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"control failed: {ex.Message}");
        }
        finally
        {
            socketContext.WebSocket.Dispose();
            Console.WriteLine("control ended");
        }

        return true;
    }

    private static async Task<bool> AcceptSessionAsync(
        HttpListenerContext context,
        int port,
        HostSessionBroker broker,
        string sessionId)
    {
        if (!context.Request.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return false;
        }

        if (!HostPtyProtocol.IsAllowedOrigin(context.Request.Headers["Origin"], port))
        {
            context.Response.StatusCode = 403;
            return false;
        }

        HttpListenerWebSocketContext socketContext;
        try
        {
            socketContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"session upgrade failed: {ex.Message}");
            return false;
        }

        var columns = QueryInt(context.Request, "cols", 80);
        var rows = QueryInt(context.Request, "rows", 24);
        Console.WriteLine($"tab {sessionId} {columns}x{rows}");
        try
        {
            var attached = await broker.TryAttachAsync(sessionId, socketContext.WebSocket, columns, rows)
                .ConfigureAwait(false);
            if (!attached && socketContext.WebSocket.State == System.Net.WebSockets.WebSocketState.Open)
            {
                await socketContext.WebSocket.CloseAsync(
                    System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation,
                    "unknown-session",
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"tab {sessionId} failed: {ex.Message}");
        }
        finally
        {
            socketContext.WebSocket.Dispose();
            Console.WriteLine($"tab {sessionId} ended");
        }

        return true;
    }

    private static async Task<bool> AcceptShellAsync(HttpListenerContext context, int port)
    {
        if (!context.Request.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return false;
        }

        var origin = context.Request.Headers["Origin"];
        if (!HostPtyProtocol.IsAllowedOrigin(origin, port))
        {
            context.Response.StatusCode = 403;
            return false;
        }

        var columns = QueryInt(context.Request, "cols", 80);
        var rows = QueryInt(context.Request, "rows", 24);
        HttpListenerWebSocketContext socketContext;
        try
        {
            socketContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"websocket upgrade failed: {ex.Message}");
            return false;
        }

        Console.WriteLine($"session started {columns}x{rows}");
        try
        {
            await HostPtyWebSocketSession.RunAsync(socketContext.WebSocket, columns, rows).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"session failed: {ex.Message}");
        }
        finally
        {
            socketContext.WebSocket.Dispose();
            Console.WriteLine("session ended");
        }

        return true;
    }

    private static async Task ServeFileAsync(HttpListenerContext context, string webRoot, string path)
    {
        var relative = Uri.UnescapeDataString(path).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        if (relative.Length == 0)
        {
            relative = "index.html";
        }

        var full = Path.GetFullPath(Path.Combine(webRoot, relative));
        var rootPrefix = webRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(full, webRoot, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 404;
            return;
        }

        if (Directory.Exists(full))
        {
            full = Path.Combine(full, "index.html");
        }

        if (!File.Exists(full))
        {
            context.Response.StatusCode = 404;
            return;
        }

        context.Response.StatusCode = 200;
        context.Response.ContentType = ContentTypes.GetValueOrDefault(Path.GetExtension(full), "application/octet-stream");
        context.Response.ContentLength64 = new FileInfo(full).Length;
        await using var stream = File.OpenRead(full);
        await stream.CopyToAsync(context.Response.OutputStream).ConfigureAwait(false);
    }

    private static int QueryInt(HttpListenerRequest request, string name, int fallback)
    {
        var value = request.QueryString[name];
        return int.TryParse(value, out var parsed) && parsed is >= 1 and <= short.MaxValue
            ? parsed
            : fallback;
    }
}
