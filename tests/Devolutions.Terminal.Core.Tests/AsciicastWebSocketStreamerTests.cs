using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Core.Tests;

public sealed class AsciicastWebSocketStreamerTests
{
    [Fact]
    public async Task StreamsDvlsCompatibleV2JsonlAndPreservesAuthenticationQuery()
    {
        await using var server = RecordingWebSocketServer.Start();
        var recording = new AsciicastRecording(120, 40, format: AsciicastFormat.V2)
        {
            Timestamp = 1234567890,
        };
        await using var streamer = new AsciicastWebSocketStreamer(
            new Uri($"{server.WebSocketBase}jet/jrec/push/session?token=signed-token&fileType=raw"));

        await streamer.StartAsync(recording);
        streamer.Write(new AsciicastFrame(0.25, "one\nTwo\r\nThree\rFour"));
        await streamer.StopAsync();

        var request = await server.Request;
        Assert.Null(streamer.RequestedSubProtocol);
        Assert.Null(request.SubProtocol);
        Assert.Equal("signed-token", request.Query["token"]);
        Assert.Equal("asciicast", request.Query["fileType"]);
        Assert.DoesNotContain("fileType=raw", request.RawQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, request.Messages.Count);

        using var header = JsonDocument.Parse(request.Messages[0]);
        Assert.Equal(2, header.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(1234567890, header.RootElement.GetProperty("timestamp").GetInt64());
        Assert.Equal(120, header.RootElement.GetProperty("width").GetInt32());
        Assert.Equal(40, header.RootElement.GetProperty("height").GetInt32());
        Assert.EndsWith("\n", request.Messages[0], StringComparison.Ordinal);

        using var output = JsonDocument.Parse(request.Messages[1]);
        Assert.Equal(0.25, output.RootElement[0].GetDouble());
        Assert.Equal("o", output.RootElement[1].GetString());
        Assert.Equal("one\r\nTwo\r\nThree\rFour", output.RootElement[2].GetString());
        Assert.EndsWith("\n", request.Messages[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task SupportsAsciinemaV2SubprotocolWhenExplicitlyRequested()
    {
        await using var server = RecordingWebSocketServer.Start("v2.asciicast");
        await using var streamer = AsciicastWebSocketStreamer.CreateForAsciinema(
            server.WebSocketBase,
            AsciicastFormat.V2,
            appendAsciicastFileType: false);
        var recording = new AsciicastRecording(80, 24, format: AsciicastFormat.V2);

        await streamer.StartAsync(recording);
        await streamer.StopAsync();

        var request = await server.Request;
        Assert.Equal("v2.asciicast", streamer.RequestedSubProtocol);
        Assert.Equal("v2.asciicast", request.SubProtocol);
        Assert.Empty(request.RawQuery);
        Assert.Single(request.Messages);
        using var header = JsonDocument.Parse(request.Messages[0]);
        Assert.Equal(2, header.RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task SupportsAsciinemaV3SubprotocolWithRelativeEventTimestamps()
    {
        await using var server = RecordingWebSocketServer.Start("v3.asciicast");
        await using var streamer = AsciicastWebSocketStreamer.CreateForAsciinema(
            server.WebSocketBase,
            AsciicastFormat.V3);
        var recording = new AsciicastRecording(80, 24, format: AsciicastFormat.V3);

        await streamer.StartAsync(recording);
        streamer.Write(new AsciicastFrame(0.25, "one"));
        streamer.Write(new AsciicastFrame(0.75, "two"));
        await streamer.StopAsync();

        var request = await server.Request;
        Assert.Equal("v3.asciicast", request.SubProtocol);
        Assert.Empty(request.RawQuery);
        Assert.Equal(3, request.Messages.Count);

        using var header = JsonDocument.Parse(request.Messages[0]);
        Assert.Equal(3, header.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(80, header.RootElement.GetProperty("term").GetProperty("cols").GetInt32());
        Assert.Equal(24, header.RootElement.GetProperty("term").GetProperty("rows").GetInt32());

        using var firstOutput = JsonDocument.Parse(request.Messages[1]);
        Assert.Equal(0.25, firstOutput.RootElement[0].GetDouble());
        Assert.Equal("one", firstOutput.RootElement[2].GetString());

        using var secondOutput = JsonDocument.Parse(request.Messages[2]);
        Assert.Equal(0.5, secondOutput.RootElement[0].GetDouble());
        Assert.Equal("two", secondOutput.RootElement[2].GetString());
    }

    [Fact]
    public async Task RejectsRecordingThatDoesNotMatchRequestedSubprotocol()
    {
        await using var streamer = AsciicastWebSocketStreamer.CreateForAsciinema(
            new Uri("ws://127.0.0.1:1/"),
            AsciicastFormat.V3);
        var recording = new AsciicastRecording(80, 24, format: AsciicastFormat.V2);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => streamer.StartAsync(recording));

        Assert.Contains("v3.asciicast", exception.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingWebSocketServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly Task<RecordingRequest> _request;

        private RecordingWebSocketServer(
            HttpListener listener,
            Uri webSocketBase,
            string? acceptedSubProtocol)
        {
            _listener = listener;
            WebSocketBase = webSocketBase;
            _request = AcceptAsync(acceptedSubProtocol);
        }

        public Uri WebSocketBase { get; }
        public Task<RecordingRequest> Request => _request;

        public static RecordingWebSocketServer Start(string? acceptedSubProtocol = null)
        {
            var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            return new RecordingWebSocketServer(
                listener,
                new Uri($"ws://127.0.0.1:{port}/"),
                acceptedSubProtocol);
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Close();
            try
            {
                await _request.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception) when (!_request.IsCompletedSuccessfully)
            {
            }
        }

        private async Task<RecordingRequest> AcceptAsync(string? acceptedSubProtocol)
        {
            var context = await _listener.GetContextAsync();
            var messages = new List<string>();
            var socket = (await context.AcceptWebSocketAsync(acceptedSubProtocol)).WebSocket;
            try
            {
                var buffer = new byte[16 * 1024];
                while (true)
                {
                    var message = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await socket.CloseAsync(
                                WebSocketCloseStatus.NormalClosure,
                                "Recording received",
                                CancellationToken.None);
                            return new RecordingRequest(
                                context.Request.QueryString,
                                context.Request.Url?.Query ?? string.Empty,
                                socket.SubProtocol,
                                messages);
                        }

                        message.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    Assert.Equal(WebSocketMessageType.Text, result.MessageType);
                    messages.Add(Encoding.UTF8.GetString(message.ToArray()));
                }
            }
            finally
            {
                socket.Dispose();
            }
        }
    }

    private sealed record RecordingRequest(
        System.Collections.Specialized.NameValueCollection Query,
        string RawQuery,
        string? SubProtocol,
        IReadOnlyList<string> Messages);
}
