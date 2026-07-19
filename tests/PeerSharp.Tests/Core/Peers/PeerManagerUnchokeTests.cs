using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Extensions;
using PeerSharp.Messages;

namespace PeerSharp.Tests.Core.Peers;

public class PeerManagerUnchokeTests
{
    private class MockPeer : PeerCommunication
    {
        public MockPeer(Torrent torrent, TimeProvider? timeProvider = null)
            : base(torrent, new MockListener(), timeProvider ?? TimeProvider.System) { }

        public void SetSpeed(int dl) => SetSmoothedDownloadSpeedForTesting(dl);
        public void SetInterested(bool val) => SetPeerInterestedForTesting(val);
    }

    private class MockListener : IPeerListener
    {
        public Task ConnectionClosedAsync(IPeerCommunication peer, int code) => Task.CompletedTask;
        public Task ExtendedHandshakeFinishedAsync(IPeerCommunication peer, ExtensionHandshake handshake) => Task.CompletedTask;
        public Task ExtendedMessageReceivedAsync(IPeerCommunication peer, int type, byte[] data) => Task.CompletedTask;
        public Task HandshakeFinishedAsync(IPeerCommunication peer) => Task.CompletedTask;
        public Task HolepunchMessageReceivedAsync(IPeerCommunication peer, UtHolepunch.MsgId id, System.Net.IPEndPoint endpoint, UtHolepunch.ErrorCode error) => Task.CompletedTask;
        public Task MessageReceivedAsync(IPeerCommunication peer, PeerMessage msg) => Task.CompletedTask;
        public Task PexReceivedAsync(IPeerCommunication peer, List<System.Net.IPEndPoint> added, List<byte> addedFlags, List<System.Net.IPEndPoint> dropped) => Task.CompletedTask;
        public Task PortReceivedAsync(IPeerCommunication peer, ushort dhtPort) => Task.CompletedTask;
    }

    [Fact]
    public void UnchokePeers_PrefersFastestInterestedPeers()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        // Use 2 slots with exactly 2 interested candidates so no optimistic slot is reserved.
        // When candidates.Count <= slots, regularSlots = slots (no reservation).
        torrent.Settings.Connection.UploadSlotsMin = 2;
        torrent.Settings.Connection.UploadSlotsMax = 2;
        torrent.Settings.Connection.OptimisticUnchokeIntervalSeconds = 9999;

        var manager = new PeerManager(torrent, null!, null!, TimeProvider.System, null!);

        // Setup peers: 2 interested (candidates), 1 not interested (filler)
        var p1 = new MockPeer(torrent); p1.SetSpeed(100); p1.SetInterested(true); p1.Choke();
        var p2 = new MockPeer(torrent); p2.SetSpeed(200); p2.SetInterested(true); p2.Choke();
        var p3 = new MockPeer(torrent); p3.SetSpeed(50); p3.SetInterested(false); p3.Choke(); // Not interested

        manager.AddConnectedPeerForTesting(p1);
        manager.AddConnectedPeerForTesting(p2);
        manager.AddConnectedPeerForTesting(p3);

        // Prevent optimistic unchoke
        manager.SetOptimisticPeerForTesting(null, TimeProvider.System.GetUtcNow());

        // Act
        manager.UnchokePeers();

        // Assert: both interested peers unchoked, non-interested stays choked
        Assert.False(p2.AmChoking, "Fastest peer should be unchoked");
        Assert.False(p1.AmChoking, "Second fastest should be unchoked");
        Assert.True(p3.AmChoking, "Non-interested peer should remain choked");
    }

    [Fact]
    public void UnchokePeers_Seeding_RotatesOutQuotaCompletePeer()
    {
        // torrent.Finished = true triggers the seeding sort path in UnchokePeers.
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Pieces.SetHaveAll();
        torrent.Settings.Connection.UploadSlotsMin = 2;
        torrent.Settings.Connection.UploadSlotsMax = 2;

        var now = TimeProvider.System.GetUtcNow();

        // pOld: unchoked 10 minutes ago and has sent far more than the piece quota.
        // SeedingChoker will mark it as quota-complete and de-prioritize it.
        var oldTime = new FakeTimeProvider(now.AddMinutes(-10));
        var pOld = new MockPeer(torrent, oldTime);
        pOld.Unchoke();                         // LastUnchokedAt = now-10min
        pOld.AddUploaded(400_000);              // UploadedSinceUnchoked = 400_000 > 16384*20
        oldTime.Advance(TimeSpan.FromSeconds(20)); // allow Choke() to pass its cooldown later
        pOld.SetInterested(true);

        // pFast: unchoked 30 seconds ago (well within the 60s quota window) with a high upload rate.
        var fastTime = new FakeTimeProvider(now.AddSeconds(-30));
        var pFast = new MockPeer(torrent, fastTime);
        pFast.Unchoke();                        // LastUnchokedAt = now-30s → NOT quota-complete
        pFast.AddUploaded(600_000);             // gives UpdateSpeed() a high result
        fastTime.Advance(TimeSpan.FromSeconds(20));
        pFast.SetInterested(true);

        // pWaiting: choked, waiting for a slot.
        var pWaiting = new MockPeer(torrent);
        pWaiting.SetInterested(true);           // stays choked (AmChoking = true by default)

        var manager = new PeerManager(torrent, null!, null!, TimeProvider.System, null!);

        manager.AddConnectedPeerForTesting(pOld);
        manager.AddConnectedPeerForTesting(pFast);
        manager.AddConnectedPeerForTesting(pWaiting);

        // Pin the optimistic unchoke slot to pWaiting so the test is deterministic.
        manager.SetOptimisticPeerForTesting(pWaiting, now);

        manager.UnchokePeers();

        Assert.False(pFast.AmChoking, "pFast (within quota, fastest) should remain unchoked");
        Assert.False(pWaiting.AmChoking, "pWaiting should be unchoked into the slot freed by pOld");
        Assert.True(pOld.AmChoking, "pOld (quota-complete) should be choked out to allow rotation");
    }
}
