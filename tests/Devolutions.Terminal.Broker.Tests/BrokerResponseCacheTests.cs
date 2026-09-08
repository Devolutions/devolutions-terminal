using Devolutions.Terminal.Broker;
using Xunit;

namespace Devolutions.Terminal.Broker.Tests;

public sealed class BrokerResponseCacheTests
{
    private static BrokerResponse Response(string id) => new(BrokerProtocol.Version, id, BrokerStatus.Success, id);

    [Fact]
    public async Task PendingRequestCannotBlockExpiredCompletedEviction()
    {
        var clock = new TestClock();
        var cache = new BrokerResponseCache(2, 2, clock);
        var pending = new TaskCompletionSource<BrokerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = cache.GetOrAdd("pending", () => pending.Task);
        await cache.GetOrAdd("completed", () => Task.FromResult(Response("completed")))!;
        Assert.Null(cache.GetOrAdd("full", () => Task.FromResult(Response("full"))));
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Same(first, cache.GetOrAdd("pending", () => throw new InvalidOperationException()));
        Assert.NotNull(cache.GetOrAdd("new", () => Task.FromResult(Response("new"))));
        pending.SetResult(Response("pending"));
        await first!;
    }

    [Fact]
    public async Task RetryRetentionBeginsWhenSlowRequestCompletes()
    {
        var clock = new TestClock();
        var cache = new BrokerResponseCache(1, 1, clock);
        var pending = new TaskCompletionSource<BrokerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = cache.GetOrAdd("slow", () => pending.Task);
        clock.Advance(TimeSpan.FromMinutes(1));
        pending.SetResult(Response("slow"));
        await original!;
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Null(cache.GetOrAdd("other", () => Task.FromResult(Response("other"))));
        Assert.Same(original, cache.GetOrAdd("slow", () => throw new InvalidOperationException()));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.NotNull(cache.GetOrAdd("other", () => Task.FromResult(Response("other"))));
    }

    [Fact]
    public async Task ActiveAdmissionIsBoundedAndDuplicateJoinsOriginal()
    {
        var cache = new BrokerResponseCache(8, 1);
        var pending = new TaskCompletionSource<BrokerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = cache.GetOrAdd("one", () => pending.Task);
        Assert.Null(cache.GetOrAdd("two", () => throw new InvalidOperationException()));
        Assert.Same(original, cache.GetOrAdd("one", () => throw new InvalidOperationException()));
        pending.SetResult(Response("one"));
        await original!;
        Assert.NotNull(cache.GetOrAdd("two", () => Task.FromResult(Response("two"))));
    }

    private sealed class TestClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}
