namespace PeerSharp.Internals.Bandwidth;

internal interface IBandwidthUser
{
    string Name { get; }

    void AssignBandwidth(int amount);
}

/// <summary>
/// A token bucket of transfer quota for one direction of one torrent.
/// </summary>
/// <remarks>
/// <para>
/// Callers are concurrent by design: <see cref="BandwidthManager"/> spends quota from a deliberately
/// lock-free fast path, on whichever peer thread wants to send, while its tick loop refills every
/// channel from another thread entirely.
/// </para>
/// <para>
/// Each mutator therefore commits as one step. An earlier version added and then clamped in a
/// separate interlocked operation, which is not the same thing: a spend large enough to break the
/// debt floor could be lifted back above it by a concurrent refund arriving between the two, so the
/// clamp re-read a value that no longer needed clamping and the floor went unenforced. The property
/// test in PropertyBasedConcurrencyTests reproduces exactly that interleaving, and no combination of
/// separate interlocked steps fixes it, because the pair is what has to be atomic - not each half.
/// </para>
/// </remarks>
internal class BandwidthChannel
{
    private readonly Lock _lock = new();

    private long _limit;
    private long _quota;
    private long _subQuota;

    public BandwidthChannel(TimeProvider timeProvider)
    {
        _limit = 0; // 0 = infinite
        _quota = 0;
    }

    /// <summary>
    /// The quota available to spend now, or <see cref="int.MaxValue"/> when the channel is unlimited.
    /// </summary>
    /// <remarks>
    /// Read without taking the lock. Every mutator commits <c>_quota</c> in a single store, so this
    /// observes a value the channel genuinely held rather than a torn or half-applied one.
    /// </remarks>
    public int AvailableQuota
    {
        get
        {
            long limit = Interlocked.Read(ref _limit);
            if (limit == 0)
            {
                return int.MaxValue;
            }

            return (int)Math.Clamp(Interlocked.Read(ref _quota), int.MinValue, int.MaxValue);
        }
    }

    public bool CanUse(int amount)
    {
        long limit = Interlocked.Read(ref _limit);
        if (limit == 0)
        {
            return true;
        }

        return Interlocked.Read(ref _quota) >= amount;
    }

    public long GetLimit()
    {
        return Interlocked.Read(ref _limit);
    }

    /// <summary>
    /// Returns unused quota to the channel, up to the burst ceiling.
    /// </summary>
    public void ReturnQuota(int amount)
    {
        if (amount <= 0)
        {
            return;
        }

        lock (_lock)
        {
            if (_limit == 0)
            {
                return;
            }

            Interlocked.Exchange(ref _quota, Math.Min(SaturatingTriple(_limit), AddSaturating(_quota, amount)));
        }
    }

    /// <summary>
    /// Sets the channel's rate in bytes per second, or 0 for unlimited.
    /// </summary>
    /// <remarks>
    /// Takes the lock like the other mutators. The limit is what every one of them clamps against,
    /// so changing it outside would let a spend read one limit and enforce a ceiling derived from
    /// another - the same split-step problem the mutators exist to avoid, and reachable in practice
    /// because limits are reconfigured while transfers are running.
    /// </remarks>
    public void SetLimit(long limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        lock (_lock)
        {
            Interlocked.Exchange(ref _limit, limit);
        }
    }

    /// <summary>
    /// Refills the channel for <paramref name="dt"/> milliseconds of elapsed time.
    /// </summary>
    public void UpdateQuota(int dt)
    {
        lock (_lock)
        {
            if (_limit == 0)
            {
                return;
            }

            // _limit is bytes/sec and dt is ms, so the refill is limit * dt / 1000. The remainder is
            // carried in _subQuota rather than discarded, or a channel ticked often enough would be
            // rounded down to a standstill.
            long generated = _limit > long.MaxValue / dt ? long.MaxValue : _limit * dt;
            long delta = generated / 1000;

            long subQuota = _subQuota + (generated % 1000);
            if (subQuota >= 1000)
            {
                delta += subQuota / 1000;
                subQuota %= 1000;
            }

            _subQuota = subQuota;

            // Cap the bucket so an idle channel cannot bank an unbounded burst. libtorrent uses the
            // same 3x ceiling.
            Interlocked.Exchange(ref _quota, Math.Min(SaturatingTriple(_limit), AddSaturating(_quota, delta)));
        }
    }

    /// <summary>
    /// Spends quota, allowing bounded debt.
    /// </summary>
    /// <remarks>
    /// Quota may go negative: callers check and spend as two steps, so concurrent senders can commit
    /// to more than the bucket held. That is temporary over-allocation and the next refill absorbs
    /// it. The floor is what stops the debt growing without bound.
    /// </remarks>
    public void UseQuota(int amount)
    {
        lock (_lock)
        {
            if (_limit == 0)
            {
                return;
            }

            Interlocked.Exchange(ref _quota, Math.Max(-SaturatingTriple(_limit), AddSaturating(_quota, -amount)));
        }
    }

    private static long AddSaturating(long current, long value)
    {
        if (value > 0 && current > long.MaxValue - value)
        {
            return long.MaxValue;
        }

        if (value < 0 && current < long.MinValue - value)
        {
            return long.MinValue;
        }

        return current + value;
    }

    private static long SaturatingTriple(long value) => value > long.MaxValue / 3 ? long.MaxValue : value * 3;
}
