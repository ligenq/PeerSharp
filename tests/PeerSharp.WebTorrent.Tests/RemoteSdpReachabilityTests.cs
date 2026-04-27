using System.Net;
using PeerSharp.WebTorrent.Utilities;

namespace PeerSharp.WebTorrent.Tests;

public class RemoteSdpReachabilityTests
{
    private static readonly IReadOnlyList<LocalSubnet> NoLocalSubnets = Array.Empty<LocalSubnet>();

    [Fact]
    public void IsLikelyReachable_ReturnsTrue_WhenSrflxCandidatePresent()
    {
        string sdp = BuildSdp(
            "a=candidate:1 1 udp 2122260223 192.168.1.10 49152 typ host generation 0",
            "a=candidate:2 1 udp 1686052607 203.0.113.5 49152 typ srflx raddr 192.168.1.10 rport 49152 generation 0",
            "a=end-of-candidates");

        Assert.True(RemoteSdpReachability.IsLikelyReachable(sdp, NoLocalSubnets));
    }

    [Fact]
    public void IsLikelyReachable_ReturnsTrue_WhenRelayCandidatePresent()
    {
        string sdp = BuildSdp(
            "a=candidate:1 1 udp 2122260223 10.0.0.4 49152 typ host generation 0",
            "a=candidate:2 1 udp 41885439 198.51.100.7 49152 typ relay raddr 0.0.0.0 rport 0 generation 0",
            "a=end-of-candidates");

        Assert.True(RemoteSdpReachability.IsLikelyReachable(sdp, NoLocalSubnets));
    }

    [Fact]
    public void IsLikelyReachable_ReturnsTrue_WhenPublicHostCandidatePresent()
    {
        string sdp = BuildSdp(
            "a=candidate:1 1 udp 2122260223 198.51.100.42 49152 typ host generation 0",
            "a=end-of-candidates");

        Assert.True(RemoteSdpReachability.IsLikelyReachable(sdp, NoLocalSubnets));
    }

    [Fact]
    public void IsLikelyReachable_ReturnsFalse_WhenOnlyPrivateHostCandidatesAndEndOfCandidates()
    {
        string sdp = BuildSdp(
            "a=candidate:1 1 udp 2122260223 172.18.0.3 49152 typ host generation 0",
            "a=candidate:2 1 udp 2122260222 172.18.0.3 49153 typ host generation 0",
            "a=end-of-candidates");

        Assert.False(RemoteSdpReachability.IsLikelyReachable(sdp, NoLocalSubnets));
    }

    [Fact]
    public void IsLikelyReachable_ReturnsTrue_WhenPrivateHostCandidatesButNoEndOfCandidates_TrickleIce()
    {
        string sdp = BuildSdp(
            "a=candidate:1 1 udp 2122260223 172.18.0.3 49152 typ host generation 0");

        Assert.True(RemoteSdpReachability.IsLikelyReachable(sdp, NoLocalSubnets));
    }

    [Fact]
    public void IsLikelyReachable_ReturnsTrue_WhenRemoteHostShareLocalSubnet()
    {
        string sdp = BuildSdp(
            "a=candidate:1 1 udp 2122260223 192.168.1.50 49152 typ host generation 0",
            "a=end-of-candidates");

        var subnets = new[]
        {
            new LocalSubnet(IPAddress.Parse("192.168.1.10"), IPAddress.Parse("255.255.255.0"))
        };

        Assert.True(RemoteSdpReachability.IsLikelyReachable(sdp, subnets));
    }

    [Fact]
    public void IsLikelyReachable_ReturnsFalse_WhenPrivateHostsOutsideLocalSubnet()
    {
        string sdp = BuildSdp(
            "a=candidate:1 1 udp 2122260223 10.42.0.5 49152 typ host generation 0",
            "a=end-of-candidates");

        var subnets = new[]
        {
            new LocalSubnet(IPAddress.Parse("192.168.1.10"), IPAddress.Parse("255.255.255.0"))
        };

        Assert.False(RemoteSdpReachability.IsLikelyReachable(sdp, subnets));
    }

    [Fact]
    public void IsLikelyReachable_ReturnsTrue_WhenSdpHasNoCandidatesAndEndOfCandidates()
    {
        // No candidate lines means there's nothing for us to reject — defer to the ICE agent.
        string sdp = BuildSdp("a=end-of-candidates");

        Assert.True(RemoteSdpReachability.IsLikelyReachable(sdp, NoLocalSubnets));
    }

    [Fact]
    public void IsLikelyReachable_ReturnsTrue_WhenSdpIsEmpty()
    {
        Assert.True(RemoteSdpReachability.IsLikelyReachable(string.Empty, NoLocalSubnets));
    }

    [Fact]
    public void IsLikelyReachable_ReturnsTrue_WhenMdnsHostnameCandidate()
    {
        // Hostname candidates can't be parsed as IPAddress and are skipped by the address check —
        // hostAddresses ends up empty, so reachability falls through to the "nothing to reject" path.
        string sdp = BuildSdp(
            "a=candidate:1 1 udp 2122260223 abcd-1234.local 49152 typ host generation 0",
            "a=end-of-candidates");

        Assert.True(RemoteSdpReachability.IsLikelyReachable(sdp, NoLocalSubnets));
    }

    [Fact]
    public void IsLikelyReachable_RecognisesCgnatRangeAsPrivate()
    {
        string sdp = BuildSdp(
            "a=candidate:1 1 udp 2122260223 100.64.5.6 49152 typ host generation 0",
            "a=end-of-candidates");

        Assert.False(RemoteSdpReachability.IsLikelyReachable(sdp, NoLocalSubnets));
    }

    [Fact]
    public void IsLikelyReachable_RecognisesLinkLocalRangeAsPrivate()
    {
        string sdp = BuildSdp(
            "a=candidate:1 1 udp 2122260223 169.254.10.10 49152 typ host generation 0",
            "a=end-of-candidates");

        Assert.False(RemoteSdpReachability.IsLikelyReachable(sdp, NoLocalSubnets));
    }

    [Fact]
    public void IsLikelyReachable_ReturnsFalse_WhenOnlyIpv6HostCandidatesWithEndOfCandidates()
    {
        // IPv6 host literals get stripped by FilterUnsupportedIceCandidates at send/receive time,
        // so an IPv6-only peer with end-of-candidates leaves nothing for ICE to try — short-circuit.
        string sdp = BuildSdp(
            "a=candidate:1 1 udp 2122260223 fe80::1 49152 typ host generation 0",
            "a=candidate:2 1 udp 2122260222 2001:db8::1 49153 typ host generation 0",
            "a=end-of-candidates");

        Assert.False(RemoteSdpReachability.IsLikelyReachable(sdp, NoLocalSubnets));
    }

    [Fact]
    public void IsLikelyReachable_ReturnsTrue_WhenIpv6HostButAlsoSrflx()
    {
        // srflx remains a viable path even if every host is IPv6.
        string sdp = BuildSdp(
            "a=candidate:1 1 udp 2122260223 fe80::1 49152 typ host generation 0",
            "a=candidate:2 1 udp 1686052607 203.0.113.5 49152 typ srflx raddr fe80::1 rport 49152 generation 0",
            "a=end-of-candidates");

        Assert.True(RemoteSdpReachability.IsLikelyReachable(sdp, NoLocalSubnets));
    }

    [Fact]
    public void EnumerateLocalSubnets_DoesNotThrow()
    {
        var subnets = RemoteSdpReachability.EnumerateLocalSubnets();
        Assert.NotNull(subnets);
    }

    private static string BuildSdp(params string[] candidateLines)
    {
        var lines = new List<string>
        {
            "v=0",
            "o=- 0 0 IN IP4 127.0.0.1",
            "s=-",
            "t=0 0",
            "m=application 9 UDP/DTLS/SCTP webrtc-datachannel",
            "c=IN IP4 0.0.0.0",
            "a=ice-ufrag:abcd",
            "a=ice-pwd:0123456789abcdef0123456789",
        };
        lines.AddRange(candidateLines);
        return string.Join("\r\n", lines) + "\r\n";
    }
}
