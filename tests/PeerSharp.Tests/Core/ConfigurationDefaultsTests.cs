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

        Assert.True(settings.Connection.EnableWebSeeds);
    }

    [Fact]
    public void TransferLimits_AcceptInt64RangeAndRejectNegativeValues()
    {
        var transfer = new TransferSettings();
        long limit = (long)int.MaxValue + 1;

        transfer.MaxDownloadSpeed = limit;
        transfer.MaxUploadSpeed = limit;

        Assert.Equal(limit, transfer.MaxDownloadSpeed);
        Assert.Equal(limit, transfer.MaxUploadSpeed);
        Assert.Throws<ArgumentOutOfRangeException>(() => transfer.MaxDownloadSpeed = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => transfer.MaxUploadSpeed = -1);
    }
}
