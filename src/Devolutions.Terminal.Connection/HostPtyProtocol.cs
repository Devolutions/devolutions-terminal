using System.Buffers.Binary;
using System.Net;

namespace Devolutions.Terminal.Connection;

/// <summary>
/// Binary frames for the loopback host PTY bridge. Each WebSocket message is
/// one frame: opcode, then payload. Direction is implied by who sends it.
/// </summary>
public static class HostPtyProtocol
{
    public const string HealthPath = "/pty/health";
    public const string SocketPath = "/pty";
    public const string ControlPath = "/dt";
    public const string SessionPathPrefix = "/pty/";
    public const string HostHeaderName = "X-Dterm-Host";
    public const string HostHeaderValue = "pty";
    public const string ControlHeaderName = "X-Dterm-Control";
    public const string ControlHeaderValue = "/dt";

    public const byte Data = 0x01;
    public const byte Resize = 0x02;
    public const byte Exit = 0x04;

    public const int MaxPayload = 32 * 1024;

    public static Uri CreateWebSocketUri(Uri httpBase, int columns, int rows)
    {
        ArgumentNullException.ThrowIfNull(httpBase);
        ValidateDimension(columns, nameof(columns));
        ValidateDimension(rows, nameof(rows));
        return new UriBuilder(httpBase)
        {
            Scheme = WebSocketScheme(httpBase),
            Path = SocketPath,
            Query = $"cols={columns}&rows={rows}",
        }.Uri;
    }

    public static Uri CreateHealthUri(Uri httpBase)
    {
        ArgumentNullException.ThrowIfNull(httpBase);
        return new UriBuilder(httpBase)
        {
            Path = HealthPath,
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri;
    }

    public static Uri CreateControlUri(Uri httpBase)
    {
        ArgumentNullException.ThrowIfNull(httpBase);
        return new UriBuilder(httpBase)
        {
            Scheme = WebSocketScheme(httpBase),
            Path = ControlPath,
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri;
    }

    /// <summary>
    /// Dedicated per-tab transport. Uses <c>wss</c> when <paramref name="httpBase"/>
    /// is HTTPS and <c>ws</c> otherwise. The path must be <c>/pty/{id}</c>.
    /// </summary>
    public static Uri CreateSessionWebSocketUri(Uri httpBase, string sessionPath, int columns, int rows)
    {
        ArgumentNullException.ThrowIfNull(httpBase);
        if (!TryParseSessionId(sessionPath, out var sessionId))
        {
            throw new ArgumentException("Session transport must be a relative /pty/{id} path.", nameof(sessionPath));
        }

        ValidateDimension(columns, nameof(columns));
        ValidateDimension(rows, nameof(rows));
        return new UriBuilder(httpBase)
        {
            Scheme = WebSocketScheme(httpBase),
            Path = CreateSessionPath(sessionId),
            Query = $"cols={columns}&rows={rows}",
        }.Uri;
    }

    public static string CreateSessionPath(string sessionId)
    {
        if (!TryParseSessionId(SessionPathPrefix + sessionId, out var parsed))
        {
            throw new ArgumentException("Session id must be 32 hexadecimal characters.", nameof(sessionId));
        }

        return SessionPathPrefix + parsed;
    }

    public static bool TryParseSessionId(string? path, out string sessionId)
    {
        sessionId = "";
        if (string.IsNullOrEmpty(path) ||
            path.Contains("://", StringComparison.Ordinal) ||
            path.Contains('\\') ||
            path.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        var slash = path.IndexOf('/', 1);
        if (slash < 0 ||
            !path.AsSpan(0, slash + 1).Equals(SessionPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var id = path.AsSpan(slash + 1);
        if (id.Length != 32)
        {
            return false;
        }

        foreach (var ch in id)
        {
            if (ch is not ((>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F')))
            {
                return false;
            }
        }

        sessionId = id.ToString().ToLowerInvariant();
        return true;
    }

    private static string WebSocketScheme(Uri httpBase) =>
        httpBase.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";

    public static byte[] EncodeData(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayload)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), payload.Length, "Split host PTY data before encoding.");
        }

        var frame = new byte[payload.Length + 1];
        frame[0] = Data;
        payload.CopyTo(frame.AsSpan(1));
        return frame;
    }

    public static List<byte[]> SplitData(ReadOnlySpan<byte> payload)
    {
        var frames = new List<byte[]>(Math.Max(1, (payload.Length + MaxPayload - 1) / MaxPayload));
        var offset = 0;
        while (offset < payload.Length)
        {
            var count = Math.Min(MaxPayload, payload.Length - offset);
            frames.Add(EncodeData(payload.Slice(offset, count)));
            offset += count;
        }

        return frames;
    }

    public static byte[] EncodeResize(int columns, int rows)
    {
        ValidateDimension(columns, nameof(columns));
        ValidateDimension(rows, nameof(rows));
        var frame = new byte[5];
        frame[0] = Resize;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), (ushort)columns);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(3), (ushort)rows);
        return frame;
    }

    public static byte[] EncodeExit(int exitCode)
    {
        var frame = new byte[5];
        frame[0] = Exit;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1), exitCode);
        return frame;
    }

    public static bool TryReadResize(ReadOnlySpan<byte> message, out int columns, out int rows)
    {
        columns = 0;
        rows = 0;
        if (message.Length != 5 || message[0] != Resize)
        {
            return false;
        }

        columns = BinaryPrimitives.ReadUInt16LittleEndian(message.Slice(1));
        rows = BinaryPrimitives.ReadUInt16LittleEndian(message.Slice(3));
        return columns is >= 1 and <= short.MaxValue && rows is >= 1 and <= short.MaxValue;
    }

    public static bool TryReadExit(ReadOnlySpan<byte> message, out int exitCode)
    {
        exitCode = 0;
        if (message.Length != 5 || message[0] != Exit)
        {
            return false;
        }

        exitCode = BinaryPrimitives.ReadInt32LittleEndian(message.Slice(1));
        return true;
    }

    public static bool IsLoopback(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        return address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address.MapToIPv4());
    }

    public static bool IsAllowedOrigin(string? origin, int port)
    {
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Port != port || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        return uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("[::1]", StringComparison.OrdinalIgnoreCase) ||
            uri.IdnHost.Equals("::1", StringComparison.OrdinalIgnoreCase);
    }

    public static int ValidateDimension(int value, string parameterName)
    {
        if (value is < 1 or > short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Host PTY dimensions must be between 1 and {short.MaxValue}.");
        }

        return value;
    }
}
