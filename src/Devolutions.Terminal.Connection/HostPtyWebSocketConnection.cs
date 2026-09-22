using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace Devolutions.Terminal.Connection;

/// <summary>
/// Browser-side terminal connection. Bytes go to a host process that owns the
/// real PTY. Falls back is the caller's job when <see cref="IsBridgeAvailableAsync"/>
/// is false.
/// </summary>
public sealed class HostPtyWebSocketConnection : IRestartableTerminalConnection
{
    public const string ShellName = "host-pty";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Uri _httpBase;
    private readonly string? _sessionPath;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private ClientWebSocket? _socket;
    private Channel<byte[]>? _outbound;
    private CancellationTokenSource? _lifetime;
    private Task? _sendTask;
    private Task? _receiveTask;
    private TerminalLaunchOptions? _lastOptions;
    private bool _hasStarted;
    private bool _disposed;
    private bool _exitPublished;

    public HostPtyWebSocketConnection(Uri httpBase, string? sessionPath = null)
    {
        ArgumentNullException.ThrowIfNull(httpBase);
        if (!httpBase.IsAbsoluteUri || httpBase.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Host PTY base URI must be an absolute http(s) URI.", nameof(httpBase));
        }

        if (sessionPath is not null && !HostPtyProtocol.TryParseSessionId(sessionPath, out _))
        {
            throw new ArgumentException("Session transport must be a relative /pty/{id} path.", nameof(sessionPath));
        }

        _httpBase = httpBase;
        _sessionPath = sessionPath;
    }

    public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
    public event EventHandler<int>? Exited;
    public event EventHandler<TerminalExitInfo>? SessionExited;
    public event EventHandler<Exception>? Faulted;

    public bool IsRunning { get; private set; }
    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public TerminalConnectionCapabilities Capabilities { get; } =
        TerminalConnectionCapabilities.Resize | TerminalConnectionCapabilities.Restart;
    public TerminalConnectionState State { get; private set; } = TerminalConnectionState.NotConnected;
    public TerminalProcessMetadata? ProcessMetadata { get; private set; }
    public TerminalExitInfo? LastExitInfo { get; private set; }

    public static async Task<bool> IsBridgeAvailableAsync(
        Uri httpBase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpBase);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var client = new HttpClient();
        try
        {
            using var response = await client.GetAsync(HostPtyProtocol.CreateHealthUri(httpBase), linked.Token)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode &&
                response.Headers.TryGetValues(HostPtyProtocol.HostHeaderName, out var values) &&
                values.Contains(HostPtyProtocol.HostHeaderValue, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is HttpRequestException or NotSupportedException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
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

    public async Task StartAsync(TerminalLaunchOptions options, CancellationToken cancellationToken = default)
    {
        ValidateOptions(options, cancellationToken);
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_hasStarted)
                {
                    throw new InvalidOperationException(
                        "The host PTY connection has already been started. Use RestartAsync to replace its session.");
                }
            }

            await ConnectCoreAsync(options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task RestartAsync(
        TerminalLaunchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var restartOptions = options ?? _lastOptions
                ?? throw new InvalidOperationException("No previous host PTY launch options are available.");
            ValidateOptions(restartOptions, cancellationToken);
            await StopCoreAsync(TerminalExitReason.Closed).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await ConnectCoreAsync(restartOptions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopCoreAsync(TerminalExitReason.Closed).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        ChannelWriter<byte[]> writer;
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsRunning || _outbound is null)
            {
                throw new InvalidOperationException("The host PTY connection is not running.");
            }

            writer = _outbound.Writer;
        }

        foreach (var frame in HostPtyProtocol.SplitData(data))
        {
            if (!writer.TryWrite(frame))
            {
                writer.WriteAsync(frame).AsTask().GetAwaiter().GetResult();
            }
        }
    }

    public void Write(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 0)
        {
            Write(Utf8.GetBytes(text));
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (data.IsEmpty)
        {
            return;
        }

        ChannelWriter<byte[]> writer;
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsRunning || _outbound is null)
            {
                throw new InvalidOperationException("The host PTY connection is not running.");
            }

            writer = _outbound.Writer;
        }

        foreach (var frame in HostPtyProtocol.SplitData(data.Span))
        {
            await writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Resize(int columns, int rows)
    {
        columns = HostPtyProtocol.ValidateDimension(columns, nameof(columns));
        rows = HostPtyProtocol.ValidateDimension(rows, nameof(rows));
        ChannelWriter<byte[]>? writer = null;
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Columns = columns;
            Rows = rows;
            if (IsRunning && _outbound is not null)
            {
                writer = _outbound.Writer;
            }
        }

        writer?.TryWrite(HostPtyProtocol.EncodeResize(columns, rows));
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            await StopCoreAsync(TerminalExitReason.Disposed).ConfigureAwait(false);
            lock (_stateLock)
            {
                State = TerminalConnectionState.Disposed;
            }
        }
        finally
        {
            _lifecycleLock.Release();
            _lifecycleLock.Dispose();
        }
    }

    private async Task ConnectCoreAsync(TerminalLaunchOptions options, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        var outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_stateLock)
        {
            State = TerminalConnectionState.Connecting;
            LastExitInfo = null;
            _exitPublished = false;
            Columns = options.Columns;
            Rows = options.Rows;
            _socket = socket;
            _outbound = outbound;
            _lifetime = lifetime;
            _lastOptions = options;
            ProcessMetadata = new TerminalProcessMetadata(
                Guid.NewGuid(),
                0,
                ShellName,
                options.WorkingDirectory ?? _httpBase.Host,
                DateTimeOffset.UtcNow);
        }

        try
        {
            var uri = _sessionPath is null
                ? HostPtyProtocol.CreateWebSocketUri(_httpBase, options.Columns, options.Rows)
                : HostPtyProtocol.CreateSessionWebSocketUri(_httpBase, _sessionPath, options.Columns, options.Rows);
            await socket.ConnectAsync(uri, lifetime.Token).ConfigureAwait(false);
            var sendTask = SendLoopAsync(socket, outbound.Reader, lifetime.Token);
            var receiveTask = ReceiveLoopAsync(socket, lifetime.Token);
            lock (_stateLock)
            {
                _sendTask = sendTask;
                _receiveTask = receiveTask;
                _hasStarted = true;
                IsRunning = true;
                State = TerminalConnectionState.Connected;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            outbound.Writer.TryComplete();
            socket.Dispose();
            lock (_stateLock)
            {
                IsRunning = false;
                State = TerminalConnectionState.Failed;
                _socket = null;
                _outbound = null;
            }

            await lifetime.CancelAsync().ConfigureAwait(false);
            lifetime.Dispose();
            Faulted?.Invoke(this, ex);
            throw;
        }
    }

    private async Task StopCoreAsync(TerminalExitReason reason)
    {
        ClientWebSocket? socket;
        Channel<byte[]>? outbound;
        CancellationTokenSource? lifetime;
        Task? sendTask;
        Task? receiveTask;
        lock (_stateLock)
        {
            if (!IsRunning && State is not TerminalConnectionState.Connecting and not TerminalConnectionState.Connected)
            {
                return;
            }

            IsRunning = false;
            State = TerminalConnectionState.Closing;
            socket = _socket;
            outbound = _outbound;
            lifetime = _lifetime;
            sendTask = _sendTask;
            receiveTask = _receiveTask;
            _socket = null;
            _outbound = null;
            _lifetime = null;
            _sendTask = null;
            _receiveTask = null;
        }

        outbound?.Writer.TryComplete();
        if (lifetime is not null)
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
        }

        if (socket is { State: WebSocketState.Open })
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                socket.Abort();
            }
        }

        socket?.Dispose();
        await WaitQuietlyAsync(sendTask).ConfigureAwait(false);
        await WaitQuietlyAsync(receiveTask).ConfigureAwait(false);
        lifetime?.Dispose();
        PublishExit(reason, exitCode: null);
    }

    private async Task SendLoopAsync(
        ClientWebSocket socket,
        ChannelReader<byte[]> outbound,
        CancellationToken cancellationToken)
    {
        try
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fault(ex);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[HostPtyProtocol.MaxPayload + 16];
        using var message = new MemoryStream();
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    PublishExit(TerminalExitReason.ConnectionFailure, exitCode: null);
                    return;
                }

                if (result.Count > 0)
                {
                    message.Write(buffer, 0, result.Count);
                }

                if (!result.EndOfMessage)
                {
                    continue;
                }

                var frame = message.ToArray();
                message.SetLength(0);
                if (frame.Length == 0)
                {
                    continue;
                }

                if (frame[0] == HostPtyProtocol.Data && frame.Length > 1)
                {
                    OutputReceived?.Invoke(this, frame.AsMemory(1));
                }
                else if (HostPtyProtocol.TryReadExit(frame, out var exitCode))
                {
                    PublishExit(TerminalExitReason.ProcessExited, exitCode);
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fault(ex);
            PublishExit(TerminalExitReason.ConnectionFailure, exitCode: null);
        }
    }

    private void PublishExit(TerminalExitReason reason, int? exitCode)
    {
        TerminalExitInfo info;
        lock (_stateLock)
        {
            if (_exitPublished || _disposed && reason != TerminalExitReason.Disposed)
            {
                return;
            }

            _exitPublished = true;
            IsRunning = false;
            if (State != TerminalConnectionState.Disposed)
            {
                State = reason == TerminalExitReason.Disposed
                    ? TerminalConnectionState.Disposed
                    : TerminalConnectionState.Closed;
            }

            info = new TerminalExitInfo(
                ProcessMetadata,
                exitCode,
                reason,
                false,
                DateTimeOffset.UtcNow);
            LastExitInfo = info;
        }

        try
        {
            SessionExited?.Invoke(this, info);
            Exited?.Invoke(this, exitCode ?? -1);
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(this, ex);
        }
    }

    private void Fault(Exception exception) => Faulted?.Invoke(this, exception);

    private static async Task WaitQuietlyAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
    }

    private static void ValidateOptions(TerminalLaunchOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        _ = HostPtyProtocol.ValidateDimension(options.Columns, nameof(options.Columns));
        _ = HostPtyProtocol.ValidateDimension(options.Rows, nameof(options.Rows));
    }
}
