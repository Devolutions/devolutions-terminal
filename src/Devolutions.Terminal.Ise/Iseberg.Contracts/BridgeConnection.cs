using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace Iseberg.Core;

public sealed class BridgeRemoteException(BridgeFault fault) : InvalidOperationException(fault.Message)
{
    public BridgeFault Fault { get; } = fault;
}

/// <summary>Persistent bidirectional correlation; handlers never execute on the frame reader.</summary>
public sealed class BridgeConnection(Stream stream) : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private readonly ConcurrentDictionary<long, CancellationTokenSource> incoming = new();
    private readonly SemaphoreSlim requestSlots = new(IseBridgeProtocol.MaximumPendingRequests);
    private readonly Channel<BridgeEnvelope> writes = Channel.CreateBounded<BridgeEnvelope>(
        new BoundedChannelOptions(IseBridgeProtocol.MaximumQueuedFrames)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private long nextId;
    private int failed;
    private Task? reader;
    private Task? writer;
    public Func<string, JsonElement, CancellationToken, Task<JsonElement>>? Request { get; set; }
    public Action<string, JsonElement>? Event { get; set; }
    public event Action<Exception>? Disconnected;
    public Task Completion => reader ?? Task.CompletedTask;

    public void Start()
    {
        if (reader is not null) throw new InvalidOperationException("The bridge reader is already running.");
        writer = Task.Run(WriteLoopAsync);
        reader = Task.Run(ReadLoopAsync);
    }

    public async Task<JsonElement> CallAsync(string operation, JsonElement payload, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(lifetime.IsCancellationRequested, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!requestSlots.Wait(0))
            throw new InvalidOperationException("The private PowerShell request queue is full.");
        var id = Interlocked.Increment(ref nextId);
        var result = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, result)) throw new InvalidOperationException("Duplicate bridge request.");
        using var registration = cancellationToken.Register(() =>
        {
            if (pending.TryRemove(id, out var request))
            {
                request.TrySetCanceled(cancellationToken);
                if (!lifetime.IsCancellationRequested)
                {
                    try { Send(new("cancel", id, operation, BridgeJson.Empty)); }
                    catch (IOException) { }
                }
            }
        });
        try
        {
            Send(new("request", id, operation, payload));
            return await result.Task.ConfigureAwait(false);
        }
        finally
        {
            pending.TryRemove(id, out _);
            requestSlots.Release();
        }
    }

    public void Publish(string operation, JsonElement payload) => Send(new("event", 0, operation, payload));

    private void Send(BridgeEnvelope envelope)
    {
        if (!writes.Writer.TryWrite(envelope))
        {
            var error = new IOException("The private PowerShell transport queue overflowed. Session state was lost.");
            Fail(error);
            throw error;
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var envelope in writes.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, IseBridgeJsonContext.Default.BridgeEnvelope);
                if (bytes.Length > IseBridgeProtocol.MaximumFrameBytes)
                    throw new InvalidDataException("The private PowerShell frame exceeds its size limit.");
                var prefix = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
                await stream.WriteAsync(prefix, lifetime.Token).ConfigureAwait(false);
                await stream.WriteAsync(bytes, lifetime.Token).ConfigureAwait(false);
                await stream.FlushAsync(lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) { Fail(exception); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            var prefix = new byte[4];
            while (!lifetime.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(prefix, lifetime.Token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
                if (length is <= 0 or > IseBridgeProtocol.MaximumFrameBytes)
                    throw new InvalidDataException("Invalid private PowerShell frame length.");
                var bytes = new byte[length];
                await stream.ReadExactlyAsync(bytes, lifetime.Token).ConfigureAwait(false);
                var envelope = JsonSerializer.Deserialize(bytes, IseBridgeJsonContext.Default.BridgeEnvelope)
                    ?? throw new InvalidDataException("Empty private PowerShell frame.");
                switch (envelope.Kind)
                {
                    case "response":
                        if (pending.TryRemove(envelope.Id, out var result))
                        {
                            if (envelope.Fault is { Code: "Canceled" }) result.TrySetCanceled();
                            else if (envelope.Fault is { } fault) result.TrySetException(new BridgeRemoteException(fault));
                            else result.TrySetResult(envelope.Payload);
                        }
                        break;
                    case "request":
                        if (incoming.Count >= IseBridgeProtocol.MaximumPendingRequests)
                            throw new InvalidDataException("The private PowerShell incoming request queue is full.");
                        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        if (!incoming.TryAdd(envelope.Id, cancellation))
                            throw new InvalidDataException("Duplicate private PowerShell incoming request.");
                        _ = Task.Run(() => DispatchAsync(envelope, cancellation));
                        break;
                    case "cancel":
                        lock (incoming)
                        {
                            if (incoming.TryGetValue(envelope.Id, out var canceled)) canceled.Cancel();
                        }
                        break;
                    case "event":
                        Event?.Invoke(envelope.Operation, envelope.Payload);
                        break;
                    default: throw new InvalidDataException("Unknown private PowerShell envelope kind.");
                }
            }
        }
        catch (Exception exception) { Fail(exception); }
    }

    private async Task DispatchAsync(BridgeEnvelope envelope, CancellationTokenSource cancellation)
    {
        try
        {
            var handler = Request ?? throw new InvalidOperationException("No private PowerShell request handler is attached.");
            var value = await handler(envelope.Operation, envelope.Payload, cancellation.Token).ConfigureAwait(false);
            if (!lifetime.IsCancellationRequested) Send(new("response", envelope.Id, envelope.Operation, value));
        }
        catch (Exception exception)
        {
            var fault = exception switch
            {
                OperationCanceledException => new BridgeFault("Canceled", "The PowerShell operation was canceled."),
                FileConflictException conflict => new BridgeFault("FileConflict", conflict.Message,
                    conflict.FilePath, conflict.ExpectedVersion, conflict.ActualVersion),
                _ => new BridgeFault(exception.GetType().Name, exception.Message)
            };
            if (!lifetime.IsCancellationRequested)
            {
                try { Send(new("response", envelope.Id, envelope.Operation, BridgeJson.Empty, fault)); }
                catch (IOException) { }
            }
        }
        finally
        {
            lock (incoming)
            {
                incoming.TryRemove(envelope.Id, out _);
                cancellation.Dispose();
            }
        }
    }

    private void Fail(Exception exception)
    {
        if (Interlocked.Exchange(ref failed, 1) != 0) return;
        lifetime.Cancel();
        writes.Writer.TryComplete();
        stream.Dispose();
        foreach (var result in pending.Values) result.TrySetException(exception);
        lock (incoming)
        {
            foreach (var cancellation in incoming.Values) cancellation.Cancel();
        }
        Disconnected?.Invoke(exception);
    }

    public async ValueTask DisposeAsync()
    {
        Fail(new ObjectDisposedException(nameof(BridgeConnection)));
        if (reader is not null) await reader.ConfigureAwait(false);
        if (writer is not null) await writer.ConfigureAwait(false);
        lifetime.Dispose();
    }
}
