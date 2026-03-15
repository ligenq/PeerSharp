using System.Net;

namespace PeerSharp.Internals.Peers;

internal enum PeerSourceKind
{
    Tracker = 0,
    Lpd = 1,
    Dht = 2,
    Pex = 3,
    Resume = 4,
    Ltep = 5,
    Unknown = 7
}

/// <summary>
/// Tracks the history of a peer endpoint to enable libtransmission-style prioritization.
/// </summary>
internal sealed class PeerHistory
{
    /// <summary>Best (most trusted) source rank seen for this peer.</summary>
    public int BestSourceRank { get; set; } = int.MaxValue;

    /// <summary>The endpoint of the peer.</summary>
    public required IPEndPoint EndPoint { get; init; }

    /// <summary>Whether we have ever successfully exchanged piece data with this peer.</summary>
    public bool ExchangedData { get; set; }

    /// <summary>Number of consecutive failed connection attempts.</summary>
    public int FruitlessConnectionCount { get; set; }

    /// <summary>Whether the peer is confirmed to be connectable (accepted an inbound connection).</summary>
    public bool IsConnectable { get; set; }

    /// <summary>Whether the peer is currently upload-only (a seed).</summary>
    public bool IsSeed { get; set; }

    /// <summary>When the last connection attempt was made.</summary>
    public DateTimeOffset LastAttempt { get; set; } = DateTimeOffset.MinValue;

    /// <summary>Earliest time we should attempt to connect to this peer again.</summary>
    public DateTimeOffset NextConnectAttempt { get; set; } = DateTimeOffset.MinValue;

    /// <summary>Most recent uTP failure time.</summary>
    public DateTimeOffset LastUtpFailure { get; set; } = DateTimeOffset.MinValue;

    /// <summary>Most recent time we penalized uTP for slowness.</summary>
    public DateTimeOffset LastUtpPenalty { get; set; } = DateTimeOffset.MinValue;

    /// <summary>Most recent uTP success time.</summary>
    public DateTimeOffset LastUtpSuccess { get; set; } = DateTimeOffset.MinValue;

    /// <summary>Consecutive uTP failures for this peer.</summary>
    public int UtpFailureCount { get; set; }

    /// <summary>Whether we have a strong hint that this peer supports uTP.</summary>
    public bool UtpHinted { get; set; }

    /// <summary>Earliest time we should consider using uTP for this peer again.</summary>
    public DateTimeOffset UtpPenaltyUntil { get; set; } = DateTimeOffset.MinValue;

    /// <summary>Whether uTP is known to be unsupported or failing for this peer.</summary>
    public bool UtpSupported { get; set; } = true;

    /// <summary>Calculates a score for this candidate. Lower is better (higher priority).</summary>
    public long GetScore(bool torrentIsSeeding, Priority torrentPriority, DateTimeOffset now)
    {
        // libtransmission inspired scoring
        long score = 0;

        // 1. Prefer peers we've exchanged piece data with, or never tried (1 bit)
        score <<= 1;
        if (FruitlessConnectionCount > 0 && !ExchangedData)
        {
            score |= 1;
        }

        // 2. Prefer peers we've exchanged data with (1 bit)
        score <<= 1;
        if (!ExchangedData)
        {
            score |= 1;
        }

        // 3. Prefer peers attempted least recently (32 bits)
        // Convert to seconds since epoch for comparison
        score <<= 32;
        score |= (long)((ulong)LastAttempt.ToUnixTimeSeconds() & 0xFFFFFFFFu);

        // 4. Prefer peers belonging to higher priority torrents (2 bits)
        score <<= 2;
        score |= EvaluatePriority(torrentPriority);

        // 5. Prefer downloading torrents over seeding (1 bit)
        score <<= 1;
        if (torrentIsSeeding)
        {
            score |= 1;
        }

        // 6. Prefer connectable peers (1 bit)
        score <<= 1;
        if (!IsConnectable)
        {
            score |= 1;
        }

        // 7. Prefer peers we might be able to upload to (1 bit)
        score <<= 1;
        if (IsSeed)
        {
            score |= 1;
        }

        // 8. Prefer peers from more trusted sources (4 bits)
        score <<= 4;
        score |= (uint)Math.Min(BestSourceRank, 15);

        return score;
    }

    public bool IsUtpAllowed(DateTimeOffset now)
    {
        return UtpSupported && now >= UtpPenaltyUntil;
    }

    public void RegisterUtpFailure(DateTimeOffset now, ConnectionSettings settings)
    {
        LastUtpFailure = now;
        UtpFailureCount++;

        int backoffPow = Math.Min(UtpFailureCount - 1, 6);
        int penaltySeconds = settings.UtpPenaltyBaseSeconds * (1 << backoffPow);
        if (penaltySeconds > settings.UtpPenaltyMaxSeconds)
        {
            penaltySeconds = settings.UtpPenaltyMaxSeconds;
        }
        var penaltyUntil = now.AddSeconds(penaltySeconds);
        if (penaltyUntil > UtpPenaltyUntil)
        {
            UtpPenaltyUntil = penaltyUntil;
        }

        if (UtpFailureCount >= settings.UtpFailureHardLimit)
        {
            UtpSupported = false;
        }
    }

    public bool RegisterUtpSlow(DateTimeOffset now, ConnectionSettings settings)
    {
        if (now - LastUtpPenalty < TimeSpan.FromSeconds(settings.UtpSlowPenaltyCooldownSeconds))
        {
            return false;
        }

        LastUtpPenalty = now;
        var slowUntil = now.AddSeconds(settings.UtpSlowPenaltySeconds);
        if (slowUntil > UtpPenaltyUntil)
        {
            UtpPenaltyUntil = slowUntil;
        }

        UtpFailureCount++;
        if (UtpFailureCount >= settings.UtpFailureHardLimit)
        {
            UtpSupported = false;
        }

        return true;
    }

    public void RegisterUtpSuccess(DateTimeOffset now)
    {
        UtpSupported = true;
        UtpFailureCount = 0;
        UtpPenaltyUntil = DateTimeOffset.MinValue;
        LastUtpSuccess = now;
        UtpHinted = true;
    }

    public void UpdateSource(PeerSourceKind source)
    {
        int rank = (int)source;
        if (rank < BestSourceRank)
        {
            BestSourceRank = rank;
        }
    }

    private static uint EvaluatePriority(Priority torrentPriority)
    {
        return torrentPriority switch
        {
            Priority.High => 0,
            Priority.Normal => 1,
            Priority.Low => 2,
            _ => 1
        };
    }
}
