namespace PeerSharp.Tests.Api;

public class AddTorrentOptionsTests
{
    [Fact]
    public void Snapshot_DetachesMutableCollectionsEndpointsAndResumeData()
    {
        string[] trackers = ["https://original.invalid/announce"];
        FileSelection[] selections = [new(true, Priority.Normal)];
        var peer = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1234);
        byte[] resumeBytes = [1, 2, 3];
        var options = new AddTorrentOptions
        {
            AdditionalTrackers = trackers,
            AdditionalPeers = [peer],
            FileSelections = selections,
            ResumeData = new TorrentResumeData { Data = resumeBytes }
        };
        var snapshot = options.Snapshot();
        trackers[0] = "https://changed.invalid/announce";
        selections[0] = new(false);
        peer.Port = 5678;
        resumeBytes[0] = 99;
        options.StartImmediately = false;
        Assert.Equal("https://original.invalid/announce", Assert.Single(snapshot.AdditionalTrackers!));
        Assert.True(Assert.Single(snapshot.FileSelections!).Selected);
        Assert.Equal(1234, Assert.Single(snapshot.AdditionalPeers!).Port);
        Assert.Equal(1, snapshot.ResumeData!.Data[0]);
        Assert.True(snapshot.StartImmediately);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-1)]
    public void Snapshot_RejectsInvalidRatio(float ratio)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new AddTorrentOptions { RatioLimit = ratio }.Snapshot());

    [Fact]
    public void Snapshot_RejectsInvalidLimitsAndStrategy()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AddTorrentOptions { SeedTimeLimit = TimeSpan.FromSeconds(-1) }.Snapshot());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AddTorrentOptions { DownloadStrategy = (DownloadStrategy)999 }.Snapshot());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AddTorrentOptions { DownloadLimitBytesPerSecond = -1 }.Snapshot());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AddTorrentOptions { UploadLimitBytesPerSecond = -1 }.Snapshot());
    }

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
        Assert.Null(options.AdditionalPeers);
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




