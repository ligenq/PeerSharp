namespace PeerSharp.Internals.Dht;

/// <summary>
/// BEP 51: the subset of stored info-hashes this node offers to indexers, and the interval over
/// which that subset stays put.
///
/// <para>
/// The subset has to be <em>stable</em>, not freshly drawn per query: an indexer is told how long
/// the answer will hold and is expected not to ask again before then, which is only a useful
/// contract if asking again would genuinely return the same thing. So the sample is computed once
/// and reused until the refresh interval elapses, and the reported interval counts down towards that
/// rotation rather than restating the full period - an indexer that arrives late in the cycle should
/// hear how long is actually left, not be told to wait all over again.
/// </para>
/// </summary>
internal sealed class DhtInfoHashSampler
{
    /// <summary>
    /// BEP 51: "The permissible range is between 0 and 21600 seconds (inclusive)."
    /// </summary>
    internal static readonly TimeSpan MaxRefreshInterval = TimeSpan.FromSeconds(21600);

    /// <summary>
    /// How many hashes one response carries. A DHT reply has to survive as a single unfragmented UDP
    /// datagram: 20 samples is 400 bytes, which leaves room for the compact node lists (up to 512
    /// bytes when both IPv4 and IPv6 are present) and still stays far below the path MTU. Nodes
    /// holding more than this advertise the surplus through <c>num</c>, which is what tells an
    /// indexer to come back for a different subset later.
    /// </summary>
    internal const int MaxSamplesPerResponse = 20;

    private const int InfoHashLength = 20;

    private readonly Lock _lock = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _refreshInterval;

    private byte[] _samples = [];
    private DateTimeOffset _computedAt = DateTimeOffset.MinValue;
    private bool _computed;

    public DhtInfoHashSampler(TimeProvider timeProvider, TimeSpan? refreshInterval = null)
    {
        _timeProvider = timeProvider;
        _refreshInterval = refreshInterval is { } interval && interval <= MaxRefreshInterval
            ? interval
            : MaxRefreshInterval;
    }

    /// <summary>
    /// Returns the current sample of <paramref name="storedInfoHashes"/>, rotating it first if the
    /// refresh interval has elapsed.
    /// </summary>
    /// <param name="storedInfoHashes">
    /// Hex-encoded keys of the info-hashes this node holds peers for, as stored in the peer cache.
    /// </param>
    public DhtInfoHashSample Take(ICollection<string> storedInfoHashes)
    {
        var now = _timeProvider.GetUtcNow();

        lock (_lock)
        {
            // An empty sample is not worth holding on to: a node that happened to be asked before it
            // had stored anything would otherwise keep answering "nothing" for the whole six hours,
            // long after it filled up.
            bool expired = !_computed || _samples.Length == 0 || now - _computedAt >= _refreshInterval;
            if (expired)
            {
                _samples = Draw(storedInfoHashes);
                _computedAt = now;
                _computed = true;
            }

            var elapsed = now - _computedAt;
            var remaining = _refreshInterval - elapsed;
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            return new DhtInfoHashSample(_samples, storedInfoHashes.Count, (int)remaining.TotalSeconds);
        }
    }

    /// <summary>
    /// Draws up to <see cref="MaxSamplesPerResponse"/> hashes uniformly at random, without
    /// replacement, and concatenates them.
    /// </summary>
    private static byte[] Draw(ICollection<string> storedInfoHashes)
    {
        if (storedInfoHashes.Count == 0)
        {
            return [];
        }

        // A snapshot, because the peer cache is mutated concurrently by announce_peer and by
        // maintenance expiry while we are drawing from it.
        var keys = storedInfoHashes.ToArray();

        // Partial shuffle: only as many positions as we intend to keep, so a node holding a large
        // store does not pay for a full permutation on every rotation.
        int wanted = Math.Min(MaxSamplesPerResponse, keys.Length);
        for (int i = 0; i < wanted; i++)
        {
            int j = Random.Shared.Next(i, keys.Length);
            (keys[i], keys[j]) = (keys[j], keys[i]);
        }

        var samples = new List<byte>(wanted * InfoHashLength);
        for (int i = 0; i < keys.Length && samples.Count < wanted * InfoHashLength; i++)
        {
            // The cache is keyed by hex string; anything that is not a v1 info-hash cannot go into a
            // BEP 51 sample, which is defined as a run of 20 byte hashes.
            if (keys[i].Length != InfoHashLength * 2 || !TryDecode(keys[i], out var hash))
            {
                continue;
            }

            samples.AddRange(hash);
        }

        return [.. samples];
    }

    private static bool TryDecode(string hex, out byte[] hash)
    {
        try
        {
            hash = Convert.FromHexString(hex);
            return hash.Length == InfoHashLength;
        }
        catch (FormatException)
        {
            hash = [];
            return false;
        }
    }
}

/// <summary>
/// One BEP 51 answer: the sampled hashes, how many the node actually holds, and how long this subset
/// remains current.
/// </summary>
/// <param name="Samples">Concatenated 20-byte info-hashes. Empty when the node stores none.</param>
/// <param name="Num">
/// Total info-hash keys in storage. Larger than the sample count means there is more to collect after
/// the interval expires.
/// </param>
/// <param name="IntervalSeconds">Seconds until the subset may change.</param>
internal readonly record struct DhtInfoHashSample(byte[] Samples, int Num, int IntervalSeconds);
