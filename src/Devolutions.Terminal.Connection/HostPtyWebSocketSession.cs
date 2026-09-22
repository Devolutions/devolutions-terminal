using System.Net.WebSockets;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace Devolutions.Terminal.Connection;

/// <summary>
/// Bridges an accepted WebSocket to a local ConPTY or Unix PTY. The browser
/// never chooses the command line.
/// </summary>
public static class HostPtyWebSocketSession
{
    public static Task RunAsync(
        WebSocket socket,
        int columns,
        int rows,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            socket,
            columns,
            rows,
            CreatePlatformConnection,
            CreateDefaultLaunchOptions,
            cancellationToken);

    public static Task RunAsync(
        WebSocket socket,
        int columns,
        int rows,
        Func<int, int, TerminalLaunchOptions> launchOptions,
        CancellationToken cancellationToken = default) =>
        RunAsync(socket, columns, rows, CreatePlatformConnection, launchOptions, cancellationToken);

    public static async Task RunAsync(
        WebSocket socket,
        int columns,
        int rows,
        Func<IRestartableTerminalConnection> connectionFactory,
        Func<int, int, TerminalLaunchOptions> launchOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(launchOptions);
        columns = HostPtyProtocol.ValidateDimension(columns, nameof(columns));
        rows = HostPtyProtocol.ValidateDimension(rows, nameof(rows));

        await using var shell = connectionFactory();
        var outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sendTask = SendLoopAsync(socket, outbound.Reader, lifetime.Token);

        void OnOutput(object? sender, ReadOnlyMemory<byte> data)
        {
            try
            {
                foreach (var frame in HostPtyProtocol.SplitData(data.Span))
                {
                    Queue(outbound.Writer, frame);
                }
            }
            catch (ChannelClosedException)
            {
            }
        }

        void OnExit(object? sender, TerminalExitInfo info)
        {
            try
            {
                Queue(outbound.Writer, HostPtyProtocol.EncodeExit(info.ExitCode ?? -1));
                outbound.Writer.TryComplete();
            }
            catch (ChannelClosedException)
            {
            }
        }

        shell.OutputReceived += OnOutput;
        shell.SessionExited += OnExit;
        try
        {
            await shell.StartAsync(launchOptions(columns, rows), lifetime.Token).ConfigureAwait(false);
            await ReceiveLoopAsync(socket, shell, lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            shell.OutputReceived -= OnOutput;
            shell.SessionExited -= OnExit;
            outbound.Writer.TryComplete();
            await lifetime.CancelAsync().ConfigureAwait(false);
            try
            {
                await sendTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "session-ended",
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                }
            }
        }
    }

    public static TerminalLaunchOptions CreateDefaultLaunchOptions(int columns, int rows) =>
        new()
        {
            CommandLine = DefaultCommandLine(),
            WorkingDirectory = DefaultWorkingDirectory(),
            Columns = columns,
            Rows = rows,
            InheritEnvironment = true,
            IsDefaultTerminalSession = true,
        };

    public static string DefaultCommandLine()
    {
        if (OperatingSystem.IsWindows())
        {
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var powershell = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(powershell))
            {
                return powershell;
            }

            var cmd = Path.Combine(system, "cmd.exe");
            return File.Exists(cmd) ? cmd : "cmd.exe";
        }

        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (string.IsNullOrWhiteSpace(shell))
        {
            shell = OperatingSystem.IsMacOS() ? "/bin/zsh" : "/bin/bash";
        }

        return shell.Contains(' ', StringComparison.Ordinal) ? shell : shell + " -l";
    }

    public static string DefaultWorkingDirectory()
    {
        var current = Environment.CurrentDirectory;
        return Directory.Exists(current)
            ? current
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static async Task SendLoopAsync(
        WebSocket socket,
        ChannelReader<byte[]> outbound,
        CancellationToken cancellationToken)
    {
        await foreach (var frame in outbound.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (socket.State != WebSocketState.Open)
            {
                break;
            }

            await socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task ReceiveLoopAsync(
        WebSocket socket,
        IRestartableTerminalConnection shell,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[HostPtyProtocol.MaxPayload + 16];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            ValueWebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (WebSocketException)
            {
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            if (result.Count > 0)
            {
                message.Write(buffer, 0, result.Count);
            }

            if (!result.EndOfMessage)
            {
                if (message.Length > HostPtyProtocol.MaxPayload + 16)
                {
                    break;
                }

                continue;
            }

            try
            {
                Dispatch(message, shell);
            }
            catch (InvalidOperationException)
            {
            }

            message.SetLength(0);
        }
    }

    private static void Dispatch(MemoryStream message, IRestartableTerminalConnection shell)
    {
        var frame = message.GetBuffer().AsSpan(0, (int)message.Length);
        if (frame.IsEmpty)
        {
            return;
        }

        switch (frame[0])
        {
            case HostPtyProtocol.Data when frame.Length > 1:
                shell.Write(frame.Slice(1));
                break;
            case HostPtyProtocol.Resize when HostPtyProtocol.TryReadResize(frame, out var columns, out var rows):
                shell.Resize(columns, rows);
                break;
        }
    }

    private static void Queue(ChannelWriter<byte[]> writer, byte[] frame)
    {
        if (writer.TryWrite(frame))
        {
            return;
        }

        writer.WriteAsync(frame).AsTask().GetAwaiter().GetResult();
    }

    private static IRestartableTerminalConnection CreatePlatformConnection()
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateWindowsConnection();
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return CreateUnixConnection();
        }

        throw new PlatformNotSupportedException("Host PTY requires Windows, Linux, or macOS.");
    }

    [SupportedOSPlatform("windows")]
    private static ConPtyConnection CreateWindowsConnection() => new();

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static LinuxPtyConnection CreateUnixConnection() => new();
}
