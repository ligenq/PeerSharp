using CsCheck;
using PeerSharp.Internals.Peers;
using System.Net;

namespace PeerSharp.Tests.Core.Peers;

/// <summary>
/// BEP 40 canonical peer priority, against the values the BEP itself publishes.
/// </summary>
/// <remarks>
/// <para>
/// The property that makes this worth having at all is agreement between the two ends: when both
/// peers compute a priority for the same connection, they must get the same number, or the decision
/// it drives - which connection gives way when one has to - is made two different ways and the churn
/// it exists to prevent happens anyway. A value only this client agrees with is deterministic and
/// useless.
/// </para>
/// <para>
/// So the spec's own worked examples are the anchor. An implementation can be self-consistent,
/// symmetric and stable while computing something no other client computes, and every property short
/// of a published vector will pass.
/// </para>
/// </remarks>
public class PeerPriorityTests
{
    /// <summary>
    /// The two examples given in BEP 40, which fix the masking, the ordering and the hash at once.
    /// </summary>
    [Theory]
    [InlineData("123.213.32.10", "98.76.54.32", 0xec2d7224u)]
    [InlineData("123.213.32.10", "123.213.32.234", 0x99568189u)]
    public void MatchesTheValuesInTheSpecification(string ourIp, string peerIp, uint expected)
    {
        uint priority = PeerPriority.Calculate(EndPoint(ourIp), EndPoint(peerIp));

        Assert.Equal(expected, priority);
    }

    [Theory]
    [InlineData("123.213.32.10", "98.76.54.32", 0xec2d7224u)]
    [InlineData("123.213.32.10", "123.213.32.234", 0x99568189u)]
    public void TheOtherEndComputesTheSameValue(string ourIp, string peerIp, uint expected)
    {
        // Each end holds the pair the other way round. Sorting the masked values is what makes the
        // two calls agree, and this is the whole reason the BEP specifies sorting.
        Assert.Equal(expected, PeerPriority.Calculate(EndPoint(peerIp), EndPoint(ourIp)));
    }

    [Fact]
    public void BothEndsAgreeForAnyPairOfAddresses()
    {
        Gen.Select(Address, Address, Gen.UShort[1, 65535], Gen.UShort[1, 65535]).Sample((ours, peer, ourPort, peerPort) =>
        {
            var ourEndPoint = new IPEndPoint(ours, ourPort);
            var peerEndPoint = new IPEndPoint(peer, peerPort);

            Assert.Equal(
                PeerPriority.Calculate(ourEndPoint, peerEndPoint),
                PeerPriority.Calculate(peerEndPoint, ourEndPoint));
        }, iter: 20_000);
    }

    [Fact]
    public void NeighboursAreStillToldApart()
    {
        // The masking keeps a byte beyond the prefix two addresses share, so peers inside our own
        // network still order against each other. Masking to the shared prefix alone would hand every
        // one of them the same priority, which is the failure mode this guards.
        var ours = EndPoint("192.168.1.1");
        var priorities = new HashSet<uint>();

        for (int i = 2; i < 60; i++)
        {
            priorities.Add(PeerPriority.Calculate(ours, EndPoint($"192.168.1.{i}")));
        }

        Assert.True(priorities.Count > 50, $"58 neighbours produced only {priorities.Count} distinct priorities");
    }

    [Fact]
    public void CloserAddressesKeepMoreOfThemselves()
    {
        // A pair sharing a /24 is masked less aggressively than a pair sharing only a /16, which is
        // masked less than an unrelated pair. Changing a byte the mask discards must not change the
        // priority; changing one it keeps must.
        var ours = EndPoint("10.20.30.40");

        Assert.Equal(
            PeerPriority.Calculate(ours, EndPoint("10.20.30.41")),
            PeerPriority.Calculate(ours, EndPoint("10.20.30.41")));

        // Unrelated /16: the last two bytes survive only through the 0x55 mask, so a change confined
        // to the bits that mask away leaves the value alone.
        Assert.Equal(
            PeerPriority.Calculate(ours, EndPoint("200.100.0.0")),
            PeerPriority.Calculate(ours, EndPoint("200.100.170.170")));

        // 0xAA is the complement of 0x55, so 200.100.170.170 and 200.100.0.0 mask identically while
        // 200.100.85.85 does not.
        Assert.NotEqual(
            PeerPriority.Calculate(ours, EndPoint("200.100.0.0")),
            PeerPriority.Calculate(ours, EndPoint("200.100.85.85")));
    }

    [Fact]
    public void IdenticalAddressesFallBackToPorts()
    {
        // Two peers behind the same NAT present the same address, and the priority still has to tell
        // the connections apart.
        var address = IPAddress.Parse("203.0.113.9");
        uint first = PeerPriority.Calculate(new IPEndPoint(address, 6881), new IPEndPoint(address, 51413));
        uint second = PeerPriority.Calculate(new IPEndPoint(address, 6881), new IPEndPoint(address, 6882));

        Assert.NotEqual(first, second);

        // Still symmetric.
        Assert.Equal(first, PeerPriority.Calculate(new IPEndPoint(address, 51413), new IPEndPoint(address, 6881)));
    }

    [Fact]
    public void IPv6PairsAreSymmetricAndDistinct()
    {
        var ours = new IPEndPoint(IPAddress.Parse("2001:db8:1:2::1"), 6881);
        var near = new IPEndPoint(IPAddress.Parse("2001:db8:1:2::2"), 6881);
        var far = new IPEndPoint(IPAddress.Parse("2001:db9:9:9::9"), 6881);

        Assert.Equal(PeerPriority.Calculate(ours, near), PeerPriority.Calculate(near, ours));
        Assert.Equal(PeerPriority.Calculate(ours, far), PeerPriority.Calculate(far, ours));
        Assert.NotEqual(PeerPriority.Calculate(ours, near), PeerPriority.Calculate(ours, far));
    }

    [Fact]
    public void MixedAddressFamiliesDoNotThrow()
    {
        // Cannot happen on a connection that exists, and the BEP does not define it, so the only
        // requirement is that it degrades rather than throwing.
        var v4 = new IPEndPoint(IPAddress.Parse("198.51.100.1"), 6881);
        var v6 = new IPEndPoint(IPAddress.Parse("2001:db8::1"), 6881);

        Assert.Equal(PeerPriority.Calculate(v4, v6), PeerPriority.Calculate(v4, v6));
    }

    [Fact]
    public void TheFallbackIsStablePerPeerAndTorrent()
    {
        // Used when this client does not know its own public address, where the canonical value
        // cannot be computed. It only has to be deterministic and to separate peers.
        Gen.Select(Address, Gen.Byte.Array[20, 20], Address).Sample((peer, infoHash, other) =>
        {
            Assert.Equal(
                PeerPriority.CalculateWithoutLocalAddress(peer, infoHash),
                PeerPriority.CalculateWithoutLocalAddress(peer, infoHash));

            if (!peer.Equals(other))
            {
                Assert.NotEqual(
                    PeerPriority.CalculateWithoutLocalAddress(peer, infoHash),
                    PeerPriority.CalculateWithoutLocalAddress(other, infoHash));
            }
        }, iter: 5_000);
    }

    private static readonly Gen<IPAddress> Address = Gen.Byte.Array[4, 4].Select(b => new IPAddress(b));

    private static IPEndPoint EndPoint(string address) => new(IPAddress.Parse(address), 6881);
}
