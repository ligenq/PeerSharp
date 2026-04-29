using PeerSharp.Internals.Peers;

namespace PeerSharp.Tests.Core.Peers;

public class PipelineDepthCalculatorTests
{
    [Fact]
    public void CalculateOptimal_NoSpeedAndNoEstimate_UsesConfiguredInitialClamped()
    {
        int depth = PipelineDepthCalculator.CalculateOptimal(
            speedBytesPerSec: 0,
            rttMs: 0,
            estimatedBandwidthBytesPerSec: 0,
            estimatedRttMs: 0,
            initialPipelineDepth: 32);

        Assert.Equal(32, depth);
    }

    [Fact]
    public void CalculateOptimal_NoSpeed_FallsBackToEstimate()
    {
        int depth = PipelineDepthCalculator.CalculateOptimal(
            speedBytesPerSec: 0,
            rttMs: 0,
            estimatedBandwidthBytesPerSec: 10 * 1024 * 1024,
            estimatedRttMs: 100,
            initialPipelineDepth: 16);

        // bytes_in_flight = 10MB/s * 100ms / 1000 = 1MB
        // depth = 1MB * 1.5 / 16KB = 96
        Assert.Equal(96, depth);
    }

    [Fact]
    public void CalculateOptimal_ConfiguredInitialBelowMin_ClampsToMin()
    {
        int depth = PipelineDepthCalculator.CalculateOptimal(0, 0, 0, 0, initialPipelineDepth: 1);
        Assert.Equal(PipelineDepthCalculator.MinPipeline, depth);
    }

    [Fact]
    public void CalculateOptimal_ConfiguredInitialAboveMax_ClampsToMax()
    {
        int depth = PipelineDepthCalculator.CalculateOptimal(0, 0, 0, 0, initialPipelineDepth: 1000);
        Assert.Equal(PipelineDepthCalculator.MaxPipeline, depth);
    }

    [Fact]
    public void CalculateOptimal_MeasuredSpeedAndRtt_UsesBandwidthDelayProduct()
    {
        // 5MB/s * 80ms = 400KB; *1.5/16KB = ~37
        int depth = PipelineDepthCalculator.CalculateOptimal(
            speedBytesPerSec: 5 * 1024 * 1024,
            rttMs: 80,
            estimatedBandwidthBytesPerSec: 0,
            estimatedRttMs: 0,
            initialPipelineDepth: 16);

        Assert.InRange(depth, 30, 45);
    }

    [Fact]
    public void CalculateOptimal_VeryHighSpeed_ClampsToMaxPipeline()
    {
        // 1GB/s * 1000ms is a huge number that would overflow naively.
        int depth = PipelineDepthCalculator.CalculateOptimal(
            speedBytesPerSec: int.MaxValue,
            rttMs: 1000,
            estimatedBandwidthBytesPerSec: 0,
            estimatedRttMs: 0,
            initialPipelineDepth: 16);

        Assert.Equal(PipelineDepthCalculator.MaxPipeline, depth);
    }

    [Fact]
    public void CalculateOptimalForRtt_ZeroSpeed_ReturnsMin()
    {
        Assert.Equal(PipelineDepthCalculator.MinPipeline,
            PipelineDepthCalculator.CalculateOptimalForRtt(0, 100));
    }

    [Fact]
    public void CalculateOptimalForRtt_HigherRttIncreasesDepth()
    {
        int low = PipelineDepthCalculator.CalculateOptimalForRtt(2 * 1024 * 1024, 50);
        int high = PipelineDepthCalculator.CalculateOptimalForRtt(2 * 1024 * 1024, 500);
        Assert.True(high > low);
    }

    [Fact]
    public void Adapt_NoStrikes_LowRtt_ReturnsOptimal()
    {
        int adapted = PipelineDepthCalculator.Adapt(40, strikes: 0, rttMs: 50, minPipelineDepth: 4);
        Assert.Equal(40, adapted);
    }

    [Fact]
    public void Adapt_StrikesReduceDepth()
    {
        int adapted = PipelineDepthCalculator.Adapt(40, strikes: 2, rttMs: 50, minPipelineDepth: 4);
        Assert.Equal(20, adapted);
    }

    [Fact]
    public void Adapt_StrikesClampedToMinFloor()
    {
        int adapted = PipelineDepthCalculator.Adapt(40, strikes: 100, rttMs: 50, minPipelineDepth: 4);
        Assert.Equal(4, adapted);
    }

    [Fact]
    public void Adapt_HighRttHalvesDepth()
    {
        int adapted = PipelineDepthCalculator.Adapt(40, strikes: 0, rttMs: 800, minPipelineDepth: 4);
        Assert.Equal(20, adapted);
    }

    [Fact]
    public void Adapt_StrikesAndHighRttApplyTogether()
    {
        // 40 - 1*10 = 30; then halved = 15
        int adapted = PipelineDepthCalculator.Adapt(40, strikes: 1, rttMs: 1000, minPipelineDepth: 4);
        Assert.Equal(15, adapted);
    }
}
