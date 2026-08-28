namespace PeerSharp.Internals.Peers;

/// <summary>
/// How many block requests to keep outstanding on one peer.
/// </summary>
/// <remarks>
/// <para>
/// The question is "how much work should this peer have queued", and the answer is measured in time
/// rather than in bytes: enough requests that the peer stays busy for the next few seconds. A peer
/// that runs its queue dry sends nothing until the next request arrives, so the queue has to cover
/// the whole round trip plus whatever the peer's own scheduler adds.
/// </para>
/// <para>
/// This was previously a bandwidth-delay product - speed multiplied by measured RTT. That is the
/// right formula for a network pipe and the wrong one here, because it feeds back on itself: the
/// depth it produces limits the speed, and the reduced speed then justifies the depth. On a link
/// where the RTT rounds to zero the product collapses to the floor and stays there no matter how
/// fast the peer actually is. Measured against libtorrent's <c>connection_tester</c> over loopback
/// that floor was eight blocks where libtorrent queued five hundred, and PeerSharp ran at a tenth of
/// the rate while using a fifth of the CPU - idle, waiting for requests it had not sent.
/// </para>
/// <para>
/// Time-based sizing has no such feedback: a peer serving 6 MB/s earns a queue of about 1100 blocks
/// (capped below that), and the estimate rises with the rate rather than chasing it down.
/// </para>
/// </remarks>
internal static class PipelineDepthCalculator
{
    private const int BlockSize = 16 * 1024;

    /// <summary>The floor, matching libtorrent's <c>min_request_queue</c>.</summary>
    public const int MinPipeline = 2;

    /// <summary>
    /// The ceiling, matching libtorrent's <c>max_out_request_queue</c> default. Outstanding requests
    /// cost a small record each on this side; the data itself is buffered by the sender.
    /// </summary>
    public const int MaxPipeline = 500;

    /// <summary>
    /// Seconds of work to keep queued on a peer, matching libtorrent's <c>request_queue_time</c>.
    /// </summary>
    public const int DefaultQueueTimeSeconds = 3;

    /// <summary>
    /// Calculates the depth from the peer's measured download rate. Falls back to the configured
    /// estimate, and then to the configured initial depth, while the rate is still unknown.
    /// </summary>
    public static int CalculateOptimal(
        int speedBytesPerSec,
        int queueTimeSeconds,
        int estimatedBandwidthBytesPerSec,
        int initialPipelineDepth)
    {
        int queueTime = queueTimeSeconds > 0 ? queueTimeSeconds : DefaultQueueTimeSeconds;

        if (speedBytesPerSec > 0)
        {
            return QueueTimeDepth(speedBytesPerSec, queueTime);
        }

        // Nothing has arrived yet. Start from the configured estimate rather than the floor, so the
        // first seconds of a transfer are not spent ramping up from two blocks in flight.
        if (estimatedBandwidthBytesPerSec > 0)
        {
            return QueueTimeDepth(estimatedBandwidthBytesPerSec, queueTime);
        }

        return Math.Clamp(initialPipelineDepth, MinPipeline, MaxPipeline);
    }

    /// <summary>
    /// Adapts the depth for observed peer reliability. Each strike removes a tenth of the ceiling,
    /// and a peer whose round trip has collapsed to a crawl gets half.
    /// </summary>
    public static int Adapt(int optimalDepth, int strikes, int rttMs, int minPipelineDepth)
    {
        int depth = optimalDepth;
        if (strikes > 0)
        {
            depth = Math.Max(minPipelineDepth, depth - (strikes * (MaxPipeline / 10)));
        }

        // Not the sizing input any more, but still a symptom: a peer this slow to answer will not
        // drain a long queue, and the requests only delay giving up on it.
        if (rttMs >= 800)
        {
            depth = Math.Max(minPipelineDepth, depth / 2);
        }

        return depth;
    }

    private static int QueueTimeDepth(int speedBytesPerSec, int queueTimeSeconds)
    {
        long depth = (long)speedBytesPerSec * queueTimeSeconds / BlockSize;
        return (int)Math.Clamp(depth, MinPipeline, MaxPipeline);
    }
}
