namespace PeerSharp.Internals.Network;

/// <summary>
/// Backoff policy for the UDP receive loop. Isolated single errors (ICMP port unreachable,
/// connection resets) are common and harmless, so the first failures cost nothing; a socket
/// that keeps failing backs off exponentially so the loop cannot hot-spin and flood the log.
/// </summary>
internal static class UdpReceiveBackoff
{
    public const int MaxDelayMs = 1000;

    /// <summary>
    /// Delay to apply after the given number of consecutive receive failures.
    /// Returns 0 for the first two failures, then doubles from 10ms up to <see cref="MaxDelayMs"/>.
    /// </summary>
    public static int ComputeDelayMs(int consecutiveFailures)
    {
        if (consecutiveFailures <= 2)
        {
            return 0;
        }

        int exponent = Math.Min(consecutiveFailures - 3, 10);
        return Math.Min(MaxDelayMs, 10 << exponent);
    }
}
