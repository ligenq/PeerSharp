namespace PeerSharp.Tests;

public class AddTorrentOptionsTests
{
    [Fact]
    public void Default_ReturnsNewInstance()
    {
        var a = AddTorrentOptions.Default;
        var b = AddTorrentOptions.Default;

        Assert.NotSame(a, b);
    }

    [Fact]
    public void Defaults_AreExpected()
    {
        var options = new AddTorrentOptions();

        Assert.True(options.StartImmediately);
        Assert.Equal(DownloadStrategy.RarestFirst, options.DownloadStrategy);
        Assert.Null(options.DownloadPath);
        Assert.Null(options.AdditionalTrackers);
        Assert.Null(options.FileSelections);
        Assert.Null(options.Events);
        Assert.Null(options.ResumeData);
        Assert.Null(options.RatioLimit);
        Assert.Null(options.SeedTimeLimit);
        Assert.Null(options.DownloadLimitBytesPerSecond);
        Assert.Null(options.UploadLimitBytesPerSecond);
        Assert.Equal(0, options.QueuePriority);
    }

    [Fact]
    public void Constructor_SetsDownloadPath()
    {
        var options = new AddTorrentOptions("C:\\Downloads");

        Assert.Equal("C:\\Downloads", options.DownloadPath);
    }
}




