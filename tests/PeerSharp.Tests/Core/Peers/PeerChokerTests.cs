using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Peers;

namespace PeerSharp.Tests.Core.Peers;

public class PeerChokerTests
{
    [Fact]
    public void Rechoke_PrefersFastestInterestedPeers()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.Connection.UploadSlotsMin = 2;
        torrent.Settings.Connection.UploadSlotsMax = 2;
        var choker = new PeerChoker(torrent, TimeProvider.System, NullLogger.Instance);
        var slow = new PolicyTestPeer(torrent); slow.SetInterested(true); slow.SetSpeed(10);
        var fast = new PolicyTestPeer(torrent); fast.SetInterested(true); fast.SetSpeed(100);
        var uninterested = new PolicyTestPeer(torrent); uninterested.SetInterested(false); uninterested.SetSpeed(200);

        choker.Rechoke([slow, fast, uninterested], connectedCount: 3);

        Assert.False(slow.AmChoking);
        Assert.False(fast.AmChoking);
        Assert.True(uninterested.AmChoking);
    }

    [Fact]
    public void GetUploadSlots_UsesConfiguredUploadLimit()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.Connection.UploadSlotsMin = 1;
        torrent.Settings.Connection.UploadSlotsMax = 16;
        torrent.Settings.Connection.TargetUploadPerSlotBytesPerSec = 100_000;
        torrent.Settings.Transfer.MaxUploadSpeed = 1_000_000;
        var choker = new PeerChoker(torrent, TimeProvider.System, NullLogger.Instance);

        Assert.Equal(10, choker.GetUploadSlotsForTesting(connectedCount: 32));
    }
}
