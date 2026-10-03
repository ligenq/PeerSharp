namespace PeerSharp.Tests.Core;

public class SettingsTests
{
    [Fact]
    public void RejectMalformedPeerIdAndOverflowingTrackerCount()
    {
        var settings = new Settings();
        Assert.Throws<ArgumentNullException>(() => settings.PeerId = null!);
        Assert.Throws<ArgumentException>(() => settings.PeerId = new byte[19]);
        Assert.Throws<ArgumentException>(() => settings.PeerId = new byte[21]);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.MaxPeersPerTrackerRequest = uint.MaxValue);
        byte[] peerId = Enumerable.Repeat((byte)1, 20).ToArray();
        settings.PeerId = peerId;
        peerId[0] = 2;
        Assert.Equal(1, settings.PeerId[0]);
        settings.MaxPeersPerTrackerRequest = 0;
        Assert.Equal(0u, settings.MaxPeersPerTrackerRequest);
    }
}
