namespace PeerSharp.Internals.Bandwidth;

internal interface IBandwidthUser
{
    string Name { get; }

    void AssignBandwidth(int amount);
}

internal class BandwidthChannel
{
    // Multiple threads can call UpdateQuota, UseQuota, ReturnQuota simultaneously
    private long _limit;

    private long _quota;
    private long _subQuota;

    public BandwidthChannel(TimeProvider timeProvider)
    {
        _limit = 0; // 0 = infinite
        _quota = 0;
    }

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

        long quota = Interlocked.Read(ref _quota);
        return quota >= amount;
    }

    public long GetLimit()
    {
        return Interlocked.Read(ref _limit);
    }

    /// <summary>
    /// Returns unused bandwidth quota back to the channel.
    /// Thread-safe using Interlocked operations.
    /// </summary>
    public void ReturnQuota(int amount)
    {
        long limit = Interlocked.Read(ref _limit);
        if (limit == 0 || amount <= 0)
        {
            return;
        }

        long newQuota = AddSaturating(ref _quota, amount);

        // Cap to prevent quota from growing unboundedly
        long maxQuota = SaturatingTriple(limit);
        if (maxQuota > 0 && newQuota > maxQuota)
        {
            // Atomically clamp to max using CompareExchange loop
            long current;
            do
            {
                current = Interlocked.Read(ref _quota);
                if (current <= maxQuota)
                {
                    break;
                }
            } while (Interlocked.CompareExchange(ref _quota, maxQuota, current) != current);
        }
    }

    public void SetLimit(long limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        Interlocked.Exchange(ref _limit, limit);
    }

    public void UpdateQuota(int dt)
    {
        long limit = Interlocked.Read(ref _limit);
        if (limit == 0)
        {
            return;
        }

        // Add to quota based on time passed and limit
        // limit is bytes/sec. dt is ms.
        // quota += limit * dt / 1000

        long newQuota = limit > long.MaxValue / dt ? long.MaxValue : limit * dt;
        long quotaDelta = newQuota / 1000;
        long subQuotaDelta = newQuota % 1000;

        long newSubQuota = Interlocked.Add(ref _subQuota, subQuotaDelta);

        // Handle overflow from subQuota to quota
        if (newSubQuota >= 1000)
        {
            // Atomically transfer overflow: subQuota -= 1000, quota += 1
            long actualSubQuota = Interlocked.Add(ref _subQuota, -1000);
            if (actualSubQuota >= 0)
            {
                quotaDelta++; // Add overflow to quota delta
            }
            else
            {
                // Race: another thread already processed overflow, undo our subtraction
                Interlocked.Add(ref _subQuota, 1000);
            }
        }

        // Add quota delta
        long newTotalQuota = AddSaturating(ref _quota, quotaDelta);

        // Cap quota to avoid huge bursts after idle time
        // libtorrent caps at 3 * limit usually
        long maxQuota = SaturatingTriple(limit);
        if (maxQuota > 0 && newTotalQuota > maxQuota)
        {
            // Atomically clamp to max using CompareExchange
            long current;
            do
            {
                current = Interlocked.Read(ref _quota);
                if (current <= maxQuota)
                {
                    break;
                }
            } while (Interlocked.CompareExchange(ref _quota, maxQuota, current) != current);
        }
    }

    public void UseQuota(int amount)
    {
        long limit = Interlocked.Read(ref _limit);
        if (limit == 0)
        {
            return;
        }

        // Note: Quota can go negative if multiple threads check-then-use simultaneously
        // This is acceptable - it represents temporary over-allocation that will be
        // corrected on the next UpdateQuota cycle. We only prevent extreme negative values.
        long newQuota = AddSaturating(ref _quota, -amount);

        // Prevent quota from going below -maxQuota (prevents unbounded debt)
        long minQuota = -SaturatingTriple(limit);
        if (newQuota < minQuota)
        {
            // Clamp to minimum using CompareExchange loop
            long current;
            do
            {
                current = Interlocked.Read(ref _quota);
                if (current >= minQuota)
                {
                    break;
                }
            } while (Interlocked.CompareExchange(ref _quota, minQuota, current) != current);
        }
    }

    private static long AddSaturating(ref long location, long value)
    {
        long current;
        long next;
        do
        {
            current = Interlocked.Read(ref location);
            if (value > 0 && current > long.MaxValue - value)
            {
                next = long.MaxValue;
            }
            else if (value < 0 && current < long.MinValue - value)
            {
                next = long.MinValue;
            }
            else
            {
                next = current + value;
            }
        }
        while (Interlocked.CompareExchange(ref location, next, current) != current);

        return next;
    }

    private static long SaturatingTriple(long value) => value > long.MaxValue / 3 ? long.MaxValue : value * 3;
}
