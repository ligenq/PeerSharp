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
        var first = new PexPeer(torrent) { RemoteEndPoint = new IPEndPoint(IPAddress.Parse("1.1.1.1"), 1000) };
        var second = new PexPeer(torrent) { RemoteEndPoint = new IPEndPoint(IPAddress.Parse("2.2.2.2"), 2000) };

        coordinator.Broadcast([first, second]);

        Assert.Contains(first.Pex.Updates.Single(), peer => peer.Endpoint.Equals(second.RemoteEndPoint));
        Assert.DoesNotContain(first.Pex.Updates.Single(), peer => peer.Endpoint.Equals(first.RemoteEndPoint));
        Assert.Contains(second.Pex.Updates.Single(), peer => peer.Endpoint.Equals(first.RemoteEndPoint));
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
