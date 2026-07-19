using PeerSharp.Internals.Dht;
using System.Net;

namespace PeerSharp.Tests.Core.Dht;

public class DhtCompactNodeCodecTests
{
    [Fact]
    public void EncodeParse_Ipv4Nodes_RoundTripsCompactFormat()
    {
        var node = new NodeInfo(NodeId(1), new IPEndPoint(IPAddress.Parse("203.0.113.10"), 6881));

        byte[] encoded = DhtCompactNodeCodec.Encode([node], ipv6: false);
        var parsed = DhtCompactNodeCodec.Parse(encoded, ipv6: false);

        Assert.Equal(26, encoded.Length);
        var parsedNode = Assert.Single(parsed);
        Assert.Equal(node.Id, parsedNode.Id);
        Assert.Equal(node.EndPoint, parsedNode.EndPoint);
    }

    [Fact]
    public void EncodeParse_Ipv6Nodes_RoundTripsCompactFormat()
    {
        var node = new NodeInfo(NodeId(2), new IPEndPoint(IPAddress.Parse("2001:db8::1"), 51413));

        byte[] encoded = DhtCompactNodeCodec.Encode([node], ipv6: true);
        var parsed = DhtCompactNodeCodec.Parse(encoded, ipv6: true);

        Assert.Equal(38, encoded.Length);
        var parsedNode = Assert.Single(parsed);
        Assert.Equal(node.Id, parsedNode.Id);
        Assert.Equal(node.EndPoint, parsedNode.EndPoint);
    }

    [Fact]
    public void Encode_FiltersByAddressFamily()
    {
        var ipv4 = new NodeInfo(NodeId(1), new IPEndPoint(IPAddress.Parse("198.51.100.1"), 1));
        var ipv6 = new NodeInfo(NodeId(2), new IPEndPoint(IPAddress.Parse("2001:db8::2"), 2));

        byte[] encodedV4 = DhtCompactNodeCodec.Encode([ipv4, ipv6], ipv6: false);
        byte[] encodedV6 = DhtCompactNodeCodec.Encode([ipv4, ipv6], ipv6: true);

        Assert.Equal(26, encodedV4.Length);
        Assert.Equal(38, encodedV6.Length);
        Assert.Equal(ipv4.EndPoint, Assert.Single(DhtCompactNodeCodec.Parse(encodedV4, ipv6: false)).EndPoint);
        Assert.Equal(ipv6.EndPoint, Assert.Single(DhtCompactNodeCodec.Parse(encodedV6, ipv6: true)).EndPoint);
    }

    [Fact]
    public void Parse_IgnoresTrailingPartialNode()
    {
        var node = new NodeInfo(NodeId(1), new IPEndPoint(IPAddress.Parse("203.0.113.10"), 6881));
        byte[] encoded = DhtCompactNodeCodec.Encode([node], ipv6: false)
            .Concat(new byte[] { 1, 2, 3 })
            .ToArray();

        var parsed = DhtCompactNodeCodec.Parse(encoded, ipv6: false);

        Assert.Single(parsed);
    }

    [Fact]
    public void Encode_SkipsShortNodeIds()
    {
        var node = new NodeInfo(new byte[19], new IPEndPoint(IPAddress.Parse("203.0.113.10"), 6881));

        byte[] encoded = DhtCompactNodeCodec.Encode([node], ipv6: false);

        Assert.Empty(encoded);
    }

    [Fact]
    public void Encode_FollowingNodeStaysAlignedWhenPriorEntryFiltered()
    {
        // First node would be filtered (wrong family); second is valid. Output must
        // contain exactly one well-formed record so downstream parsing is correct.
        var ipv6 = new NodeInfo(NodeId(1), new IPEndPoint(IPAddress.Parse("2001:db8::1"), 1));
        var ipv4 = new NodeInfo(NodeId(2), new IPEndPoint(IPAddress.Parse("203.0.113.10"), 6881));

        byte[] encoded = DhtCompactNodeCodec.Encode([ipv6, ipv4], ipv6: false);

        Assert.Equal(26, encoded.Length);
        var parsed = DhtCompactNodeCodec.Parse(encoded, ipv6: false);
        Assert.Equal(ipv4.EndPoint, Assert.Single(parsed).EndPoint);
        Assert.Equal(ipv4.Id, Assert.Single(parsed).Id);
    }

    [Fact]
    public void Encode_EmptyInput_ReturnsEmptyArray()
    {
        byte[] encoded = DhtCompactNodeCodec.Encode(Array.Empty<NodeInfo>(), ipv6: false);
        Assert.Empty(encoded);
    }

    private static byte[] NodeId(byte seed)
    {
        return Enumerable.Range(0, 20).Select(i => (byte)(seed + i)).ToArray();
    }
}
