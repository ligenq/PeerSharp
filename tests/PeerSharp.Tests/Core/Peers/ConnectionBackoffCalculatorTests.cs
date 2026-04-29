using PeerSharp.Internals.Peers;

namespace PeerSharp.Tests.Core.Peers;

public class ConnectionBackoffCalculatorTests
{
    [Fact]
    public void Calculate_FirstFailure_UsesBaseDelay()
    {
        var delay = ConnectionBackoffCalculator.Calculate(
            fruitlessConnectionCount: 1,
            baseSeconds: 5,
            maxSeconds: 300,
            jitterMs: 0);

        Assert.Equal(TimeSpan.FromSeconds(5), delay);
    }

    [Fact]
    public void Calculate_ZeroFailures_TreatedAsFirstFailure()
    {
        var delay = ConnectionBackoffCalculator.Calculate(
            fruitlessConnectionCount: 0,
            baseSeconds: 5,
            maxSeconds: 300,
            jitterMs: 0);

        Assert.Equal(TimeSpan.FromSeconds(5), delay);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 40)]
    [InlineData(5, 80)]
    [InlineData(6, 160)]
    [InlineData(7, 320)]
    public void Calculate_DoublesDelayUpToCap(int fruitlessCount, int expectedSeconds)
    {
        var delay = ConnectionBackoffCalculator.Calculate(
            fruitlessConnectionCount: fruitlessCount,
            baseSeconds: 5,
            maxSeconds: 600,
            jitterMs: 0);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void Calculate_CapsAtMaxSeconds()
    {
        var delay = ConnectionBackoffCalculator.Calculate(
            fruitlessConnectionCount: 100,
            baseSeconds: 5,
            maxSeconds: 60,
            jitterMs: 0);

        Assert.Equal(TimeSpan.FromSeconds(60), delay);
    }

    [Fact]
    public void Calculate_BaseLargerThanMax_UsesBaseAsFloorForMax()
    {
        // Defensive: misconfigured max < base should still produce a delay >= base
        var delay = ConnectionBackoffCalculator.Calculate(
            fruitlessConnectionCount: 1,
            baseSeconds: 30,
            maxSeconds: 5,
            jitterMs: 0);

        Assert.Equal(TimeSpan.FromSeconds(30), delay);
    }

    [Fact]
    public void Calculate_NonPositiveBase_TreatedAsOne()
    {
        var delay = ConnectionBackoffCalculator.Calculate(
            fruitlessConnectionCount: 1,
            baseSeconds: 0,
            maxSeconds: 300,
            jitterMs: 0);

        Assert.Equal(TimeSpan.FromSeconds(1), delay);
    }

    [Fact]
    public void Calculate_AppliesJitterFromInjectedPicker()
    {
        var delay = ConnectionBackoffCalculator.Calculate(
            fruitlessConnectionCount: 1,
            baseSeconds: 5,
            maxSeconds: 300,
            jitterMs: 200,
            jitterPicker: max =>
            {
                Assert.Equal(201, max);
                return 150;
            });

        Assert.Equal(TimeSpan.FromSeconds(5) + TimeSpan.FromMilliseconds(150), delay);
    }

    [Fact]
    public void Calculate_NegativeJitter_TreatedAsZero()
    {
        var delay = ConnectionBackoffCalculator.Calculate(
            fruitlessConnectionCount: 1,
            baseSeconds: 5,
            maxSeconds: 300,
            jitterMs: -50);

        Assert.Equal(TimeSpan.FromSeconds(5), delay);
    }
}
