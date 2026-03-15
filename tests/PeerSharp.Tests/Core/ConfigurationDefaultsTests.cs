namespace PeerSharp.Tests.Core;

public class ConfigurationDefaultsTests
{
    [Fact]
    public void Settings_Defaults_EnableDht()
    {
        var settings = new Settings();

        Assert.True(settings.Dht.Enabled);
    }

    [Fact]
    public void Settings_Defaults_ForNewPerformanceSettings()
    {
        var settings = new Settings();

        Assert.Equal(0u, settings.Files.MaxDiskReadSpeed);
        Assert.Equal(0u, settings.Files.MaxDiskWriteSpeed);
        Assert.Equal(8, settings.Transfer.MaxConcurrentPieceHashing);
        Assert.Equal(8, settings.Transfer.MaxConcurrentPieceWrites);

        Assert.Equal(5, settings.Connection.PeerReconnectBaseSeconds);
        Assert.Equal(300, settings.Connection.PeerReconnectMaxSeconds);
        Assert.Equal(2000, settings.Connection.PeerReconnectJitterMs);

        Assert.Equal(8, settings.Connection.SlowPeerMinConnectedPeers);
        Assert.Equal(30 * 1024, settings.Connection.SlowPeerMinDownloadSpeedBytesPerSec);
        Assert.Equal(30 * 1024, settings.Connection.SlowPeerMinUploadSpeedBytesPerSec);
        Assert.Equal(30, settings.Connection.SlowPeerGraceSeconds);
    }
}
