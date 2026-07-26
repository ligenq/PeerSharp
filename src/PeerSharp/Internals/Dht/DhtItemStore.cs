using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;

namespace PeerSharp.Internals.Dht;

/// <summary>
/// Storage for BEP 44 items held on behalf of the network.
///
/// A storage node accepts data from strangers, so every limit here is a defence rather than a
/// tuning knob. Items expire, the store is capped, and puts are rate limited per source address -
/// the last of those matters more than it looks, because verifying an Ed25519 signature costs
/// roughly 270 microseconds and an unlimited put rate would let one peer spend a core of ours per
/// few hundred packets. <see cref="IsPutAllowed"/> is deliberately callable before verification so
/// the handler can reject a flood without paying for the curve arithmetic.
/// </summary>
internal sealed class DhtItemStore
{
    /// <summary>How long an item survives without being refreshed. BEP 44 suggests around two hours.</summary>
    public static readonly TimeSpan DefaultExpiry = TimeSpan.FromHours(2);

    /// <summary>Maximum number of items held at once.</summary>
    public const int DefaultCapacity = 1000;

    /// <summary>Puts accepted from a single address per <see cref="RateLimitWindow"/>.</summary>
    public const int DefaultPutsPerAddress = 20;

    /// <summary>Window over which <see cref="DefaultPutsPerAddress"/> is counted.</summary>
    public static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(1);

    private readonly Lock _lock = new();
    private readonly Dictionary<DhtTarget, Entry> _items = [];
    private readonly Dictionary<IPAddress, RateCounter> _putRates = [];
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DhtItemStore> _logger;
    private readonly int _capacity;
    private readonly TimeSpan _expiry;
    private readonly int _putsPerAddress;

    public DhtItemStore(TimeProvider timeProvider)
        : this(timeProvider, NullLoggerFactory.Instance, DefaultCapacity, DefaultExpiry, DefaultPutsPerAddress)
    {
    }

    public DhtItemStore(
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        int capacity,
        TimeSpan expiry,
        int putsPerAddress)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(putsPerAddress);

        _timeProvider = timeProvider;
        _logger = loggerFactory.CreateLogger<DhtItemStore>();
        _capacity = capacity;
        _expiry = expiry;
        _putsPerAddress = putsPerAddress;
    }

    /// <summary>Number of unexpired items currently held.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                PruneLocked();
                return _items.Count;
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="source"/> is within its put budget. Call this <b>before</b>
    /// verifying a signature: that is the expensive step, and the whole point of the limit is to
    /// stop an attacker making us pay it.
    /// </summary>
    /// <remarks>Consumes one unit of budget when it returns true.</remarks>
    public bool IsPutAllowed(IPAddress source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            if (!_putRates.TryGetValue(source, out var counter) || now - counter.WindowStart >= RateLimitWindow)
            {
                _putRates[source] = new RateCounter(now, 1);
                return true;
            }

            if (counter.Count >= _putsPerAddress)
            {
                _logger.LogDebug("Rate limited DHT put from {Source}", source);
                return false;
            }

            _putRates[source] = counter with { Count = counter.Count + 1 };
            return true;
        }
    }

    /// <summary>
    /// Validates and stores an item.
    /// </summary>
    /// <param name="item">The item to store.</param>
    /// <param name="compareAndSwap">
    /// Optional expected sequence number of the item already stored, for mutable items.
    /// </param>
    /// <returns><see cref="DhtPutError.None"/> on success, otherwise the BEP 44 error to reply with.</returns>
    public DhtPutError Store(DhtItem item, long? compareAndSwap = null)
    {
        ArgumentNullException.ThrowIfNull(item);

        var structural = DhtItemCodec.Validate(item);
        if (structural != DhtPutError.None)
        {
            return structural;
        }

        var now = _timeProvider.GetUtcNow();
        var target = item.Target;

        lock (_lock)
        {
            PruneLocked();

            bool alreadyPresent = _items.TryGetValue(target, out var existing);

            if (item is DhtMutableItem mutable)
            {
                var stored = alreadyPresent ? existing.Item as DhtMutableItem : null;
                var replacement = DhtItemCodec.CheckReplacement(stored, mutable, compareAndSwap);
                if (replacement != DhtPutError.None)
                {
                    return replacement;
                }
            }
            else if (compareAndSwap is not null)
            {
                // cas is meaningless for an item whose address is its own content hash.
                return DhtPutError.Protocol;
            }

            if (!alreadyPresent && _items.Count >= _capacity && !TryEvictLocked())
            {
                // Full of live items. Refusing is better than evicting something a peer is
                // relying on; the network holds the item on other nodes too.
                _logger.LogDebug("DHT item store at capacity ({Capacity}); rejecting put", _capacity);
                return DhtPutError.Protocol;
            }

            _items[target] = new Entry(item, now);
            return DhtPutError.None;
        }
    }

    /// <summary>Retrieves an item, or null when the address is empty or the item has expired.</summary>
    public DhtItem? TryGet(DhtTarget target)
    {
        lock (_lock)
        {
            if (!_items.TryGetValue(target, out var entry))
            {
                return null;
            }

            if (_timeProvider.GetUtcNow() - entry.StoredAt >= _expiry)
            {
                _items.Remove(target);
                return null;
            }

            return entry.Item;
        }
    }

    /// <summary>Drops expired items and stale rate-limit counters.</summary>
    public void Prune()
    {
        lock (_lock)
        {
            PruneLocked();
        }
    }

    private void PruneLocked()
    {
        var now = _timeProvider.GetUtcNow();

        foreach (var target in _items.Where(pair => now - pair.Value.StoredAt >= _expiry).Select(pair => pair.Key).ToArray())
        {
            _items.Remove(target);
        }

        foreach (var address in _putRates.Where(pair => now - pair.Value.WindowStart >= RateLimitWindow).Select(pair => pair.Key).ToArray())
        {
            _putRates.Remove(address);
        }
    }

    /// <summary>
    /// Evicts the oldest item to make room. Returns false when nothing can be evicted.
    /// </summary>
    /// <remarks>
    /// Oldest-first is the simple choice. A DHT-aware store would prefer to drop whichever item
    /// is furthest from our node id, since those are the ones we are least responsible for, but
    /// that needs a full XOR ordering the routing table does not currently expose.
    /// </remarks>
    private bool TryEvictLocked()
    {
        if (_items.Count == 0)
        {
            return false;
        }

        var oldest = _items.OrderBy(pair => pair.Value.StoredAt).First();
        _items.Remove(oldest.Key);
        return true;
    }

    private readonly record struct Entry(DhtItem Item, DateTimeOffset StoredAt);

    private readonly record struct RateCounter(DateTimeOffset WindowStart, int Count);
}
