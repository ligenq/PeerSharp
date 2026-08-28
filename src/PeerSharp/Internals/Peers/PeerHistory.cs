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
    WebTorrent = 6,
    Unknown = 7,

    /// <summary>Added by the application, rather than discovered.</summary>
    Manual = 8
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

    /// <summary>
    /// Whether a connection we made to this endpoint was accepted, which is the only thing that
    /// confirms it can be dialled. A peer connecting to us proves the peer is reachable, not that the
    /// address it happened to dial from is - see <see cref="IsListenAddress"/>.
    /// </summary>
    public bool IsConnectable { get; set; }

    /// <summary>
    /// Whether this endpoint is where the peer accepts connections, rather than a source port we
    /// merely observed one arrive from.
    ///
    /// <para>
    /// True for everything a peer source hands us - trackers, the DHT, PEX, resume data - because
    /// those are all listening addresses by definition. False only for the entry created from an
    /// incoming connection, whose port is ephemeral and belongs to that one connection: nobody can
    /// dial it, so it must not be gossiped onward or offered as a candidate.
    /// </para>
    /// </summary>
    public bool IsListenAddress { get; set; } = true;

    /// <summary>
    /// Whether the peer is currently upload-only (a seed). May be hearsay: BEP 11 carries a seed flag,
    /// so this can be another peer's claim rather than something observed here. Good enough to rank a
    /// candidate by, which is all it is used for.
    /// </summary>
    public bool IsSeed { get; set; }

    /// <summary>
    /// Whether this client saw the peer say it has everything, over a connection of its own - a full
    /// bitfield or BEP 6 have_all, not a PEX flag.
    ///
    /// <para>
    /// Separate from <see cref="IsSeed"/> because it carries a heavier consequence: while this torrent
    /// is also complete the peer is not dialled at all, and a peer nobody dials cannot correct the
    /// record. Acting on hearsay there would let one peer's PEX message, mistaken or malicious, flag a
    /// whole swarm as seeds and quietly stop this client connecting to any of it.
    /// </para>
    /// </summary>
    public bool SeedConfirmed { get; set; }

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

    /// <summary>
    /// Whether the next outgoing connection to this peer should offer encryption.
    ///
    /// <para>
    /// Encryption support cannot be discovered without trying, and a peer that refuses one form may
    /// accept the other, so the choice alternates across attempts and is remembered here rather than
    /// being retried inside a single attempt. libtorrent does the same thing with its per-peer
    /// <c>pe_support</c> flag: it flips the flag before dialling and flips it back only when the
    /// handshake completes, so a peer that keeps failing keeps alternating, and one that works stays
    /// on whatever worked.
    /// </para>
    ///
    /// <para>
    /// The alternative - reconnecting immediately in plaintext when a peer hangs up mid-handshake -
    /// was measured against a live swarm and failed 72 times out of 77. Peers hang up for reasons that
    /// have nothing to do with encryption, and dialling straight back meets the same reason again while
    /// costing the peer a second connection.
    /// </para>
    /// </summary>
    public bool OfferEncryptionNext { get; set; } = true;

    /// <summary>Consecutive outgoing handshakes to this peer that ended without a reply.</summary>
    public int HandshakeFailureCount { get; set; }

    /// <summary>
    /// How many times the backoff has been waived for this peer to let it be tried again promptly
    /// with the other encryption choice.
    /// </summary>
    /// <remarks>
    /// libtorrent's <c>fast_reconnects</c>, and bounded the same way. It rewinds a peer's reconnect
    /// clock when it starts an encrypted handshake - "if this fails, we need to reconnect fast" - and
    /// stops honouring that past the second time, so a peer that answers neither costs two extra
    /// dials rather than an unbounded stream of them.
    /// </remarks>
    public int FastReconnects { get; set; }

    /// <summary>
    /// Records that a handshake completed, and settles the encryption choice on whatever worked.
    /// </summary>
    public void RegisterHandshakeSuccess(bool wasEncrypted)
    {
        HandshakeFailureCount = 0;
        OfferEncryptionNext = wasEncrypted;
    }

    /// <summary>
    /// Records a handshake that produced no usable connection, and switches what we offer next time.
    /// </summary>
    public void RegisterHandshakeFailure()
    {
        HandshakeFailureCount++;
        OfferEncryptionNext = !OfferEncryptionNext;
    }

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
