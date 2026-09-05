using PeerSharp.Internals.Peers;

namespace PeerSharp.Tests.Core.Peers;

/// <summary>
/// Which transports a dial tries, and in what order.
/// </summary>
/// <remarks>
/// The rule is libtorrent's, which asks only about the peer in hand: use uTP if this peer is
/// believed to support it, TCP otherwise. There was a share target here, aiming to keep 70% of
/// connections on uTP, and it has gone - libtorrent has no such notion, and a quota can only
/// overrule the evidence, sending a peer known to speak uTP over TCP because of when it happened to
/// be dialled. What a share was meant to guard against is answered closer to the evidence: per-peer
/// demotion when a peer will not take uTP, and the cold-start limit when the path carries no UDP.
/// </remarks>
public class TransportPlanBuilderTests
{
    private static ConnectionSettings DefaultSettings(
        bool tcp = true,
        bool utp = true,
        bool preferUtp = true)
    {
        return new ConnectionSettings
        {
            EnableTcpOut = tcp,
            EnableUtpOut = utp,
            PreferUtp = preferUtp
        };
    }

    private static TransportPlanBuilder.Inputs MakeInputs(
        ConnectionSettings settings,
        bool forceUtp = false,
        bool utpAvailable = true)
    {
        return new TransportPlanBuilder.Inputs(settings, forceUtp, utpAvailable);
    }

    [Fact]
    public void Build_BothDisabled_ReturnsEmpty()
    {
        var plan = TransportPlanBuilder.Build(MakeInputs(DefaultSettings(tcp: false, utp: false)));
        Assert.Empty(plan);
    }

    [Fact]
    public void Build_ForceUtpAndUtpAllowed_ReturnsOnlyUtp()
    {
        var plan = TransportPlanBuilder.Build(MakeInputs(DefaultSettings(), forceUtp: true));
        Assert.Equal([TransportPreference.Utp], plan);
    }

    [Fact]
    public void Build_ForceUtpButUtpUnavailable_ReturnsEmpty()
    {
        var plan = TransportPlanBuilder.Build(
            MakeInputs(DefaultSettings(), forceUtp: true, utpAvailable: false));
        Assert.Empty(plan);
    }

    [Fact]
    public void Build_ForceUtpButUtpDisabled_ReturnsEmpty()
    {
        var plan = TransportPlanBuilder.Build(
            MakeInputs(DefaultSettings(utp: false), forceUtp: true));
        Assert.Empty(plan);
    }

    [Fact]
    public void Build_TcpOnly_ReturnsTcpOnly()
    {
        var plan = TransportPlanBuilder.Build(MakeInputs(DefaultSettings(utp: false)));
        Assert.Equal([TransportPreference.Tcp], plan);
    }

    [Fact]
    public void Build_UtpUnavailable_ReturnsTcpOnly()
    {
        var plan = TransportPlanBuilder.Build(MakeInputs(DefaultSettings(), utpAvailable: false));
        Assert.Equal([TransportPreference.Tcp], plan);
    }

    [Fact]
    public void Build_UtpDisabledOutgoingButTcpOff_ReturnsEmpty()
    {
        // Nothing left to dial with. The alternative - falling back to a transport the caller turned
        // off - would make EnableUtpOut and EnableTcpOut advisory.
        var plan = TransportPlanBuilder.Build(MakeInputs(DefaultSettings(tcp: false, utp: false)));
        Assert.Empty(plan);
    }

    [Fact]
    public void Build_PreferUtpFalse_TcpFirstThenUtp()
    {
        var plan = TransportPlanBuilder.Build(MakeInputs(DefaultSettings(preferUtp: false)));
        Assert.Equal([TransportPreference.Tcp, TransportPreference.Utp], plan);
    }

    [Fact]
    public void EveryPeerLeadsWithUtpRegardlessOfWhatIsAlreadyConnected()
    {
        // The share target used to make this depend on the peers already connected, so the same peer
        // got a different transport depending on when it was dialled. The plan now depends on the
        // settings and on this peer alone, which is what makes it reproducible.
        var settings = DefaultSettings();

        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(
                [TransportPreference.Utp, TransportPreference.Tcp],
                TransportPlanBuilder.Build(MakeInputs(settings)));
        }
    }

    [Fact]
    public void UtpKeepsTcpBehindItSoABlockedUdpPathStillReachesThePeer()
    {
        // Leading with uTP is only affordable because the fallback is inside the same dial. Losing
        // the second entry would turn a blocked UDP path from one capped attempt into a lost peer.
        var plan = TransportPlanBuilder.Build(MakeInputs(DefaultSettings()));

        Assert.Equal([TransportPreference.Utp, TransportPreference.Tcp], plan);
    }
}
