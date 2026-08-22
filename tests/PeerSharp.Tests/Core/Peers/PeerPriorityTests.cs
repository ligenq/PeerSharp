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

    [Theory]
    // Reference vectors from libtorrent 2.1's test_peer_priority.cpp. The final two distinguish the
    // IPv6 rule from the IPv4 one: IPv6 retains one byte beyond the first differing byte.
    [InlineData("ffff:0fff:ffff:ffff:ffff:ffff:ffff:ffff", 0x59d71f38u)]
    [InlineData("ffff:ffff:0fff:ffff:ffff:ffff:ffff:ffff", 0x081d5282u)]
    [InlineData("ffff:ffff:ff0f:ffff:ffff:ffff:ffff:ffff", 0xc5f972beu)]
    [InlineData("ffff:ffff:ffff:0fff:ffff:ffff:ffff:ffff", 0x9ff50bd0u)]
    public void IPv6MatchesLibtorrentReferenceVectors(string peerAddress, uint expected)
    {
        var ours = new IPEndPoint(IPAddress.Parse("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff"), 0x4d2);
        var peer = new IPEndPoint(IPAddress.Parse(peerAddress), 0x12c);

        Assert.Equal(expected, PeerPriority.Calculate(ours, peer));
        Assert.Equal(expected, PeerPriority.Calculate(peer, ours));
    }

    [Fact]
    public void IPv6PriorityUsesAddressBytesBeyondTheFirst64Bits()
    {
        var ours = new IPEndPoint(IPAddress.Parse("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff"), 0x4d2);
        var first = new IPEndPoint(IPAddress.Parse("ffff:ffff:ffff:ffff:ffff:0fff:ffff:ffff"), 0x12c);
        var second = new IPEndPoint(IPAddress.Parse("ffff:ffff:ffff:ffff:ffff:1fff:ffff:ffff"), 0x12c);

        Assert.NotEqual(PeerPriority.Calculate(ours, first), PeerPriority.Calculate(ours, second));
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
    public void AnUnknownLocalAddressStillSeparatesNetworks()
    {
        // Before enough sources agree on our public address, the unspecified address stands in - the
        // same placeholder libtorrent starts from. The result is not canonical yet, since the peer at
        // the other end is using our real address, but peers from different networks must still rank
        // differently or the ordering decides nothing.
        var unknown = new IPEndPoint(IPAddress.Any, 6881);
        var priorities = new HashSet<uint>();

        for (int i = 1; i < 60; i++)
        {
            priorities.Add(PeerPriority.Calculate(unknown, new IPEndPoint(IPAddress.Parse($"198.{i}.100.7"), 6881)));
        }

        Assert.True(priorities.Count > 50, $"59 networks produced only {priorities.Count} distinct priorities");
    }

    [Fact]
    public void DistantPeersOnOneNetworkDeliberatelyRankAlike()
    {
        // Not a defect, and worth pinning so it is not "fixed" later. For a pair sharing no prefix
        // the mask is FF.FF.55.55, so only alternating bits of the last two bytes survive and a whole
        // /24 collapses into a handful of values. That is the point: BEP 40 ranks by network for
        // peers far away and by host only for peers close by, which is what stops one distant network
        // filling the swarm slot by slot. The full-address hash this replaced spread them out and
        // gave that up.
        var ours = new IPEndPoint(IPAddress.Parse("123.213.32.10"), 6881);
        var priorities = new HashSet<uint>();

        for (int i = 1; i < 60; i++)
        {
            priorities.Add(PeerPriority.Calculate(ours, new IPEndPoint(IPAddress.Parse($"198.51.100.{i}"), 6881)));
        }

        Assert.InRange(priorities.Count, 2, 16);
    }

    [Fact]
    public void LearningOurAddressChangesTheAnswerToTheCanonicalOne()
    {
        // Nothing caches the priority, so the value corrects itself as soon as the address is known.
        // This is the transition libtorrent leaves as a TODO, because it caches its peer ranks.
        var peer = EndPoint("98.76.54.32");

        uint beforeWeKnow = PeerPriority.Calculate(new IPEndPoint(IPAddress.Any, 6881), peer);
        uint once = PeerPriority.Calculate(EndPoint("123.213.32.10"), peer);

        Assert.NotEqual(beforeWeKnow, once);
        Assert.Equal(0xec2d7224u, once);
    }

    [Fact]
    public void AMismatchedAddressFamilyIsTreatedAsUnknown()
    {
        // Cannot happen on a connection that exists. If it does, our v4 address says nothing about a
        // v6 peer, so it is worth no more than not knowing - which is what libtorrent's external
        // address table returns in the same situation.
        var v6Peer = new IPEndPoint(IPAddress.Parse("2001:db8::1"), 6881);

        Assert.Equal(
            PeerPriority.Calculate(new IPEndPoint(IPAddress.IPv6Any, 6881), v6Peer),
            PeerPriority.Calculate(new IPEndPoint(IPAddress.Parse("198.51.100.1"), 6881), v6Peer));
    }

    private static readonly Gen<IPAddress> Address = Gen.Byte.Array[4, 4].Select(b => new IPAddress(b));

    private static IPEndPoint EndPoint(string address) => new(IPAddress.Parse(address), 6881);
}
