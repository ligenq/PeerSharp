using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;

namespace PeerSharp.Tests.Core.Peers;

public class PeerExchangeCoordinatorTests
{
    [Fact]
    public void Broadcast_ExcludesEachRecipientFromItsOwnUpdate()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var coordinator = new PeerExchangeCoordinator(torrent, new ConcurrentDictionary<IPEndPoint, PeerHistory>(), NullLogger.Instance);
        // The port a connection arrived on is ephemeral; what gets shared is where the peer listens.
        var first = new PexPeer(torrent)
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse("1.1.1.1"), 40001),
            RemoteListenEndPoint = new IPEndPoint(IPAddress.Parse("1.1.1.1"), 1000)
        };
        var second = new PexPeer(torrent)
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse("2.2.2.2"), 40002),
            RemoteListenEndPoint = new IPEndPoint(IPAddress.Parse("2.2.2.2"), 2000)
        };

        coordinator.Broadcast([first, second]);

        Assert.Contains(first.Pex.Updates.Single(), peer => peer.Endpoint.Equals(second.RemoteListenEndPoint));
        Assert.DoesNotContain(first.Pex.Updates.Single(), peer => peer.Endpoint.Equals(first.RemoteListenEndPoint));
        Assert.Contains(second.Pex.Updates.Single(), peer => peer.Endpoint.Equals(first.RemoteListenEndPoint));

        // And never the connection endpoints, which nobody could connect back to.
        Assert.DoesNotContain(first.Pex.Updates.Single(), peer => peer.Endpoint.Equals(second.RemoteEndPoint));
    }

    [Fact]
    public void Broadcast_SkipsPeersThatHaveNotSaidWhereTheyListen()
    {
        // Without BEP 10 'p' all we have is an ephemeral source port. Sharing it would put an address
        // in the swarm that every recipient then wastes connection attempts on.
        var torrent = TorrentTestUtility.CreateMinimal();
        var coordinator = new PeerExchangeCoordinator(torrent, new ConcurrentDictionary<IPEndPoint, PeerHistory>(), NullLogger.Instance);
        var silent = new PexPeer(torrent) { RemoteEndPoint = new IPEndPoint(IPAddress.Parse("1.1.1.1"), 40001) };
        var recipient = new PexPeer(torrent)
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse("2.2.2.2"), 40002),
            RemoteListenEndPoint = new IPEndPoint(IPAddress.Parse("2.2.2.2"), 2000)
        };

        coordinator.Broadcast([silent, recipient]);

        // An update is still offered - UtPex.Update decides for itself whether anything changed - but
        // it must carry nothing, because the only other peer had no shareable address.
        Assert.Empty(recipient.Pex.Updates.Single());
    }

    [Fact]
    public void Broadcast_DoesNotShareAConnectedPeersEphemeralKnownEndpoint()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var connectedEndpoint = new IPEndPoint(IPAddress.Parse("1.1.1.1"), 40001);
        var knownPeers = new ConcurrentDictionary<IPEndPoint, PeerHistory>();
        knownPeers[connectedEndpoint] = new PeerHistory { EndPoint = connectedEndpoint };
        var coordinator = new PeerExchangeCoordinator(torrent, knownPeers, NullLogger.Instance);
        var connected = new PexPeer(torrent)
        {
            RemoteEndPoint = connectedEndpoint,
            RemoteListenEndPoint = new IPEndPoint(IPAddress.Parse("1.1.1.1"), 1000)
        };
        var recipient = new PexPeer(torrent)
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse("2.2.2.2"), 40002),
            RemoteListenEndPoint = new IPEndPoint(IPAddress.Parse("2.2.2.2"), 2000)
        };

        coordinator.Broadcast([connected, recipient]);

        Assert.Contains(recipient.Pex.Updates.Single(), peer => peer.Endpoint.Equals(connected.RemoteListenEndPoint));
        Assert.DoesNotContain(recipient.Pex.Updates.Single(), peer => peer.Endpoint.Equals(connectedEndpoint));
    }

    [Fact]
    public void Broadcast_DoesNotShareAnEphemeralEndpointLeftBehindByADepartedPeer()
    {
        // The case the connected-peer exclusion cannot reach. Every peer that dials us leaves a history
        // entry under its ephemeral source port, and nothing removes it when the connection ends - only
        // size-based pruning ever does. Gossiping it closes a loop, because the recipient records the
        // address, ranks it, dials it, and fails, having learnt it from us.
        var torrent = TorrentTestUtility.CreateMinimal();
        var departed = new IPEndPoint(IPAddress.Parse("3.3.3.3"), 40003);
        var knownPeers = new ConcurrentDictionary<IPEndPoint, PeerHistory>
        {
            [departed] = new PeerHistory { EndPoint = departed, IsListenAddress = false }
        };
        var coordinator = new PeerExchangeCoordinator(torrent, knownPeers, NullLogger.Instance);
        var connected = new PexPeer(torrent)
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse("1.1.1.1"), 40001),
            RemoteListenEndPoint = new IPEndPoint(IPAddress.Parse("1.1.1.1"), 1000)
        };
        var recipient = new PexPeer(torrent)
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse("2.2.2.2"), 40002),
            RemoteListenEndPoint = new IPEndPoint(IPAddress.Parse("2.2.2.2"), 2000)
        };

        coordinator.Broadcast([connected, recipient]);

        Assert.DoesNotContain(recipient.Pex.Updates.Single(), peer => peer.Endpoint.Equals(departed));
    }

    [Fact]
    public void Broadcast_StillSharesKnownPeersThatAreRealListeningAddresses()
    {
        // The other half of the same filter: a peer a tracker or the DHT told us about is a listening
        // address by definition, and is exactly what PEX exists to pass on.
        var torrent = TorrentTestUtility.CreateMinimal();
        var fromTracker = new IPEndPoint(IPAddress.Parse("4.4.4.4"), 6881);
        var knownPeers = new ConcurrentDictionary<IPEndPoint, PeerHistory>
        {
            [fromTracker] = new PeerHistory { EndPoint = fromTracker }
        };
        var coordinator = new PeerExchangeCoordinator(torrent, knownPeers, NullLogger.Instance);
        var connected = new PexPeer(torrent)
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse("1.1.1.1"), 40001),
            RemoteListenEndPoint = new IPEndPoint(IPAddress.Parse("1.1.1.1"), 1000)
        };
        var recipient = new PexPeer(torrent)
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse("2.2.2.2"), 40002),
            RemoteListenEndPoint = new IPEndPoint(IPAddress.Parse("2.2.2.2"), 2000)
        };

        coordinator.Broadcast([connected, recipient]);

        Assert.Contains(recipient.Pex.Updates.Single(), peer => peer.Endpoint.Equals(fromTracker));
    }

    [Fact]
    public void ApplyFlags_SetsSeedAndUtpCapabilities()
    {
        var history = new PeerHistory { EndPoint = new IPEndPoint(IPAddress.Loopback, 6881) };

        PeerExchangeCoordinator.ApplyFlags(history, (byte)(UtPex.Peer.Seed | UtPex.Peer.Utp));

        Assert.True(history.IsSeed);
        Assert.True(history.UtpSupported);
        Assert.True(history.UtpHinted);
    }

    private sealed class PexPeer : PolicyTestPeer
    {
        public PexPeer(Torrent torrent) : base(torrent) { }
        public RecordingPex Pex { get; } = new();
        public override IUtPex UtPex => Pex;
    }

    private sealed class RecordingPex : IUtPex
    {
        public List<List<(IPEndPoint Endpoint, byte Flags)>> Updates { get; } = [];
        public int? LocalMessageId { get; set; }
        public int? RemoteMessageId { get; set; }
        public void Init(ExtensionHandshake handshake) { }
        public void SetLocalMessageId(int id) => LocalMessageId = id;
        public Task HandleMessageAsync(byte[] data) => Task.CompletedTask;
        public void SendPex(List<IPEndPoint> added, List<byte> addedFlags, List<IPEndPoint> dropped) { }
        public void Update(IEnumerable<(IPEndPoint Ep, byte Flags)> peers) => Updates.Add(peers.Select(peer => (peer.Ep, peer.Flags)).ToList());
    }
}
