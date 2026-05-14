namespace PeerSharp.Internals.Peers;

internal static class PipelineDepthCalculator
{
    private const int BlockSize = 16 * 1024;
    public const int MinPipeline = 8;
    public const int MaxPipeline = 128;

    /// <summary>
    /// Calculates the optimal pipeline depth using the bandwidth-delay product.
    /// When measured speed/RTT are unknown, falls back to estimated values, then
    /// to the configured initial pipeline depth.
    /// </summary>
    public static int CalculateOptimal(
        int speedBytesPerSec,
        int rttMs,
        int estimatedBandwidthBytesPerSec,
        int estimatedRttMs,
        int initialPipelineDepth)
    {
        if (speedBytesPerSec <= 0 || rttMs <= 0)
        {
            if (estimatedBandwidthBytesPerSec > 0 && estimatedRttMs > 0)
            {
                return BandwidthDelayDepth(estimatedBandwidthBytesPerSec, estimatedRttMs);
            }

            return Math.Clamp(initialPipelineDepth, MinPipeline, MaxPipeline);
        }

        return BandwidthDelayDepth(speedBytesPerSec, rttMs);
    }

    /// <summary>
    /// Calculates the optimal pipeline depth for a given RTT, when only the
    /// current speed is known. Used during RTT change logging.
    /// </summary>
    public static int CalculateOptimalForRtt(int speedBytesPerSec, int rttMs)
    {
        if (speedBytesPerSec <= 0)
        {
            return MinPipeline;
        }

        return BandwidthDelayDepth(speedBytesPerSec, rttMs);
    }

    /// <summary>
    /// Adapts the optimal pipeline depth for observed peer reliability.
    /// Each strike removes 10 from the optimal depth (clamped at MinPipelineDepth),
    /// and an RTT >= 800ms halves the depth.
    /// </summary>
    public static int Adapt(int optimalDepth, int strikes, int rttMs, int minPipelineDepth)
    {
        int depth = optimalDepth;
        if (strikes > 0)
        {
            depth = Math.Max(minPipelineDepth, depth - (strikes * 10));
        }

        if (rttMs >= 800)
        {
            depth = Math.Max(minPipelineDepth, depth / 2);
        }

        return depth;
    }

    private static int BandwidthDelayDepth(int speedBytesPerSec, int rttMs)
    {
        long bytesInFlight = (long)speedBytesPerSec * rttMs / 1000;
        long pipelineLong = bytesInFlight * 3 / 2 / BlockSize;
        return (int)Math.Clamp(pipelineLong, MinPipeline, MaxPipeline);
    }
}
