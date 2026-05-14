using PeerSharp.Core;

namespace PeerSharp.Tests.Core;

public class TorrentFileInfoTests
{
    [Fact]
    public void Progress_ZeroSize_ReturnsOne()
    {
        var info = new TorrentFileInfo("file.txt", Size: 0, Index: 0);
        Assert.Equal(1.0f, info.Progress);
    }

    [Fact]
    public void Progress_NoDownloaded_ReturnsZero()
    {
        var info = new TorrentFileInfo("file.txt", Size: 1000, Index: 0);
        Assert.Equal(0.0f, info.Progress);
    }

    [Fact]
    public void Progress_FullyDownloaded_ReturnsOne()
    {
        var info = new TorrentFileInfo("file.txt", Size: 1000, Index: 0, DownloadedBytes: 1000);
        Assert.Equal(1.0f, info.Progress);
    }

    [Fact]
    public void Progress_HalfDownloaded_ReturnsHalf()
    {
        var info = new TorrentFileInfo("file.txt", Size: 1000, Index: 0, DownloadedBytes: 500);
        Assert.Equal(0.5f, info.Progress, 0.001f);
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var a = new TorrentFileInfo("a.txt", 100, 0, 50);
        var b = new TorrentFileInfo("a.txt", 100, 0, 50);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Equality_DifferentPath_NotEqual()
    {
        var a = new TorrentFileInfo("a.txt", 100, 0);
        var b = new TorrentFileInfo("b.txt", 100, 0);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void With_ChangesDownloadedBytes()
    {
        var original = new TorrentFileInfo("file.txt", 1000, 0, 0);
        var updated = original with { DownloadedBytes = 750 };
        Assert.Equal(0, original.DownloadedBytes);
        Assert.Equal(750, updated.DownloadedBytes);
        Assert.Equal(0.75f, updated.Progress, 0.001f);
    }
}
