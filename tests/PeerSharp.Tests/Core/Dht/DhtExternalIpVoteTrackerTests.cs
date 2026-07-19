using PeerSharp.Internals.Dht;
using System.Net;

namespace PeerSharp.Tests.Core.Dht;

public class DhtExternalIpVoteTrackerTests
{
    [Fact]
    public void ProcessReport_InvalidLength_IsIgnored()
    {
        var tracker = new DhtExternalIpVoteTracker(requiredVotes: 3);

        var result = tracker.ProcessReport([1, 2, 3]);

        Assert.Equal(DhtExternalIpVoteStatus.Ignored, result.Status);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("192.168.1.10")]
    public void ProcessReport_NonPublicAddress_IsIgnored(string address)
    {
        var tracker = new DhtExternalIpVoteTracker(requiredVotes: 3);

        var result = tracker.ProcessReport(IPAddress.Parse(address).GetAddressBytes());

        Assert.Equal(DhtExternalIpVoteStatus.Ignored, result.Status);
    }

    [Fact]
    public void ProcessReport_FirstPublicAddress_StartsVote()
    {
        var tracker = new DhtExternalIpVoteTracker(requiredVotes: 3);
        var ip = IPAddress.Parse("203.0.113.10");

        var result = tracker.ProcessReport(ip.GetAddressBytes());

        Assert.Equal(DhtExternalIpVoteStatus.FirstReport, result.Status);
        Assert.Equal(ip, result.Address);
        Assert.Equal(1, result.Votes);
        Assert.Equal(3, result.RequiredVotes);
    }

    [Fact]
    public void ProcessReport_SameAddress_ReachesConfirmationThresholdOnce()
    {
        var tracker = new DhtExternalIpVoteTracker(requiredVotes: 3);
        byte[] ip = IPAddress.Parse("203.0.113.10").GetAddressBytes();

        Assert.Equal(DhtExternalIpVoteStatus.FirstReport, tracker.ProcessReport(ip).Status);
        Assert.Equal(DhtExternalIpVoteStatus.Progress, tracker.ProcessReport(ip).Status);
        var confirmed = tracker.ProcessReport(ip);
        var alreadyConfirmed = tracker.ProcessReport(ip);

        Assert.Equal(DhtExternalIpVoteStatus.Confirmed, confirmed.Status);
        Assert.Equal(3, confirmed.Votes);
        Assert.Equal(DhtExternalIpVoteStatus.AlreadyConfirmed, alreadyConfirmed.Status);
        Assert.Equal(3, alreadyConfirmed.Votes);
    }

    [Fact]
    public void ProcessReport_DifferentAddress_ResetsVotes()
    {
        var tracker = new DhtExternalIpVoteTracker(requiredVotes: 3);
        byte[] first = IPAddress.Parse("203.0.113.10").GetAddressBytes();
        var secondIp = IPAddress.Parse("198.51.100.20");

        tracker.ProcessReport(first);
        tracker.ProcessReport(first);
        var changed = tracker.ProcessReport(secondIp.GetAddressBytes());

        Assert.Equal(DhtExternalIpVoteStatus.Changed, changed.Status);
        Assert.Equal(secondIp, changed.Address);
        Assert.Equal(1, changed.Votes);
    }

    [Fact]
    public void ProcessReport_Ipv6Address_CanBeConfirmed()
    {
        var tracker = new DhtExternalIpVoteTracker(requiredVotes: 2);
        var ip = IPAddress.Parse("2001:db8::1");

        tracker.ProcessReport(ip.GetAddressBytes());
        var confirmed = tracker.ProcessReport(ip.GetAddressBytes());

        Assert.Equal(DhtExternalIpVoteStatus.Confirmed, confirmed.Status);
        Assert.Equal(ip, confirmed.Address);
    }
}
