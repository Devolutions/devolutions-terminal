using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Iseberg.Core;
using Xunit;

namespace Devolutions.Terminal.App.Tests;

public sealed class PortableIseProtocolTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(97)]
    public async Task FragmentedHeaderAndUnicodeBodyPreserveConsecutiveRequests(int fragmentSize)
    {
        var (server, client) = await ConnectAsync();
        await using var receiver = new BridgeConnection(new FragmentedStream(server, fragmentSize));
        await using var sender = new BridgeConnection(new FragmentedStream(client, fragmentSize));
        receiver.Request = (operation, payload, _) =>
        {
            Assert.Equal("echo", operation);
            return Task.FromResult(payload.Clone());
        };
        receiver.Start();
        sender.Start();
        foreach (var text in new[] { "first café 😀\n'quoted' " + new string('x', 257), "second مستقل" })
        {
            var result = await sender.CallAsync("echo", BridgeJson.Element(text)).WaitAsync(Deadline);
            Assert.Equal(text, result.GetString());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(8 * 1024 * 1024 + 1)]
    public async Task InvalidFrameLengthDisconnectsOnceAndFaultsPendingCall(int length)
    {
        var (server, client) = await ConnectAsync();
        await using var peer = server;
        await using var connection = new BridgeConnection(client);
        var disconnected = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        connection.Disconnected += error =>
        {
            Interlocked.Increment(ref count);
            disconnected.TrySetResult(error);
        };
        connection.Start();
        var pending = connection.CallAsync("pending", BridgeJson.Element("never acknowledged"));
        await ReadFrameAsync(peer);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        await peer.WriteAsync(header);
        Assert.IsType<InvalidDataException>(await disconnected.Task.WaitAsync(Deadline));
        await Assert.ThrowsAsync<InvalidDataException>(() => pending.WaitAsync(Deadline));
        await connection.Completion.WaitAsync(Deadline);
        await connection.DisposeAsync();
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public async Task CoalescedFramesRemainSeparateAndRetainTheirRequestIds()
    {
        var (server, client) = await ConnectAsync();
        await using var peer = client;
        await using var connection = new BridgeConnection(server);
        connection.Request = (operation, payload, _) =>
        {
            Assert.Equal("echo", operation);
            return Task.FromResult(payload.Clone());
        };
        connection.Start();
        static byte[] Frame(long id, string text)
        {
            var body = Encoding.UTF8.GetBytes(BridgeJson.Element(
                new BridgeEnvelope("request", id, "echo", BridgeJson.Element(text))).GetRawText());
            var frame = new byte[body.Length + 4];
            BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
            body.CopyTo(frame, 4);
            return frame;
        }
        await peer.WriteAsync(Frame(101, "first").Concat(Frame(307, "second")).ToArray());
        var responses = new Dictionary<long, string?>();
        for (var index = 0; index < 2; index++)
        {
            using var document = JsonDocument.Parse(await ReadFrameAsync(peer));
            var response = BridgeJson.Read<BridgeEnvelope>(document.RootElement);
            Assert.Equal("response", response.Kind);
            Assert.Null(response.Fault);
            responses.Add(response.Id, response.Payload.GetString());
        }
        Assert.Equal("first", responses[101]);
        Assert.Equal("second", responses[307]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TruncatedHeaderOrBodyFaultsPendingCall(bool truncateHeader)
    {
        var (server, client) = await ConnectAsync();
        await using var peer = server;
        await using var connection = new BridgeConnection(client);
        connection.Start();
        var pending = connection.CallAsync("pending", BridgeJson.Element("never acknowledged"));
        await ReadFrameAsync(peer);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 64);
        await peer.WriteAsync(truncateHeader ? header.AsMemory(0, 2) : header);
        if (!truncateHeader) await peer.WriteAsync(new byte[] { (byte)'{', (byte)'"' });
        await peer.DisposeAsync();
        await Assert.ThrowsAnyAsync<IOException>(() => pending.WaitAsync(Deadline));
        await connection.Completion.WaitAsync(Deadline);
    }

    [Fact]
    public async Task BlockedRequestAllowsOtherRequestsAndReverseCallbackWithCorrectCorrelation()
    {
        var (server, client) = await ConnectAsync();
        await using var parent = new BridgeConnection(client);
        await using var child = new BridgeConnection(server);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        parent.Request = (operation, payload, _) =>
        {
            Assert.Equal("host.echo", operation);
            return Task.FromResult(BridgeJson.Element("parent:" + payload.GetString()));
        };
        child.Request = async (operation, payload, cancellationToken) =>
        {
            if (operation == "blocked")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return BridgeJson.Element("released:" + payload.GetString());
            }
            if (operation == "callback")
                return await child.CallAsync("host.echo", payload, cancellationToken);
            Assert.Equal("echo", operation);
            return payload.Clone();
        };
        parent.Start();
        child.Start();
        var blocked = parent.CallAsync("blocked", BridgeJson.Element("first"));
        try
        {
            await entered.Task.WaitAsync(Deadline);
            Assert.Equal("second", (await parent.CallAsync("echo", BridgeJson.Element("second")).WaitAsync(Deadline)).GetString());
            Assert.Equal("parent:third", (await parent.CallAsync("callback", BridgeJson.Element("third")).WaitAsync(Deadline)).GetString());
            Assert.False(blocked.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.Equal("released:first", (await blocked.WaitAsync(Deadline)).GetString());
    }

    [Fact]
    public async Task CancelingStartedRequestCancelsRemoteTokenWithoutBreakingOtherRequests()
    {
        var (server, client) = await ConnectAsync();
        await using var parent = new BridgeConnection(client);
        await using var child = new BridgeConnection(server);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        child.Request = async (operation, payload, cancellationToken) =>
        {
            if (operation == "wait")
            {
                using var registration = cancellationToken.Register(() => canceled.TrySetResult());
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            else Assert.Equal("echo", operation);
            return payload.Clone();
        };
        parent.Start();
        child.Start();
        using var cancellation = new CancellationTokenSource();
        var pending = parent.CallAsync("wait", BridgeJson.Element("canceled"), cancellation.Token);
        await entered.Task.WaitAsync(Deadline);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Deadline));
        await canceled.Task.WaitAsync(Deadline);
        Assert.Equal("still connected", (await parent.CallAsync("echo", BridgeJson.Element("still connected")).WaitAsync(Deadline)).GetString());
    }

    [Fact]
    public async Task ClosingWhileCanceledHandlersUnwindDoesNotCancelDisposedSources()
    {
        for (var iteration = 0; iteration < 50; iteration++)
        {
            var (server, client) = await ConnectAsync();
            await using var parent = new BridgeConnection(client);
            await using var child = new BridgeConnection(server);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            child.Request = async (_, payload, cancellationToken) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return payload.Clone();
            };
            parent.Start();
            child.Start();
            using var cancellation = new CancellationTokenSource();
            var pending = parent.CallAsync("wait", BridgeJson.Element("canceled"), cancellation.Token);
            await entered.Task.WaitAsync(Deadline);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Deadline));
            await child.DisposeAsync();
            await child.Completion.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task PendingRequestLimitRejectsThe129thRequestWithoutBlockingEvents()
    {
        var (server, client) = await ConnectAsync();
        await using var peer = server;
        await using var connection = new BridgeConnection(client);
        connection.Start();
        using var cancellation = new CancellationTokenSource();
        var requests = Enumerable.Range(0, 129)
            .Select(index => connection.CallAsync("held", BridgeJson.Element(index.ToString(
                System.Globalization.CultureInfo.InvariantCulture)), cancellation.Token)).ToArray();
        connection.Publish("barrier", BridgeJson.Element("after all 129 attempted requests"));
        try
        {
            for (var index = 0; index < 128; index++)
            {
                using var document = JsonDocument.Parse(await ReadFrameAsync(peer));
                var frame = BridgeJson.Read<BridgeEnvelope>(document.RootElement);
                Assert.Equal("request", frame.Kind);
                Assert.Equal("held", frame.Operation);
                Assert.Equal(index.ToString(System.Globalization.CultureInfo.InvariantCulture), frame.Payload.GetString());
            }
            using var barrierDocument = JsonDocument.Parse(await ReadFrameAsync(peer));
            var barrier = BridgeJson.Read<BridgeEnvelope>(barrierDocument.RootElement);
            Assert.Equal("event", barrier.Kind);
            Assert.Equal("barrier", barrier.Operation);
            Assert.Equal("after all 129 attempted requests", barrier.Payload.GetString());
            Assert.All(requests.Take(128), request => Assert.False(request.IsCompleted));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => requests[128]);
            Assert.Contains("request", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            cancellation.Cancel();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(requests.Take(128)).WaitAsync(Deadline));
    }

    [Fact]
    public async Task FullOutboundQueueDisconnectsInsteadOfSilentlyDroppingAnEvent()
    {
        var stream = new BlockedStream();
        await using var connection = new BridgeConnection(stream);
        var disconnected = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        connection.Disconnected += error =>
        {
            Interlocked.Increment(ref count);
            disconnected.TrySetResult(error);
        };
        connection.Start();
        var payload = BridgeJson.Element("queued event");
        connection.Publish("in-flight", payload);
        await stream.WriteStarted.Task.WaitAsync(Deadline);
        for (var index = 0; index < 512; index++) connection.Publish("queued", payload);
        Assert.False(disconnected.Task.IsCompleted);
        var publishError = Record.Exception(() => connection.Publish("overflow", payload));
        if (publishError is not null)
            Assert.Contains("queue", publishError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("queue", (await disconnected.Task.WaitAsync(Deadline)).Message, StringComparison.OrdinalIgnoreCase);
        await connection.Completion.WaitAsync(Deadline);
        await connection.DisposeAsync();
        Assert.Equal(1, Volatile.Read(ref count));
    }

    private static async Task<(NamedPipeServerStream Server, NamedPipeClientStream Client)> ConnectAsync()
    {
        var name = "dt-p-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            var accept = server.WaitForConnectionAsync();
            await client.ConnectAsync(10_000);
            await accept.WaitAsync(Deadline);
            return (server, client);
        }
        catch
        {
            server.Dispose();
            client.Dispose();
            throw;
        }
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header).AsTask().WaitAsync(Deadline);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        Assert.InRange(length, 1, 8 * 1024 * 1024);
        var body = new byte[length];
        await stream.ReadExactlyAsync(body).AsTask().WaitAsync(Deadline);
        return body;
    }

    private sealed class FragmentedStream(Stream inner, int fragmentSize) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, fragmentSize));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer[..Math.Min(buffer.Length, fragmentSize)], cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class BlockedStream : Stream
    {
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
}
