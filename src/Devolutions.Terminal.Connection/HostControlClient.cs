using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace Devolutions.Terminal.Connection;

public sealed class HostControlException : InvalidOperationException
{
    public HostControlException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Browser-side client for the loopback <c>/dt</c> control channel.
/// </summary>
public sealed class HostControlClient : IAsyncDisposable
{
    private readonly Uri _httpBase;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<HostControlMessage>> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _lifetime;
    private Task? _readTask;

    public HostControlClient(Uri httpBase)
    {
        ArgumentNullException.ThrowIfNull(httpBase);
        if (!httpBase.IsAbsoluteUri || httpBase.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Host control base URI must be an absolute http(s) URI.", nameof(httpBase));
        }

        _httpBase = httpBase;
    }

    public string TransportScheme =>
        _httpBase.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";

    public static async Task<bool> IsAvailableAsync(Uri httpBase, CancellationToken cancellationToken = default)
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
                response.Headers.TryGetValues(HostPtyProtocol.ControlHeaderName, out var values) &&
                values.Contains(HostPtyProtocol.ControlHeaderValue, StringComparer.Ordinal);
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

    public static async Task<HostControlClient> ConnectAsync(Uri httpBase, CancellationToken cancellationToken = default)
    {
        var client = new HostControlClient(httpBase);
        await client.ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
        return client;
    }

    public async Task<HostControlMessage> ListProfilesAsync(CancellationToken cancellationToken = default) =>
        await RequestAsync(
            new HostControlMessage { Type = HostControlProtocol.ListProfiles },
            HostControlProtocol.Profiles,
            cancellationToken).ConfigureAwait(false);

    public async Task<HostControlMessage> LaunchAsync(
        string profileId,
        int columns,
        int rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        var response = await RequestAsync(
            new HostControlMessage
            {
                Type = HostControlProtocol.Launch,
                ProfileId = profileId,
                Cols = columns,
                Rows = rows,
            },
            HostControlProtocol.Launched,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(response.SessionId) ||
            !HostControlProtocol.IsSessionTransport(response.Transport, response.SessionId))
        {
            throw new HostControlException("Host returned a session transport that is not a dedicated /pty/{id} path.");
        }

        return response;
    }

    public Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return RequestAsync(
            new HostControlMessage
            {
                Type = HostControlProtocol.Close,
                SessionId = sessionId,
            },
            HostControlProtocol.Closed,
            cancellationToken);
    }

    public Uri CreateSessionUri(string transport, int columns, int rows) =>
        HostPtyProtocol.CreateSessionWebSocketUri(_httpBase, transport, columns, rows);

    public async ValueTask DisposeAsync()
    {
        await CloseSocketAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await socket.ConnectAsync(HostPtyProtocol.CreateControlUri(_httpBase), lifetime.Token).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            lifetime.Dispose();
            throw;
        }

        _socket = socket;
        _lifetime = lifetime;
        _readTask = Task.Run(() => ReadLoopAsync(socket, lifetime.Token), CancellationToken.None);
    }

    private async Task<HostControlMessage> RequestAsync(
        HostControlMessage message,
        string expectedType,
        CancellationToken cancellationToken)
    {
        var socket = _socket ?? throw new InvalidOperationException("Host control channel is not connected.");
        var requestId = Guid.NewGuid().ToString("N");
        var pending = new TaskCompletionSource<HostControlMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, pending))
        {
            throw new InvalidOperationException("Could not correlate a control request.");
        }

        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                message with { RequestId = requestId },
                HostControlJsonContext.Default.HostControlMessage);
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var response = await pending.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            if (response.Type == HostControlProtocol.Error)
            {
                throw new HostControlException(response.Message ?? "Host control request failed.");
            }

            if (!string.Equals(response.Type, expectedType, StringComparison.Ordinal))
            {
                throw new HostControlException($"Expected {expectedType} but the host sent {response.Type}.");
            }

            return response;
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private async Task ReadLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
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
                    continue;
                }

                if (message.Length > 0 && result.MessageType == WebSocketMessageType.Text)
                {
                    Dispatch(message.GetBuffer().AsSpan(0, (int)message.Length));
                }

                message.SetLength(0);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
        }
        finally
        {
            var failure = new HostControlException("Host control channel closed.");
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(failure);
            }
        }
    }

    private void Dispatch(ReadOnlySpan<byte> payload)
    {
        HostControlMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(payload, HostControlJsonContext.Default.HostControlMessage);
        }
        catch (JsonException)
        {
            return;
        }

        if (message?.RequestId is null || !_pending.TryRemove(message.RequestId, out var pending))
        {
            return;
        }

        pending.TrySetResult(message);
    }

    private async Task CloseSocketAsync()
    {
        var socket = _socket;
        var lifetime = _lifetime;
        _socket = null;
        if (lifetime is not null)
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
        }

        if (socket is not null && socket.State == WebSocketState.Open)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
            }
        }

        socket?.Dispose();
        if (_readTask is not null)
        {
            try
            {
                await _readTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
            }
        }

        lifetime?.Dispose();
    }
}
