namespace PeerSharp.Tests.Core;

public class QueueSettingsTests
{
    [Fact]
    public void RejectNegativeLimitsAndKeepZeroAsUnlimited()
    {
        var settings = new QueueSettings();
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.MaxActiveDownloads = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.MaxActiveSeeds = -1);
        Assert.Equal(3, settings.MaxActiveDownloads);
        Assert.Equal(2, settings.MaxActiveSeeds);
        settings.MaxActiveDownloads = 0;
        settings.MaxActiveSeeds = 0;
        Assert.Equal(0, settings.MaxActiveDownloads);
        Assert.Equal(0, settings.MaxActiveSeeds);
    }
}
