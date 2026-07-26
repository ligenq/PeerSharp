using PeerSharp.BEncoding;

namespace PeerSharp.Internals.Trackers;

/// <summary>
/// BEP 31: a tracker's own instruction for when to retry after a failure response.
///
/// Without this the client can only guess - see the circuit breaker in
/// <see cref="TrackerManager"/>, which doubles a 60 second base delay. A tracker that is down for
/// maintenance, or is rate limiting us, knows the real answer, and private trackers ban clients
/// that keep announcing through it.
/// </summary>
internal readonly record struct TrackerRetryHint
{
    private TrackerRetryHint(bool never, TimeSpan retryIn)
    {
        Never = never;
        RetryIn = retryIn;
    }

    /// <summary>
    /// True when the tracker asked us never to send this query again.
    /// </summary>
    public bool Never { get; }

    /// <summary>
    /// How long to wait before retrying. Meaningless when <see cref="Never"/> is set.
    /// </summary>
    public TimeSpan RetryIn { get; }

    public static TrackerRetryHint NeverRetry { get; } = new(true, TimeSpan.Zero);

    public static TrackerRetryHint After(TimeSpan retryIn) => new(false, retryIn);

    /// <summary>
    /// Reads the optional <c>retry in</c> key from a tracker failure response. The value is either
    /// a positive integer count of <em>minutes</em> or the string <c>never</c>; anything else
    /// (including a zero or negative count) is treated as absent so we fall back to our own backoff.
    /// </summary>
    public static TrackerRetryHint? TryParse(BDict dict)
    {
        var node = dict.Get("retry in");
        if (node == null)
        {
            return null;
        }

        // "never" is a bencoded string, so it arrives as BString rather than BNumber.
        if (node is BString text)
        {
            return string.Equals(text.Text, "never", StringComparison.OrdinalIgnoreCase)
                ? NeverRetry
                : null;
        }

        if (node is BNumber number && number.Value > 0)
        {
            // Guard the multiplication itself: minutes come off the wire as a long, and a hostile
            // value near long.MaxValue would overflow TimeSpan.FromMinutes. The caller clamps the
            // result to a sane ceiling anyway, so saturating here loses nothing.
            long minutes = Math.Min(number.Value, (long)TimeSpan.MaxValue.TotalMinutes);
            return After(TimeSpan.FromMinutes(minutes));
        }

        return null;
    }
}
