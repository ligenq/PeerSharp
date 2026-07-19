using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using PeerSharp.Internals.Peers;

namespace PeerSharp.Tests.Core.Peers;

public class PeerHealthMonitorTests
{
    [Fact]
    public async Task CheckAsync_ClosesSlowPeerAfterGracePeriod()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.Connection.SlowPeerMinConnectedPeers = 1;
        torrent.Settings.Connection.SlowPeerMinDownloadSpeedBytesPerSec = 1_000;
        torrent.Settings.Connection.SlowPeerGraceSeconds = 1;
        var monitor = new PeerHealthMonitor(torrent, NullLogger.Instance);
        var peer = new ClosablePeer(torrent);
        peer.SetSpeed(0);
        peer.SetInterested(true);
        peer.Unchoke();
        peer.SetAmInterestedForTesting(true);
        peer.SetPeerChokingForTesting(false);
        peer.SetLastActivityTicksForTesting(Environment.TickCount64);
        monitor.MarkSlowForTesting(peer, Environment.TickCount64 - 10_000);

        await monitor.CheckAsync([peer], connectedCount: 1);

        Assert.Equal(1, peer.CloseCalls);
        Assert.Equal(0, monitor.SlowPeerCountForTesting);
    }

    [Fact]
    public async Task CheckAsync_ClearsSlowStateWhenSwarmIsBelowThreshold()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.Connection.SlowPeerMinConnectedPeers = 2;
        var monitor = new PeerHealthMonitor(torrent, NullLogger.Instance);
        var peer = new ClosablePeer(torrent);
        monitor.MarkSlowForTesting(peer, Environment.TickCount64 - 2_000);

        await monitor.CheckAsync([peer], connectedCount: 1);

        Assert.Equal(0, monitor.SlowPeerCountForTesting);
        Assert.Equal(0, peer.CloseCalls);
    }

    private sealed class ClosablePeer : PolicyTestPeer
    {
        public ClosablePeer(Torrent torrent) : base(torrent) { }
        public int CloseCalls { get; private set; }
        public override Task CloseAsync()
        {
            CloseCalls++;
            return Task.CompletedTask;
        }
    }
}
