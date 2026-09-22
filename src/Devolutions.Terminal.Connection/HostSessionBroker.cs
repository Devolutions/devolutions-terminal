using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace Devolutions.Terminal.Connection;

/// <summary>
/// Owns profile launches for the loopback host. Session command lines come
/// from <paramref name="launchOptions"/>, never from a WebSocket message.
/// </summary>
public sealed class HostSessionBroker : IAsyncDisposable
{
    private readonly IReadOnlyList<HostProfileInfo> _profiles;
    private readonly Func<HostProfileInfo, int, int, TerminalLaunchOptions> _launchOptions;
    private readonly Func<IRestartableTerminalConnection>? _connectionFactory;
    private readonly ConcurrentDictionary<string, SessionSlot> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _defaultProfileId;

    public HostSessionBroker(
        IReadOnlyList<HostProfileInfo> profiles,
        Func<HostProfileInfo, int, int, TerminalLaunchOptions> launchOptions,
        Func<IRestartableTerminalConnection>? connectionFactory = null,
        string? defaultProfileId = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(launchOptions);
        _profiles = profiles;
        _launchOptions = launchOptions;
        _connectionFactory = connectionFactory;
        _defaultProfileId = defaultProfileId;
    }

    public IReadOnlyList<HostProfileInfo> Profiles => _profiles;

    public async Task RunControlAsync(
        WebSocket socket,
        string transportScheme,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if (transportScheme is not ("ws" or "wss"))
        {
            throw new ArgumentException("Transport scheme must be ws or wss.", nameof(transportScheme));
        }

        var sendLock = new SemaphoreSlim(1, 1);
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                HostControlMessage? request;
                try
                {
                    request = await ReadAsync(socket, cancellationToken).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    await SendAsync(
                        socket,
                        sendLock,
                        Error(null, "Control message is not JSON."),
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }
                catch (InvalidOperationException ex)
                {
                    await SendAsync(socket, sendLock, Error(null, ex.Message), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (request is null)
                {
                    break;
                }

                var response = Handle(request, transportScheme);
                await SendAsync(socket, sendLock, response, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            sendLock.Dispose();
        }
    }

    public async Task<bool> TryAttachAsync(
        string sessionId,
        WebSocket socket,
        int columns,
        int rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if (!_sessions.TryGetValue(sessionId, out var slot) || slot.IsClosed)
        {
            return false;
        }

        if (!slot.TryEnter())
        {
            return false;
        }

        try
        {
            columns = HostPtyProtocol.ValidateDimension(columns, nameof(columns));
            rows = HostPtyProtocol.ValidateDimension(rows, nameof(rows));
            var options = _launchOptions(slot.Profile, columns, rows);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, slot.Stop.Token);
            if (_connectionFactory is null)
            {
                await HostPtyWebSocketSession.RunAsync(
                    socket,
                    columns,
                    rows,
                    (_, _) => options,
                    linked.Token).ConfigureAwait(false);
            }
            else
            {
                var factory = _connectionFactory;
                await HostPtyWebSocketSession.RunAsync(
                    socket,
                    columns,
                    rows,
                    factory,
                    (_, _) => options,
                    linked.Token).ConfigureAwait(false);
            }

            return true;
        }
        finally
        {
            slot.Exit();
            _sessions.TryRemove(sessionId, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var slot in _sessions.Values)
        {
            slot.Close();
        }

        _sessions.Clear();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private HostControlMessage Handle(HostControlMessage request, string transportScheme)
    {
        switch (request.Type)
        {
            case HostControlProtocol.Hello:
            case HostControlProtocol.ListProfiles:
                return new HostControlMessage
                {
                    Type = HostControlProtocol.Profiles,
                    RequestId = request.RequestId,
                    Scheme = transportScheme,
                    DefaultProfileId = _defaultProfileId,
                    Profiles = _profiles.ToList(),
                };
            case HostControlProtocol.ListSessions:
                return new HostControlMessage
                {
                    Type = HostControlProtocol.Sessions,
                    RequestId = request.RequestId,
                    Scheme = transportScheme,
                    Sessions = _sessions.Values
                        .Where(slot => !slot.IsClosed)
                        .Select(slot => new HostSessionInfo
                        {
                            Id = slot.Id,
                            ProfileId = slot.Profile.Id,
                            Name = slot.Profile.Name,
                            Transport = HostPtyProtocol.CreateSessionPath(slot.Id),
                        })
                        .ToList(),
                };
            case HostControlProtocol.Launch:
                return Launch(request, transportScheme);
            case HostControlProtocol.Close:
                return Close(request);
            default:
                return Error(request.RequestId, "Unknown control message.");
        }
    }

    private HostControlMessage Launch(HostControlMessage request, string transportScheme)
    {
        SweepExpired();
        var profile = Find(request.ProfileId);
        if (profile is null || !profile.Launchable)
        {
            return Error(request.RequestId, "Unknown or non-launchable profile.");
        }

        if (_sessions.Count >= HostControlProtocol.MaxSessions)
        {
            return Error(request.RequestId, "Too many host sessions.");
        }

        var columns = request.Cols ?? 80;
        var rows = request.Rows ?? 24;
        try
        {
            columns = HostPtyProtocol.ValidateDimension(columns, nameof(columns));
            rows = HostPtyProtocol.ValidateDimension(rows, nameof(rows));
            _ = _launchOptions(profile, columns, rows);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Error(request.RequestId, "Profile cannot be launched.");
        }

        var id = Guid.NewGuid().ToString("N");
        var slot = new SessionSlot(id, profile);
        if (!_sessions.TryAdd(id, slot))
        {
            return Error(request.RequestId, "Could not create a session.");
        }

        return new HostControlMessage
        {
            Type = HostControlProtocol.Launched,
            RequestId = request.RequestId,
            SessionId = id,
            ProfileId = profile.Id,
            Name = profile.Name,
            Transport = HostPtyProtocol.CreateSessionPath(id),
            Scheme = transportScheme,
        };
    }

    private HostControlMessage Close(HostControlMessage request)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId) ||
            !_sessions.TryRemove(request.SessionId, out var slot))
        {
            return Error(request.RequestId, "Unknown session.");
        }

        slot.Close();
        return new HostControlMessage
        {
            Type = HostControlProtocol.Closed,
            RequestId = request.RequestId,
            SessionId = slot.Id,
        };
    }

    private HostProfileInfo? Find(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return null;
        }

        return _profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase));
    }

    private void SweepExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(60);
        foreach (var slot in _sessions.Values)
        {
            if (!slot.HasAttached && slot.Created < cutoff)
            {
                if (_sessions.TryRemove(slot.Id, out var removed))
                {
                    removed.Close();
                }
            }
        }
    }

    private static HostControlMessage Error(string? requestId, string message) =>
        new()
        {
            Type = HostControlProtocol.Error,
            RequestId = requestId,
            Message = message,
        };

    private static async Task SendAsync(
        WebSocket socket,
        SemaphoreSlim sendLock,
        HostControlMessage message,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, HostControlJsonContext.Default.HostControlMessage);
        await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static async Task<HostControlMessage?> ReadAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.Count > 0)
            {
                message.Write(buffer, 0, result.Count);
            }

            if (message.Length > HostControlProtocol.MaxMessageBytes)
            {
                throw new InvalidOperationException("Control message is too large.");
            }

            if (!result.EndOfMessage)
            {
                continue;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidOperationException("Control channel expects a text JSON message.");
            }

            if (message.Length == 0)
            {
                return null;
            }

            return JsonSerializer.Deserialize(
                message.GetBuffer().AsSpan(0, (int)message.Length),
                HostControlJsonContext.Default.HostControlMessage);
        }

        return null;
    }

    private sealed class SessionSlot
    {
        private int _state;

        public SessionSlot(string id, HostProfileInfo profile)
        {
            Id = id;
            Profile = profile;
            Created = DateTimeOffset.UtcNow;
        }

        public string Id { get; }
        public HostProfileInfo Profile { get; }
        public DateTimeOffset Created { get; }
        public CancellationTokenSource Stop { get; } = new();
        public bool HasAttached { get; private set; }
        public bool IsClosed => Volatile.Read(ref _state) == 2;

        public bool TryEnter()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            {
                return false;
            }

            HasAttached = true;
            return true;
        }

        public void Exit() => Interlocked.CompareExchange(ref _state, 0, 1);

        public void Close()
        {
            Interlocked.Exchange(ref _state, 2);
            try
            {
                Stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
