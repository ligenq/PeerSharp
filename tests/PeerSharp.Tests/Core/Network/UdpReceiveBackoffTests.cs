using PeerSharp.Internals.Network;

namespace PeerSharp.Tests.Core.Network;

/// <summary>
/// The UDP receive loop must not hot-spin on a persistently failing socket, while single
/// transient errors (ICMP port unreachable, connection resets) must stay free.
/// </summary>
public class UdpReceiveBackoffTests
{
    [Fact]
    public void FirstTwoFailures_HaveNoDelay()
    {
        Assert.Equal(0, UdpReceiveBackoff.ComputeDelayMs(1));
        Assert.Equal(0, UdpReceiveBackoff.ComputeDelayMs(2));
    }

    [Fact]
    public void RepeatedFailures_BackOffExponentially()
    {
        Assert.Equal(10, UdpReceiveBackoff.ComputeDelayMs(3));
        Assert.Equal(20, UdpReceiveBackoff.ComputeDelayMs(4));
        Assert.Equal(40, UdpReceiveBackoff.ComputeDelayMs(5));
    }

    [Fact]
    public void Delay_IsMonotonicallyNonDecreasing()
    {
        int previous = 0;
        for (int failures = 1; failures <= 100; failures++)
        {
            int delay = UdpReceiveBackoff.ComputeDelayMs(failures);
            Assert.True(delay >= previous, $"Delay decreased at {failures} failures: {delay} < {previous}");
            previous = delay;
        }
    }

    [Fact]
    public void Delay_IsCapped()
    {
        Assert.Equal(UdpReceiveBackoff.MaxDelayMs, UdpReceiveBackoff.ComputeDelayMs(1000));
        Assert.Equal(UdpReceiveBackoff.MaxDelayMs, UdpReceiveBackoff.ComputeDelayMs(int.MaxValue));
    }
}
