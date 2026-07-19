using PeerSharp.Internals.Dht;
using System.Net;

namespace PeerSharp.Tests.Core.Dht;

public class DhtCompactPeerCodecTests
{
    [Fact]
    public void EncodeParse_Ipv4Peers_RoundTripsCompactFormat()
    {
        var peer = new IPEndPoint(IPAddress.Parse("203.0.113.10"), 6881);

        var encoded = DhtCompactPeerCodec.Encode([peer], ipv6: false);
        var parsed = DhtCompactPeerCodec.Parse(encoded.Select(value => (ReadOnlyMemory<byte>)value), ipv6: false);

        byte[] value = Assert.Single(encoded);
        Assert.Equal(6, value.Length);
        Assert.Equal(new byte[] { 203, 0, 113, 10, 0x1A, 0xE1 }, value);
        Assert.Equal(peer, Assert.Single(parsed));
    }

    [Fact]
    public void EncodeParse_Ipv6Peers_RoundTripsCompactFormat()
    {
        var peer = new IPEndPoint(IPAddress.Parse("2001:db8::1"), 51413);

        var encoded = DhtCompactPeerCodec.Encode([peer], ipv6: true);
        var parsed = DhtCompactPeerCodec.Parse(encoded.Select(value => (ReadOnlyMemory<byte>)value), ipv6: true);

        Assert.Equal(18, Assert.Single(encoded).Length);
        Assert.Equal(peer, Assert.Single(parsed));
    }

    [Fact]
    public void Encode_FiltersByAddressFamily()
    {
        var ipv4 = new IPEndPoint(IPAddress.Parse("198.51.100.1"), 1000);
        var ipv6 = new IPEndPoint(IPAddress.Parse("2001:db8::2"), 2000);

        var encodedV4 = DhtCompactPeerCodec.Encode([ipv4, ipv6], ipv6: false);
        var encodedV6 = DhtCompactPeerCodec.Encode([ipv4, ipv6], ipv6: true);

        Assert.Equal(ipv4, Assert.Single(DhtCompactPeerCodec.Parse(encodedV4.Select(value => (ReadOnlyMemory<byte>)value), ipv6: false)));
        Assert.Equal(ipv6, Assert.Single(DhtCompactPeerCodec.Parse(encodedV6.Select(value => (ReadOnlyMemory<byte>)value), ipv6: true)));
    }

    [Fact]
    public void Encode_StopsAtMaxPeersAfterMatchingFamily()
    {
        var peers = Enumerable.Range(0, 5)
            .Select(i => new IPEndPoint(IPAddress.Parse($"203.0.113.{i + 1}"), 6000 + i))
            .ToArray();

        var encoded = DhtCompactPeerCodec.Encode(peers, ipv6: false, maxPeers: 2);
        var parsed = DhtCompactPeerCodec.Parse(encoded.Select(value => (ReadOnlyMemory<byte>)value), ipv6: false);

        Assert.Equal(2, encoded.Count);
        Assert.Equal(peers.Take(2), parsed);
    }

    [Fact]
    public void Parse_IgnoresInvalidLengthValues()
    {
        var peer = new IPEndPoint(IPAddress.Parse("203.0.113.10"), 6881);
        var valid = DhtCompactPeerCodec.Encode([peer], ipv6: false).Single();

        var parsed = DhtCompactPeerCodec.Parse(
            [new byte[5], valid, new byte[7]],
            ipv6: false);

        Assert.Equal(peer, Assert.Single(parsed));
    }
}
