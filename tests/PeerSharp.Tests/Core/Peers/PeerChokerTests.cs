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
    public void FillFreeUploadSlots_UnchokesTheBestWaitingPeer_AndChokesNobody()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.Connection.UploadSlotsMin = 2;
        torrent.Settings.Connection.UploadSlotsMax = 2;
        var choker = new PeerChoker(torrent, TimeProvider.System, NullLogger.Instance);
        var serving = new PolicyTestPeer(torrent); serving.SetInterested(true); serving.SetSpeed(1); serving.Unchoke();
        var slow = new PolicyTestPeer(torrent); slow.SetInterested(true); slow.SetSpeed(10);
        var fast = new PolicyTestPeer(torrent); fast.SetInterested(true); fast.SetSpeed(100);

        choker.FillFreeUploadSlots([serving, slow, fast], connectedCount: 3);

        Assert.False(serving.AmChoking);
        Assert.False(fast.AmChoking);
        Assert.True(slow.AmChoking);
    }

    [Fact]
    public void FillFreeUploadSlots_WhenEverySlotIsTaken_ChangesNothing()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.Connection.UploadSlotsMin = 1;
        torrent.Settings.Connection.UploadSlotsMax = 1;
        var choker = new PeerChoker(torrent, TimeProvider.System, NullLogger.Instance);
        var serving = new PolicyTestPeer(torrent); serving.SetInterested(true); serving.Unchoke();
        var waiting = new PolicyTestPeer(torrent); waiting.SetInterested(true); waiting.SetSpeed(100);

        choker.FillFreeUploadSlots([serving, waiting], connectedCount: 2);

        Assert.False(serving.AmChoking);
        Assert.True(waiting.AmChoking);
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
