using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Devolutions.Terminal.Connection;
using Devolutions.Terminal.Core;
using Devolutions.Terminal.Settings;
using Xunit;

namespace Devolutions.Terminal.Control.Tests;

public sealed class TermControlOutputPumpTests
{
    [AvaloniaFact]
    public async Task OutputPumpKeepsFeedingWhenViewportListenersThrow()
    {
        var connection = new FakePtyConnection();
        var control = new TermControl();
        control.ConnectionFactory = _ => connection;
        control.ViewportChanged += (_, _) => throw new InvalidOperationException("scrollbar");

        try
        {
            await control.StartAsync(new ProfileSettings { Commandline = "cmd.exe" }, 80, 24);
            await Task.Run(() =>
            {
                connection.Emit("hello");
                connection.Emit(" world");
            });

            var line = string.Concat(
                control.Engine.CreateSnapshot().Buffer.Lines[0].Cells.Select(static cell => cell.Text));
            Assert.Contains("hello world", line, StringComparison.Ordinal);
        }
        finally
        {
            await control.CloseAsync();
        }
    }

    [AvaloniaFact]
    public async Task OutputBurstCoalescesIntoFewUiDrains()
    {
        var connection = new FakePtyConnection();
        var control = new TermControl();
        control.ConnectionFactory = _ => connection;

        try
        {
            await control.StartAsync(new ProfileSettings { Commandline = "cmd.exe" }, 80, 24);
            Dispatcher.UIThread.RunJobs();
            var postsBefore = control.InvalidationPosts;
            var drainsBefore = control.InvalidationDrains;

            // The UI thread must not pump the dispatcher while the burst arrives:
            // awaiting here would let the headless dispatcher interleave drains
            // (observed on the macOS CI runner). Spin on Join so every chunk's
            // invalidation queues before any drain can run — the production burst
            // shape (one frame, many 16 KiB ConPTY reads).
            var producer = new Thread(() =>
            {
                for (var index = 0; index < 64; index++)
                {
                    connection.Emit($"line {index:D4} filler filler filler filler\r\n");
                }
            });
            producer.Start();
            producer.Join();

            Dispatcher.UIThread.RunJobs();

            var posts = control.InvalidationPosts - postsBefore;
            var drains = control.InvalidationDrains - drainsBefore;
            Assert.True(posts >= 64, $"expected at least one post per chunk, got {posts}");
            Assert.True(drains <= 2, $"expected the burst to coalesce into <= 2 drains, got {drains}");

            var viewport = string.Concat(control.Engine.CreateSnapshot().Buffer.Lines
                .SelectMany(static line => line.Cells.Select(static cell => cell.Text)));
            Assert.Contains("line 0063", viewport, StringComparison.Ordinal);
        }
        finally
        {
            await control.CloseAsync();
        }
    }

    [AvaloniaFact]
    public async Task RecordingCapturesOutputAndRetainsItAfterStop()
    {
        var connection = new FakePtyConnection();
        var control = new TermControl();
        control.ConnectionFactory = _ => connection;

        try
        {
            await control.StartAsync(new ProfileSettings { Commandline = "cmd.exe" }, 80, 24);
            control.StartRecording();
            var output = Encoding.UTF8.GetBytes("hello 🙂");
            connection.Emit(output.AsMemory(0, 8));
            connection.Emit(output.AsMemory(8));

            Assert.True(control.IsRecording);
            Assert.Null(control.StopRecording());
            Assert.False(control.IsRecording);
            Assert.True(control.HasUnsavedRecording);

            var recording = control.GetRecording();
            Assert.Equal("hello 🙂", string.Concat(recording.Frames.Select(static frame => frame.Data)));
        }
        finally
        {
            await control.CloseAsync();
        }
    }

    [AvaloniaFact]
    public async Task RecordingIncludesVisiblePromptAtStart()
    {
        var connection = new FakePtyConnection();
        var control = new TermControl();
        control.ConnectionFactory = _ => connection;

        try
        {
            await control.StartAsync(new ProfileSettings { Commandline = "cmd.exe" }, 80, 24);
            connection.Emit("PowerShell 7.6.6\r\nPS C:\\Users\\mamoreau> ");
            control.StartRecording();
            connection.Emit("echo hi\r\n");

            var recording = control.GetRecording();
            Assert.Equal(2, recording.Frames.Count);
            Assert.Contains("PowerShell 7.6.6", recording.Frames[0].Data, StringComparison.Ordinal);
            Assert.Contains("echo hi", recording.Frames[1].Data, StringComparison.Ordinal);
        }
        finally
        {
            await control.CloseAsync();
        }
    }

    [AvaloniaFact]
    public async Task RecordingCanBeSavedAfterItStops()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.cast");
        var connection = new FakePtyConnection();
        var control = new TermControl();
        control.ConnectionFactory = _ => connection;

        try
        {
            await control.StartAsync(new ProfileSettings { Commandline = "cmd.exe" }, 80, 24);
            control.StartRecording();
            connection.Emit("saved output");
            control.StopRecording();

            Assert.Equal(path, control.SaveRecording(path));
            Assert.False(control.HasUnsavedRecording);
            var saved = Devolutions.Terminal.Core.AsciicastRecording.FromJson(
                await File.ReadAllTextAsync(path));
            Assert.Equal("saved output", Assert.Single(saved.Frames).Data);
        }
        finally
        {
            await control.CloseAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task RecordingBecomesUnsavedWhenOutputArrivesAfterSave()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.cast");
        var connection = new FakePtyConnection();
        var control = new TermControl();
        control.ConnectionFactory = _ => connection;

        try
        {
            await control.StartAsync(new ProfileSettings { Commandline = "cmd.exe" }, 80, 24);
            control.StartRecording();
            connection.Emit("first");
            control.SaveRecording(path);

            Assert.False(control.HasUnsavedRecording);

            connection.Emit(" second");

            Assert.True(control.HasUnsavedRecording);
            Assert.Equal(path, control.StopRecording());
            Assert.False(control.HasUnsavedRecording);
        }
        finally
        {
            await control.CloseAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task StreamingRecordingSendsInitialScreenAndLiveOutput()
    {
        await using var server = RecordingWebSocketServer.Start();
        var connection = new FakePtyConnection();
        var control = new TermControl();
        control.ConnectionFactory = _ => connection;

        try
        {
            await control.StartAsync(new ProfileSettings { Commandline = "cmd.exe" }, 80, 24);
            connection.Emit("PowerShell 7.6.6\r\nPS C:\\Users\\mamoreau> ");

            await control.StartRecordingAsync(
                new Uri($"{server.WebSocketBase}jet/jrec/push/session?token=abc"));
            Assert.True(control.IsStreamingRecording);
            connection.Emit("echo hi\n");
            await control.StopRecordingAsync();
            Assert.False(control.IsStreamingRecording);

            var messages = await server.Messages;
            Assert.Equal(3, messages.Count);
            using var initial = JsonDocument.Parse(messages[1]);
            using var output = JsonDocument.Parse(messages[2]);
            Assert.Contains(
                "PowerShell 7.6.6",
                initial.RootElement[2].GetString(),
                StringComparison.Ordinal);
            Assert.Equal("echo hi\r\n", output.RootElement[2].GetString());
            Assert.Null(control.RecordingStreamError);
        }
        finally
        {
            await control.CloseAsync();
        }
    }

    [AvaloniaFact]
    public async Task AsciinemaV3StreamingNegotiatesSubprotocolAndUsesV3Header()
    {
        await using var server = RecordingWebSocketServer.Start("v3.asciicast");
        var connection = new FakePtyConnection();
        var control = new TermControl
        {
            ConnectionFactory = _ => connection,
        };

        try
        {
            await control.StartAsync(new ProfileSettings { Commandline = "cmd.exe" }, 100, 30);

            await control.StartRecordingAsync(server.WebSocketBase, AsciicastFormat.V3);
            connection.Emit("echo streamed\n");
            await control.StopRecordingAsync();

            var messages = await server.Messages;
            Assert.True(messages.Count >= 2);
            using var header = JsonDocument.Parse(messages[0]);
            Assert.Equal(3, header.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(100, header.RootElement.GetProperty("term").GetProperty("cols").GetInt32());
            Assert.Equal(30, header.RootElement.GetProperty("term").GetProperty("rows").GetInt32());
            Assert.Null(control.RecordingStreamError);
        }
        finally
        {
            await control.CloseAsync();
        }
    }

    [AvaloniaFact]
    public async Task InvalidStreamingUriDoesNotLeaveRecordingStartPending()
    {
        var control = new TermControl();

        await Assert.ThrowsAsync<ArgumentException>(
            () => control.StartRecordingAsync(new Uri("https://gateway.example/record")));

        control.StartRecording();
        Assert.True(control.IsRecording);
        control.StopRecording();
        await control.CloseAsync();
    }

    [AvaloniaFact]
    public async Task ReplayExposesPauseSpeedAndProgressState()
    {
        var control = new TermControl();
        var recording = new Devolutions.Terminal.Core.AsciicastRecording(80, 24);
        recording.AppendFrame(5, "later");
        using var cancellation = new CancellationTokenSource();

        var replay = control.ReplayRecordingAsync(recording, cancellation.Token);
        control.PauseReplay();
        control.SetReplaySpeed(2);

        Assert.True(control.HasReplay);
        Assert.True(control.IsReplaying);
        Assert.True(control.IsReplayPaused);
        Assert.Equal(2, control.ReplaySpeed);
        Assert.Equal(TimeSpan.FromSeconds(5), control.ReplayDuration);

        control.ResumeReplay();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replay);

        Assert.False(control.IsReplaying);
        Assert.False(control.IsReplayPaused);
        Assert.True(control.HasReplay);
        await control.CloseAsync();
    }

    private sealed class FakePtyConnection : IRestartableTerminalConnection
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
        public TerminalProcessMetadata? ProcessMetadata => null;
        public TerminalExitInfo? LastExitInfo => null;

        public Task StartAsync(
            TerminalLaunchOptions options,
            CancellationToken cancellationToken = default)
        {
            Columns = options.Columns;
            Rows = options.Rows;
            IsRunning = true;
            State = TerminalConnectionState.Connected;
            return Task.CompletedTask;
        }

        public Task StartAsync(
            string commandLine,
            string? workingDirectory,
            int columns,
            int rows,
            CancellationToken cancellationToken = default) =>
            StartAsync(
                new TerminalLaunchOptions
                {
                    CommandLine = commandLine,
                    WorkingDirectory = workingDirectory,
                    Columns = columns,
                    Rows = rows,
                },
                cancellationToken);

        public void Write(ReadOnlySpan<byte> data)
        {
        }

        public void Write(string text)
        {
        }

        public ValueTask WriteAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public void Resize(int columns, int rows)
        {
            Columns = columns;
            Rows = rows;
        }

        public Task RestartAsync(
            TerminalLaunchOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            State = TerminalConnectionState.Closed;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Emit(string text) =>
            OutputReceived?.Invoke(this, Encoding.UTF8.GetBytes(text));

        public void Emit(ReadOnlyMemory<byte> data) =>
            OutputReceived?.Invoke(this, data);
    }

    private sealed class RecordingWebSocketServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly Task<IReadOnlyList<string>> _messages;

        private RecordingWebSocketServer(
            HttpListener listener,
            Uri webSocketBase,
            string? acceptedSubProtocol)
        {
            _listener = listener;
            WebSocketBase = webSocketBase;
            _messages = AcceptAsync(acceptedSubProtocol);
        }

        public Uri WebSocketBase { get; }
        public Task<IReadOnlyList<string>> Messages => _messages;

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
                await _messages.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception) when (!_messages.IsCompletedSuccessfully)
            {
            }
        }

        private async Task<IReadOnlyList<string>> AcceptAsync(string? acceptedSubProtocol)
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
                            return messages;
                        }

                        message.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    messages.Add(Encoding.UTF8.GetString(message.ToArray()));
                }
            }
            finally
            {
                socket.Dispose();
            }
        }
    }
}
