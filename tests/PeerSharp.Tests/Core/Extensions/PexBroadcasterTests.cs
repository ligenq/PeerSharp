using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Extensions;

namespace PeerSharp.Tests.Core.Extensions;

/// <summary>
/// The policy around sending ut_pex, which is where the rules that matter live: never for a private
/// torrent, never an address nobody can connect to, and never the same peer twice.
/// </summary>
public class PexBroadcasterTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PrivateTorrent_SendsNothing()
    {
        // BEP 27. A private torrent exists to keep peer discovery inside the tracker; gossiping its
        // swarm over PEX defeats the entire point, and is the one mistake here that cannot be undone
        // once the addresses are out.
        var harness = new PexHarness(isPrivate: true);
        harness.AddPeer("10.0.0.1", listenPort: 6881);
        var recipient = harness.AddPeer("10.0.0.2", listenPort: 6882);

        harness.Broadcaster.Broadcast(Now);

        Assert.Empty(recipient.Sent);
    }

    [Fact]
    public void PeerWithNoAdvertisedListenPort_IsNeverShared()
    {
        // The connection's port is ephemeral. Sharing it would put an address in the swarm that nobody
        // can ever connect to, and we would keep re-sharing it.
        var harness = new PexHarness(isPrivate: false);
        harness.AddPeer("10.0.0.1", listenPort: null);
        var recipient = harness.AddPeer("10.0.0.2", listenPort: 6882);

        harness.Broadcaster.Broadcast(Now);

        Assert.Empty(recipient.Sent);
    }

    [Fact]
    public void PeerIsNotToldAboutItself()
    {
        var harness = new PexHarness(isPrivate: false);
        var recipient = harness.AddPeer("10.0.0.2", listenPort: 6882);

        harness.Broadcaster.Broadcast(Now);

        Assert.Empty(recipient.Sent);
    }

    [Fact]
    public void SharesTheAdvertisedListenPort_NotTheConnectionPort()
    {
        var harness = new PexHarness(isPrivate: false);
        harness.AddPeer("10.0.0.1", listenPort: 6881, connectionPort: 49512);
        var recipient = harness.AddPeer("10.0.0.2", listenPort: 6882);

        harness.Broadcaster.Broadcast(Now);

        var added = Assert.Single(recipient.Sent).Added;
        Assert.Equal(new IPEndPoint(IPAddress.Parse("10.0.0.1"), 6881), Assert.Single(added));
    }

    [Fact]
    public void APeerIsAnnouncedOnlyOnce()
    {
        var harness = new PexHarness(isPrivate: false);
        harness.AddPeer("10.0.0.1", listenPort: 6881);
        var recipient = harness.AddPeer("10.0.0.2", listenPort: 6882);

        harness.Broadcaster.Broadcast(Now);
        harness.Broadcaster.Broadcast(Now + TimeSpan.FromMinutes(5));

        // Nothing changed between the two passes, so the second has no diff to report.
        Assert.Single(recipient.Sent);
    }

    [Fact]
    public void MessagesAreNotSentMoreOftenThanTheInterval()
    {
        var harness = new PexHarness(isPrivate: false);
        harness.AddPeer("10.0.0.1", listenPort: 6881);
        var recipient = harness.AddPeer("10.0.0.2", listenPort: 6882);

        harness.Broadcaster.Broadcast(Now);
        harness.AddPeer("10.0.0.3", listenPort: 6883);
        harness.Broadcaster.Broadcast(Now + TimeSpan.FromSeconds(30));

        // A new peer appeared, but not enough time has passed to mention it.
        Assert.Single(recipient.Sent);

        harness.Broadcaster.Broadcast(Now + TimeSpan.FromSeconds(90));
        Assert.Equal(2, recipient.Sent.Count);
    }

    [Fact]
    public void DepartedPeersAreReportedAsDropped()
    {
        var harness = new PexHarness(isPrivate: false);
        var leaving = harness.AddPeer("10.0.0.1", listenPort: 6881);
        var recipient = harness.AddPeer("10.0.0.2", listenPort: 6882);

        harness.Broadcaster.Broadcast(Now);
        harness.RemovePeer(leaving);
        harness.Broadcaster.Broadcast(Now + TimeSpan.FromSeconds(90));

        var second = recipient.Sent[1];
        Assert.Empty(second.Added);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("10.0.0.1"), 6881), Assert.Single(second.Dropped));
    }

    [Fact]
    public void PeerWithoutPexSupport_IsNotSentTo()
    {
        var harness = new PexHarness(isPrivate: false);
        harness.AddPeer("10.0.0.1", listenPort: 6881);
        var recipient = harness.AddPeer("10.0.0.2", listenPort: 6882, supportsPex: false);

        harness.Broadcaster.Broadcast(Now);

        Assert.Empty(recipient.Sent);
    }

    [Fact]
    public void SeedsAreFlagged()
    {
        var harness = new PexHarness(isPrivate: false);
        harness.AddPeer("10.0.0.1", listenPort: 6881, isSeed: true);
        var recipient = harness.AddPeer("10.0.0.2", listenPort: 6882);

        harness.Broadcaster.Broadcast(Now);

        Assert.Equal(0x02, Assert.Single(recipient.Sent).Flags[0] & 0x02);
    }

    /// <summary>A swarm of fake peers the broadcaster can be pointed at.</summary>
    private sealed class PexHarness
    {
        private readonly List<FakePexPeer> _peers = [];

        public PexHarness(bool isPrivate)
        {
            Broadcaster = new PexBroadcaster(
                () => isPrivate,
                () => _peers,
                NullLogger.Instance);
        }

        public PexBroadcaster Broadcaster { get; }

        public FakePexPeer AddPeer(
            string address,
            int? listenPort,
            int connectionPort = 40000,
            bool supportsPex = true,
            bool isSeed = false)
        {
            var peer = new FakePexPeer
            {
                Name = $"{address}:{connectionPort}",
                RemoteListenEndPoint = listenPort is { } port
                    ? new IPEndPoint(IPAddress.Parse(address), port)
                    : null,
                SupportsPex = supportsPex,
                PexFlags = isSeed ? (byte)0x02 : (byte)0x00
            };

            _peers.Add(peer);
            return peer;
        }

        public void RemovePeer(FakePexPeer peer) => _peers.Remove(peer);
    }

    private sealed class FakePexPeer : IPexPeer
    {
        public string Name { get; init; } = string.Empty;

        public IPEndPoint? RemoteListenEndPoint { get; init; }

        public bool SupportsPex { get; init; }

        public byte PexFlags { get; init; }

        public List<(List<IPEndPoint> Added, List<byte> Flags, List<IPEndPoint> Dropped)> Sent { get; } = [];

        public void SendPex(List<IPEndPoint> added, List<byte> addedFlags, List<IPEndPoint> dropped)
        {
            Sent.Add((added, addedFlags, dropped));
        }
    }
}
