using System.Net.WebSockets;
using System.Threading.Channels;

namespace Devolutions.Terminal.Core;

/// <summary>
/// Streams an asciicast recording to a WebSocket push endpoint, optionally
/// negotiating an asciinema subprotocol such as <c>v2.asciicast</c> or
/// <c>v3.asciicast</c>.
/// </summary>
public sealed class AsciicastWebSocketStreamer : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly Channel<AsciicastFrame> _frames = Channel.CreateUnbounded<AsciicastFrame>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string? _requestedSubProtocol;
    private Task? _sendTask;
    private bool _started;
    private bool _stopped;
    private double _previousTimestamp;

    public AsciicastWebSocketStreamer(
        Uri streamingUri,
        string? subProtocol = null,
        bool appendAsciicastFileType = true)
    {
        ArgumentNullException.ThrowIfNull(streamingUri);
        if (!streamingUri.IsAbsoluteUri || streamingUri.Scheme is not ("ws" or "wss"))
        {
            throw new ArgumentException(
                "The asciicast streaming URI must be an absolute ws:// or wss:// URI.",
                nameof(streamingUri));
        }

        if (!string.IsNullOrWhiteSpace(subProtocol) &&
            !IsSupportedAsciinemaSubProtocol(subProtocol))
        {
            throw new ArgumentException(
                "The WebSocket sub-protocol must be a supported asciinema value such as 'v2.asciicast' or 'v3.asciicast'.",
                nameof(subProtocol));
        }

        _requestedSubProtocol = string.IsNullOrWhiteSpace(subProtocol)
            ? null
            : ToSubProtocol(FromSubProtocol(subProtocol));
        StreamingUri = appendAsciicastFileType ? AddAsciicastFileType(streamingUri) : streamingUri;
    }

    public static AsciicastWebSocketStreamer CreateForAsciinema(
        Uri streamingUri,
        AsciicastFormat format,
        bool appendAsciicastFileType = false)
    {
        ArgumentNullException.ThrowIfNull(streamingUri);
        var subProtocol = ToSubProtocol(format);
        var uri = appendAsciicastFileType ? AddAsciicastFileType(streamingUri) : streamingUri;
        return new AsciicastWebSocketStreamer(uri, subProtocol, appendAsciicastFileType: false);
    }

    public string? RequestedSubProtocol => _requestedSubProtocol;

    /// <summary>
    /// Gets the effective WebSocket URI. DVLS-compatible construction adds
    /// <c>fileType=asciicast</c> while preserving caller-provided authentication
    /// parameters; official asciinema construction leaves the URI unchanged.
    /// </summary>
    public Uri StreamingUri { get; }

    /// <summary>
    /// Gets the background send operation after <see cref="StartAsync"/>.
    /// </summary>
    public Task Completion => _sendTask ?? Task.CompletedTask;

    /// <summary>
    /// Connects and sends the asciicast JSONL header as the first text message.
    /// </summary>
    public async Task StartAsync(
        AsciicastRecording recording,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ObjectDisposedException.ThrowIf(_lifetime.IsCancellationRequested, this);
        if (_started)
        {
            throw new InvalidOperationException("This asciicast WebSocket stream has already been started.");
        }

        var format = ResolveFormat(recording.Format);
        if (_requestedSubProtocol is not null &&
            format != FromSubProtocol(_requestedSubProtocol))
        {
            throw new ArgumentException(
                $"The {_requestedSubProtocol} WebSocket subprotocol requires an asciicast {FromSubProtocol(_requestedSubProtocol)} recording.",
                nameof(recording));
        }

        _started = true;
        try
        {
            if (_requestedSubProtocol is not null)
            {
                _socket.Options.AddSubProtocol(_requestedSubProtocol);
            }

            await _socket.ConnectAsync(StreamingUri, cancellationToken).ConfigureAwait(false);
            var header = recording.CreateHeaderLine(format);
            await _socket.SendAsync(
                header,
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken).ConfigureAwait(false);
            _previousTimestamp = 0d;
            _sendTask = SendLoopAsync(_lifetime.Token, format);
        }
        catch
        {
            _started = false;
            throw;
        }
    }

    /// <summary>
    /// Queues one output event without blocking the terminal output thread.
    /// </summary>
    public void Write(AsciicastFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!_started || _stopped)
        {
            throw new InvalidOperationException("The asciicast WebSocket stream is not accepting output.");
        }

        if (_sendTask is { IsCompleted: true })
        {
            _sendTask.GetAwaiter().GetResult();
            throw new InvalidOperationException("The asciicast WebSocket stream has completed.");
        }

        if (!_frames.Writer.TryWrite(frame))
        {
            throw new InvalidOperationException("The asciicast WebSocket stream is no longer accepting output.");
        }
    }

    /// <summary>
    /// Flushes queued events and closes the WebSocket normally.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started || _stopped)
        {
            return;
        }

        _stopped = true;
        _frames.Writer.TryComplete();
        if (_sendTask is not null)
        {
            await _sendTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await _socket.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure,
                "Recording stopped",
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            _socket.Dispose();
            _lifetime.Dispose();
        }
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken, AsciicastFormat format)
    {
        try
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var data = NormalizeLineEndings(frame.Data);
                var timestamp = format == AsciicastFormat.V3
                    ? Math.Max(0d, frame.Timestamp - _previousTimestamp)
                    : frame.Timestamp;
                await _socket.SendAsync(
                    AsciicastRecording.CreateEventLine(timestamp, data, format),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken).ConfigureAwait(false);
                _previousTimestamp = frame.Timestamp;
            }
        }
        finally
        {
            _frames.Writer.TryComplete();
        }
    }

    private static string NormalizeLineEndings(string data) =>
        data.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);

    private static AsciicastFormat ResolveFormat(AsciicastFormat requested)
    {
        if (requested is AsciicastFormat.V2 or AsciicastFormat.V3)
        {
            return requested;
        }

        throw new ArgumentOutOfRangeException(nameof(requested), requested, "Only asciicast v2 and v3 are supported.");
    }

    private static string ToSubProtocol(AsciicastFormat format) => format switch
    {
        AsciicastFormat.V2 => "v2.asciicast",
        AsciicastFormat.V3 => "v3.asciicast",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Only asciicast v2 and v3 are supported."),
    };

    private static AsciicastFormat FromSubProtocol(string subProtocol) =>
        subProtocol.Equals("v2.asciicast", StringComparison.OrdinalIgnoreCase)
            ? AsciicastFormat.V2
            : subProtocol.Equals("v3.asciicast", StringComparison.OrdinalIgnoreCase)
                ? AsciicastFormat.V3
                : throw new ArgumentOutOfRangeException(
                    nameof(subProtocol),
                    subProtocol,
                    "Only the v2.asciicast and v3.asciicast WebSocket subprotocols are supported.");

    private static bool IsSupportedAsciinemaSubProtocol(string subProtocol) =>
        subProtocol.Equals("v2.asciicast", StringComparison.OrdinalIgnoreCase) ||
        subProtocol.Equals("v3.asciicast", StringComparison.OrdinalIgnoreCase);

    private static Uri AddAsciicastFileType(Uri uri)
    {
        var query = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(static part => !part.StartsWith("fileType=", StringComparison.OrdinalIgnoreCase))
            .Append("fileType=asciicast");
        return new UriBuilder(uri)
        {
            Query = string.Join('&', query),
            Fragment = string.Empty,
        }.Uri;
    }
}
