using System.Threading.Channels;

namespace Devolutions.Terminal.Connection;

internal sealed class OrderedInputWriter
{
    internal const int MaximumBytes = 4 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Channel<PendingWrite> _queue = Channel.CreateBounded<PendingWrite>(256);
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _write;
    private readonly Action<Exception> _fault;
    private readonly CancellationToken _lifetime;
    private readonly Action? _interrupt;
    private int _bufferedBytes;
    private bool _stopped;

    public OrderedInputWriter(
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> write,
        Action<Exception> fault,
        CancellationToken lifetime,
        Action? interrupt = null)
    {
        _write = write;
        _fault = fault;
        _lifetime = lifetime;
        _interrupt = interrupt;
        Completion = Task.Run(RunAsync);
    }

    public Task Completion { get; }

    public void Post(ReadOnlySpan<byte> data)
    {
        var completion = Enqueue(data);
        _ = completion.ContinueWith(task => _ = task.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public Task Enqueue(ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_stopped || _lifetime.IsCancellationRequested)
            {
                throw new IOException("Terminal input is closed.");
            }

            if (data.Length > MaximumBytes - _bufferedBytes)
            {
                throw new IOException("Terminal input queue is full (4 MiB). Wait for the terminal to read input and retry.");
            }

            var pending = new PendingWrite(data.ToArray(), cancellationToken, _interrupt);
            if (!_queue.Writer.TryWrite(pending))
            {
                pending.Dispose();
                throw new IOException("Terminal input queue is full (256 writes). Wait for the terminal to read input and retry.");
            }

            _bufferedBytes += data.Length;
            return pending.Completion.Task;
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _stopped = true;
            _queue.Writer.TryComplete();
        }
    }

    private async Task RunAsync()
    {
        Exception? failure = null;
        var interruptedBytes = 0;
        try
        {
            await foreach (var pending in _queue.Reader.ReadAllAsync(_lifetime).ConfigureAwait(false))
            {
                try
                {
                    // An in-flight frame cannot be skipped safely. Cancellation stops
                    // the session rather than allowing another frame after partial input.
                    if (!pending.TryStart())
                    {
                        continue;
                    }

                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime, pending.CancellationToken);
                    await _write(pending.Data, linked.Token).ConfigureAwait(false);
                    pending.MarkCompleted();
                    pending.Completion.TrySetResult();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
                {
                    failure = ex;
                    interruptedBytes = pending.Data.Length;
                    pending.Completion.TrySetException(ex);
                    break;
                }
                finally
                {
                    pending.Dispose();
                    lock (_gate)
                    {
                        _bufferedBytes -= pending.Data.Length;
                    }
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            failure = ex;
        }
        finally
        {
            Complete();
            var discarded = 0;
            while (_queue.Reader.TryRead(out var pending))
            {
                discarded += pending.Completion.Task.IsCanceled ? 0 : pending.Data.Length;
                pending.Completion.TrySetException(failure ?? new IOException("Terminal input is closed."));
                pending.Dispose();
            }

            if (failure is not null && (failure is not OperationCanceledException || discarded + interruptedBytes > 0))
            {
                _fault(new IOException("Terminal input did not complete; queued input was not delivered.", failure));
            }
        }
    }

    private sealed class PendingWrite : IDisposable
    {
        private readonly CancellationTokenRegistration _registration;
        private int _state;

        public PendingWrite(byte[] data, CancellationToken cancellationToken, Action? interrupt)
        {
            Data = data;
            CancellationToken = cancellationToken;
            _registration = cancellationToken.Register(() =>
            {
                var state = Interlocked.CompareExchange(ref _state, 2, 0);
                if (state == 0)
                {
                    Completion.TrySetCanceled(cancellationToken);
                }
                else if (state == 1)
                {
                    interrupt?.Invoke();
                }
            });
        }

        public byte[] Data { get; }
        public CancellationToken CancellationToken { get; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool TryStart() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;
        public void MarkCompleted() => Interlocked.Exchange(ref _state, 3);
        public void Dispose() => _registration.Dispose();
    }
}
