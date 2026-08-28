using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;

namespace PeerSharp.Internals.Dht;

/// <summary>
/// A per-source budget for inbound DHT queries.
///
/// <para>
/// A DHT node answers strangers by design, and every query it answers costs a parse, a routing
/// table walk and a reply larger than the question that prompted it. Unbudgeted, that is two things
/// at once: a way to spend our CPU and sockets from outside, and a reflector - the reply to
/// <c>get_peers</c> is many times the size of the query, and the source address of a UDP datagram is
/// whatever the sender wrote in it.
/// </para>
///
/// <para>
/// Over-limit queries are dropped rather than refused. There is no "slow down" in BEP 5, and an
/// error reply would be a packet sent to an address that has not proven it asked - which is the
/// amplification this exists to prevent.
/// </para>
/// </summary>
internal sealed class DhtQueryRateLimiter
{
    /// <summary>Queries accepted from one address per <see cref="DefaultWindow"/>.</summary>
    public const int DefaultQueriesPerAddress = 60;

    /// <summary>
    /// How many source addresses are tracked at once. The limiter must not become the exhaustion
    /// vector it exists to close, so its own table is bounded too.
    /// </summary>
    public const int DefaultMaxTrackedAddresses = 20_000;

    /// <summary>
    /// Queries accepted per window from all sources the table has no room to track, together.
    ///
    /// <para>
    /// This is what stops a full table from becoming a bypass. Per-source accounting needs a slot per
    /// source, and slots are finite; an attacker who can forge addresses can therefore always exhaust
    /// them. What must not follow is that everything else is then waved through - fill the table with
    /// twenty thousand invented addresses and every subsequent query, including a flood spoofed as
    /// one chosen victim, would be unbudgeted. A shared allowance keeps the total bounded when
    /// per-source accounting is no longer possible, which is the property the limit is actually for.
    /// </para>
    ///
    /// <para>
    /// Ten a second, well above what genuine overflow looks like: reaching it needs more than
    /// <see cref="DefaultMaxTrackedAddresses"/> distinct addresses inside one window before an
    /// untracked source is even considered.
    /// </para>
    /// </summary>
    public const int DefaultFallbackQueriesPerWindow = 600;

    /// <summary>Window over which <see cref="DefaultQueriesPerAddress"/> is counted.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);

    private readonly Lock _lock = new();
    private readonly Dictionary<IPAddress, RateCounter> _counters = [];
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DhtQueryRateLimiter> _logger;
    private readonly int _queriesPerAddress;
    private readonly int _maxTrackedAddresses;
    private readonly int _fallbackQueriesPerWindow;
    private readonly TimeSpan _window;
    private long _droppedQueries;

    // The shared allowance for sources the table cannot track, and the window it belongs to.
    private DateTimeOffset _fallbackWindowStart;
    private int _fallbackCount;

    public DhtQueryRateLimiter(TimeProvider timeProvider)
        : this(timeProvider, NullLoggerFactory.Instance, DefaultQueriesPerAddress, DefaultWindow, DefaultMaxTrackedAddresses, DefaultFallbackQueriesPerWindow)
    {
    }

    public DhtQueryRateLimiter(
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        int queriesPerAddress,
        TimeSpan window,
        int maxTrackedAddresses,
        int fallbackQueriesPerWindow = DefaultFallbackQueriesPerWindow)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queriesPerAddress);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTrackedAddresses);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fallbackQueriesPerWindow);

        _timeProvider = timeProvider;
        _logger = loggerFactory.CreateLogger<DhtQueryRateLimiter>();
        _queriesPerAddress = queriesPerAddress;
        _window = window;
        _maxTrackedAddresses = maxTrackedAddresses;
        _fallbackQueriesPerWindow = fallbackQueriesPerWindow;
        _fallbackWindowStart = timeProvider.GetUtcNow();
    }

    /// <summary>Total queries dropped since startup. Diagnostics only.</summary>
    public long DroppedQueries => Interlocked.Read(ref _droppedQueries);

    /// <summary>Source addresses currently being tracked.</summary>
    public int TrackedAddresses
    {
        get
        {
            lock (_lock)
            {
                return _counters.Count;
            }
        }
    }

    /// <summary>
    /// Whether a query from <paramref name="source"/> should be answered. Consumes one unit of
    /// budget when it returns <see langword="true"/>.
    /// </summary>
    public bool IsQueryAllowed(IPAddress source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            if (_counters.TryGetValue(source, out var counter) && now - counter.WindowStart < _window)
            {
                if (counter.Count >= _queriesPerAddress)
                {
                    Interlocked.Increment(ref _droppedQueries);
                    _logger.LogTrace("Rate limited DHT query from {Source}", source);
                    return false;
                }

                _counters[source] = counter with { Count = counter.Count + 1 };
                return true;
            }

            if (_counters.Count >= _maxTrackedAddresses)
            {
                PruneLocked(now);

                // Still full: every tracked window is live, so this is either a genuinely busy node
                // or a flood from forged source addresses. There is no slot to account for this
                // source individually, so it draws on the shared allowance instead. Refusing
                // outright would let anyone able to forge an address deny service to the addresses
                // they forge; allowing outright would make filling the table the way to bypass the
                // limit entirely.
                if (_counters.Count >= _maxTrackedAddresses)
                {
                    return IsFallbackQueryAllowedLocked(now);
                }
            }

            _counters[source] = new RateCounter(now, 1);
            return true;
        }
    }

    /// <summary>
    /// Draws one unit from the allowance shared by every source the table cannot track. Caller must
    /// hold the lock.
    /// </summary>
    private bool IsFallbackQueryAllowedLocked(DateTimeOffset now)
    {
        if (now - _fallbackWindowStart >= _window)
        {
            _fallbackWindowStart = now;
            _fallbackCount = 0;
        }

        if (_fallbackCount >= _fallbackQueriesPerWindow)
        {
            Interlocked.Increment(ref _droppedQueries);
            _logger.LogTrace("Rate limited an untracked DHT query: the source table is full and its shared allowance is spent");
            return false;
        }

        _fallbackCount++;
        return true;
    }

    /// <summary>Drops counters whose window has closed. Safe to call at any time.</summary>
    public void Prune()
    {
        lock (_lock)
        {
            PruneLocked(_timeProvider.GetUtcNow());
        }
    }

    private void PruneLocked(DateTimeOffset now)
    {
        List<IPAddress>? expired = null;
        foreach (var (address, counter) in _counters)
        {
            if (now - counter.WindowStart >= _window)
            {
                (expired ??= []).Add(address);
            }
        }

        if (expired == null)
        {
            return;
        }

        foreach (var address in expired)
        {
            _counters.Remove(address);
        }
    }

    private readonly record struct RateCounter(DateTimeOffset WindowStart, int Count);
}
