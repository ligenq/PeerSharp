using PeerSharp.Internals.Network;
using System.Net;

namespace PeerSharp.Tests.Core.Network;

public class PendingHandshakeLimiterTests
{
    private static readonly IPAddress A = IPAddress.Parse("192.0.2.1");
    private static readonly IPAddress B = IPAddress.Parse("192.0.2.2");

    [Fact]
    public void TryEnter_RefusesAnAddressAtItsLimit_ButNotOthers()
    {
        var limiter = new PendingHandshakeLimiter(maxTotal: 10, maxPerAddress: 2);

        Assert.True(limiter.TryEnter(A));
        Assert.True(limiter.TryEnter(A.MapToIPv6()));
        Assert.False(limiter.TryEnter(A));
        Assert.True(limiter.TryEnter(B));

        limiter.Exit(A.MapToIPv6());
        Assert.True(limiter.TryEnter(A));
        Assert.Equal(3, limiter.Pending);
    }

    [Fact]
    public void TryEnter_RefusesEveryoneAtTheTotalLimit_UntilOneExits()
    {
        var limiter = new PendingHandshakeLimiter(maxTotal: 2, maxPerAddress: 2);

        Assert.True(limiter.TryEnter(A));
        Assert.True(limiter.TryEnter(null));
        Assert.False(limiter.TryEnter(B));

        limiter.Exit(null);
        Assert.True(limiter.TryEnter(B));
    }
}
