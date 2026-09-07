namespace Devolutions.Terminal.Broker;

internal sealed class BrokerResponseCache(
    int capacity = 1024,
    int activeCapacity = 128,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan RetryWindow = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private int _active;

    public Task<BrokerResponse>? GetOrAdd(string id, Func<Task<BrokerResponse>> dispatch)
    {
        Entry entry;
        lock (_gate)
        {
            // Completed retention begins at completion, not admission. Active requests
            // never expire: a retry must join the original operation even under load.
            foreach (var expired in _entries.Where(pair =>
                         pair.Value.CompletedAt is { } completed &&
                         _clock.GetElapsedTime(completed) >= RetryWindow).Select(pair => pair.Key).ToArray())
            {
                _entries.Remove(expired);
            }

            if (!_entries.TryGetValue(id, out entry!))
            {
                if (_entries.Count >= capacity || _active >= activeCapacity)
                {
                    return null;
                }

                entry = new Entry();
                entry.Operation = new Lazy<Task<BrokerResponse>>(
                    () => RunAsync(entry, dispatch), LazyThreadSafetyMode.ExecutionAndPublication);
                _entries.Add(id, entry);
                _active++;
            }
        }

        return entry.Operation.Value;
    }

    private async Task<BrokerResponse> RunAsync(Entry entry, Func<Task<BrokerResponse>> dispatch)
    {
        try
        {
            return await dispatch().ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                entry.CompletedAt = _clock.GetTimestamp();
                _active--;
            }
        }
    }

    private sealed class Entry
    {
        public Lazy<Task<BrokerResponse>> Operation { get; set; } = null!;
        public long? CompletedAt { get; set; }
    }
}
