namespace PeerSharp.Internals.Bandwidth;

internal interface IBandwidthUser
{
    string Name { get; }

    void AssignBandwidth(int amount);
}

internal class BandwidthChannel
{
    // Multiple threads can call UpdateQuota, UseQuota, ReturnQuota simultaneously
    private int _limit;

    private int _quota;
    private int _subQuota;

    public BandwidthChannel(TimeProvider timeProvider)
    {
        _limit = 0; // 0 = infinite
        _quota = 0;
    }

    public int AvailableQuota
    {
        get
        {
            int limit = Interlocked.CompareExchange(ref _limit, 0, 0);
            if (limit == 0)
            {
                return int.MaxValue;
            }

            return Interlocked.CompareExchange(ref _quota, 0, 0);
        }
    }

    public bool CanUse(int amount)
    {
        int limit = Interlocked.CompareExchange(ref _limit, 0, 0);
        if (limit == 0)
        {
            return true;
        }

        int quota = Interlocked.CompareExchange(ref _quota, 0, 0);
        return quota >= amount;
    }

    public int GetLimit()
    {
        return Interlocked.CompareExchange(ref _limit, 0, 0);
    }

    /// <summary>
    /// Returns unused bandwidth quota back to the channel.
    /// Thread-safe using Interlocked operations.
    /// </summary>
    public void ReturnQuota(int amount)
    {
        int limit = Interlocked.CompareExchange(ref _limit, 0, 0);
        if (limit == 0 || amount <= 0)
        {
            return;
        }

        int newQuota = Interlocked.Add(ref _quota, amount);

        // Cap to prevent quota from growing unboundedly
        int maxQuota = limit * 3;
        if (maxQuota > 0 && newQuota > maxQuota)
        {
            // Atomically clamp to max using CompareExchange loop
            int current;
            do
            {
                current = Interlocked.CompareExchange(ref _quota, 0, 0);
                if (current <= maxQuota)
                {
                    break;
                }
            } while (Interlocked.CompareExchange(ref _quota, maxQuota, current) != current);
        }
    }

    public void SetLimit(int limit)
    {
        Interlocked.Exchange(ref _limit, limit);
    }

    public void UpdateQuota(int dt)
    {
        int limit = Interlocked.CompareExchange(ref _limit, 0, 0);
        if (limit == 0)
        {
            return;
        }

        // Add to quota based on time passed and limit
        // limit is bytes/sec. dt is ms.
        // quota += limit * dt / 1000

        long newQuota = (long)limit * dt;
        int quotaDelta = (int)(newQuota / 1000);
        int subQuotaDelta = (int)(newQuota % 1000);

        int newSubQuota = Interlocked.Add(ref _subQuota, subQuotaDelta);

        // Handle overflow from subQuota to quota
        if (newSubQuota >= 1000)
        {
            // Atomically transfer overflow: subQuota -= 1000, quota += 1
            int actualSubQuota = Interlocked.Add(ref _subQuota, -1000);
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
        int newTotalQuota = Interlocked.Add(ref _quota, quotaDelta);

        // Cap quota to avoid huge bursts after idle time
        // libtorrent caps at 3 * limit usually
        int maxQuota = limit * 3;
        if (maxQuota > 0 && newTotalQuota > maxQuota)
        {
            // Atomically clamp to max using CompareExchange
            int current;
            do
            {
                current = Interlocked.CompareExchange(ref _quota, 0, 0);
                if (current <= maxQuota)
                {
                    break;
                }
            } while (Interlocked.CompareExchange(ref _quota, maxQuota, current) != current);
        }
    }

    public void UseQuota(int amount)
    {
        int limit = Interlocked.CompareExchange(ref _limit, 0, 0);
        if (limit == 0)
        {
            return;
        }

        // Note: Quota can go negative if multiple threads check-then-use simultaneously
        // This is acceptable - it represents temporary over-allocation that will be
        // corrected on the next UpdateQuota cycle. We only prevent extreme negative values.
        int newQuota = Interlocked.Add(ref _quota, -amount);

        // Prevent quota from going below -maxQuota (prevents unbounded debt)
        int minQuota = -(limit * 3);
        if (newQuota < minQuota)
        {
            // Clamp to minimum using CompareExchange loop
            int current;
            do
            {
                current = Interlocked.CompareExchange(ref _quota, 0, 0);
                if (current >= minQuota)
                {
                    break;
                }
            } while (Interlocked.CompareExchange(ref _quota, minQuota, current) != current);
        }
    }
}
