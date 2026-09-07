using Devolutions.Terminal.Connection;
using Xunit;

namespace Devolutions.Terminal.Connection.Tests;

public sealed class OrderedInputWriterTests
{
    [Fact]
    public async Task BlockedWriterDoesNotBlockAdmissionAndPreservesOrder()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new List<byte>();
        var writer = new OrderedInputWriter(async (data, token) =>
        {
            await release.Task.WaitAsync(token);
            output.AddRange(data.ToArray());
        }, error => Assert.Fail(error.ToString()), CancellationToken.None);

        var first = writer.Enqueue(new byte[] { 1, 2 });
        var second = writer.Enqueue(new byte[] { 3 });
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        writer.Complete();
        await writer.Completion;
        Assert.Equal(new byte[] { 1, 2, 3 }, output);
    }

    [Fact]
    public async Task ByteLimitRejectsWholeWriteWithoutLosingAcceptedInput()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = 0;
        var writer = new OrderedInputWriter(async (data, token) =>
        {
            await release.Task.WaitAsync(token);
            written += data.Length;
        }, error => Assert.Fail(error.ToString()), CancellationToken.None);
        var accepted = writer.Enqueue(new byte[OrderedInputWriter.MaximumBytes]);
        Assert.Throws<IOException>(() => { _ = writer.Enqueue(new byte[1]); });
        release.SetResult();
        await accepted.WaitAsync(TimeSpan.FromSeconds(5));
        writer.Complete();
        await writer.Completion;
        Assert.Equal(OrderedInputWriter.MaximumBytes, written);
    }

    [Fact]
    public async Task CancelledPendingWriteIsSkippedWithoutCorruptingNextWrite()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new List<byte>();
        var writer = new OrderedInputWriter(async (data, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            output.AddRange(data.ToArray());
        }, error => Assert.Fail(error.ToString()), CancellationToken.None);
        var first = writer.Enqueue(new byte[] { 1 });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancelled = new CancellationTokenSource();
        var second = writer.Enqueue(new byte[] { 2 }, cancelled.Token);
        var third = writer.Enqueue(new byte[] { 3 });
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(TimeSpan.FromSeconds(1)));
        release.SetResult();
        await Task.WhenAll(first, third).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        writer.Complete();
        await writer.Completion;
        Assert.Equal(new byte[] { 1, 3 }, output);
    }

    [Fact]
    public async Task PendingWriteCountIsBoundedIndependentlyOfBytes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new OrderedInputWriter(async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        }, error => Assert.Fail(error.ToString()), CancellationToken.None);
        var first = writer.Enqueue(new byte[1]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = Enumerable.Range(0, 256).Select(_ => writer.Enqueue(new byte[1])).ToArray();
        Assert.Throws<IOException>(() => { _ = writer.Enqueue(new byte[1]); });
        release.SetResult();
        writer.Complete();
        await Task.WhenAll(queued.Append(first)).WaitAsync(TimeSpan.FromSeconds(5));
        await writer.Completion;
    }

    [Fact]
    public async Task CancellingInFlightWriteInterruptsSessionAndNeverStartsNextFrame()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interrupts = 0;
        var writes = 0;
        var writer = new OrderedInputWriter(async (_, token) =>
        {
            writes++;
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, _ => { }, CancellationToken.None, () => interrupts++);
        var first = writer.Enqueue(new byte[1], cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = writer.Enqueue(new byte[1]);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        await writer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, interrupts);
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task CancellationAfterDeliveryDoesNotInterruptSession()
    {
        using var cancellation = new CancellationTokenSource();
        var interrupts = 0;
        var writer = new OrderedInputWriter((_, _) => ValueTask.CompletedTask,
            error => Assert.Fail(error.ToString()), CancellationToken.None, () => interrupts++);
        await writer.Enqueue(new byte[1], cancellation.Token);
        cancellation.Cancel();
        writer.Complete();
        await writer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, interrupts);
    }

    [Fact]
    public async Task ShutdownInterruptsBlockedWriteAndReportsUndeliveredInput()
    {
        using var lifetime = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? reported = null;
        var writer = new OrderedInputWriter(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, error => reported = error, lifetime.Token);
        var first = writer.Enqueue(new byte[1]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = writer.Enqueue(new byte[1]);
        lifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        await writer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<IOException>(reported);
    }

    [Fact]
    public async Task TransportFailureFailsAllQueuedWritesAndRejectsFurtherInput()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? reported = null;
        var writer = new OrderedInputWriter(async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            throw new IOException("disconnected");
        }, error => reported = error, CancellationToken.None);
        var first = writer.Enqueue(new byte[] { 1 });
        var second = writer.Enqueue(new byte[] { 2 });
        release.SetResult();
        await Assert.ThrowsAsync<IOException>(() => first);
        await Assert.ThrowsAsync<IOException>(() => second);
        await writer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<IOException>(reported);
        Assert.Throws<IOException>(() => { _ = writer.Enqueue(new byte[] { 3 }); });
    }
}
