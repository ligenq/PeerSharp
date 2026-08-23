using PeerSharp.Internals.Peers;

namespace PeerSharp.Tests.Core.Peers;

/// <summary>
/// How deep a peer's request queue should be.
///
/// <para>
/// The case that matters most is the one the previous bandwidth-delay formula got wrong: a fast peer
/// on a link with almost no latency. Sizing from speed multiplied by round trip collapses to the
/// floor there, and the floor then caps the speed that would have justified a deeper queue. Sizing
/// from seconds of queued work has no such feedback, so that scenario has a test of its own below.
/// </para>
/// </summary>
public class PipelineDepthCalculatorTests
{
    private const int BlockSize = 16 * 1024;

    [Fact]
    public void NoSpeedAndNoEstimate_UsesConfiguredInitialClamped()
    {
        int depth = PipelineDepthCalculator.CalculateOptimal(
            speedBytesPerSec: 0,
            queueTimeSeconds: 3,
            estimatedBandwidthBytesPerSec: 0,
            initialPipelineDepth: 32);

        Assert.Equal(32, depth);
    }

    [Fact]
    public void NoSpeed_FallsBackToTheConfiguredEstimate()
    {
        // 10 MB/s for 3 seconds is 30 MB of work, which is 1920 blocks - past the ceiling.
        int depth = PipelineDepthCalculator.CalculateOptimal(
            speedBytesPerSec: 0,
            queueTimeSeconds: 3,
            estimatedBandwidthBytesPerSec: 10 * 1024 * 1024,
            initialPipelineDepth: 16);

        Assert.Equal(PipelineDepthCalculator.MaxPipeline, depth);
    }

    [Fact]
    public void ConfiguredInitialOutsideTheBoundsIsClamped()
    {
        Assert.Equal(
            PipelineDepthCalculator.MinPipeline,
            PipelineDepthCalculator.CalculateOptimal(0, 3, 0, initialPipelineDepth: 1));

        Assert.Equal(
            PipelineDepthCalculator.MaxPipeline,
            PipelineDepthCalculator.CalculateOptimal(0, 3, 0, initialPipelineDepth: 10_000));
    }

    [Fact]
    public void MeasuredSpeed_QueuesThatManySecondsOfWork()
    {
        const int speed = 1024 * 1024;

        int depth = PipelineDepthCalculator.CalculateOptimal(
            speedBytesPerSec: speed,
            queueTimeSeconds: 2,
            estimatedBandwidthBytesPerSec: 0,
            initialPipelineDepth: 16);

        Assert.Equal(speed * 2 / BlockSize, depth);
    }

    [Fact]
    public void AFastPeerOnANearZeroLatencyLinkStillGetsADeepQueue()
    {
        // The regression this replaced. Loopback against libtorrent's connection_tester measures a
        // round trip of about a millisecond, which the old speed-times-RTT formula turned into the
        // floor - eight blocks in flight against libtorrent's five hundred. Latency is not an input
        // here, so a peer serving 6 MB/s is queued as such however quickly it answers.
        int depth = PipelineDepthCalculator.CalculateOptimal(
            speedBytesPerSec: 6 * 1024 * 1024,
            queueTimeSeconds: PipelineDepthCalculator.DefaultQueueTimeSeconds,
            estimatedBandwidthBytesPerSec: 0,
            initialPipelineDepth: 16);

        Assert.Equal(PipelineDepthCalculator.MaxPipeline, depth);
    }

    [Fact]
    public void ASlowPeerIsNotGivenMoreThanItCanServe()
    {
        // The other half of the same contract: depth tracks the rate, so a peer trickling along does
        // not accumulate a queue it will never drain.
        int depth = PipelineDepthCalculator.CalculateOptimal(
            speedBytesPerSec: 32 * 1024,
            queueTimeSeconds: 3,
            estimatedBandwidthBytesPerSec: 0,
            initialPipelineDepth: 16);

        Assert.Equal(6, depth);
    }

    [Fact]
    public void AnInvalidQueueTimeFallsBackToTheDefault()
    {
        int explicitDefault = PipelineDepthCalculator.CalculateOptimal(
            1024 * 1024, PipelineDepthCalculator.DefaultQueueTimeSeconds, 0, 16);

        Assert.Equal(explicitDefault, PipelineDepthCalculator.CalculateOptimal(1024 * 1024, 0, 0, 16));
        Assert.Equal(explicitDefault, PipelineDepthCalculator.CalculateOptimal(1024 * 1024, -5, 0, 16));
    }

    [Fact]
    public void VeryHighSpeedDoesNotOverflow()
    {
        int depth = PipelineDepthCalculator.CalculateOptimal(
            speedBytesPerSec: int.MaxValue,
            queueTimeSeconds: 3600,
            estimatedBandwidthBytesPerSec: 0,
            initialPipelineDepth: 16);

        Assert.Equal(PipelineDepthCalculator.MaxPipeline, depth);
    }

    [Fact]
    public void Adapt_NoStrikes_LowRtt_ReturnsOptimal()
    {
        int adapted = PipelineDepthCalculator.Adapt(400, strikes: 0, rttMs: 50, minPipelineDepth: 4);
        Assert.Equal(400, adapted);
    }

    [Fact]
    public void Adapt_EachStrikeCostsATenthOfTheCeiling()
    {
        int perStrike = PipelineDepthCalculator.MaxPipeline / 10;
        int adapted = PipelineDepthCalculator.Adapt(400, strikes: 2, rttMs: 50, minPipelineDepth: 4);

        Assert.Equal(400 - (2 * perStrike), adapted);
    }

    [Fact]
    public void Adapt_StrikesClampedToMinFloor()
    {
        int adapted = PipelineDepthCalculator.Adapt(400, strikes: 100, rttMs: 50, minPipelineDepth: 4);
        Assert.Equal(4, adapted);
    }

    [Fact]
    public void Adapt_HighRttHalvesDepth()
    {
        int adapted = PipelineDepthCalculator.Adapt(400, strikes: 0, rttMs: 800, minPipelineDepth: 4);
        Assert.Equal(200, adapted);
    }

    [Fact]
    public void Adapt_StrikesAndHighRttApplyTogether()
    {
        int perStrike = PipelineDepthCalculator.MaxPipeline / 10;
        int adapted = PipelineDepthCalculator.Adapt(400, strikes: 1, rttMs: 1000, minPipelineDepth: 4);

        Assert.Equal((400 - perStrike) / 2, adapted);
    }
}
