using PeerSharp.Internals;
using Microsoft.Extensions.Time.Testing;

namespace PeerSharp.Tests.Core;

public class TorrentTests
{
    private readonly FakeTimeProvider _timeProvider = new();

    [Fact]
    public void Constructor_InitializesState()
    {
        var torrent = TorrentTestUtility.CreateMinimal();

        Assert.Equal(TorrentState.Stopped, torrent.State);
        Assert.False(torrent.Started);
        Assert.NotNull(torrent.Pieces);
        Assert.NotNull(torrent.TrackerManager);
    }

    [Fact]
    public async Task Start_ChangesStateToActive()
    {
        var info = new TorrentFileMetadata();
        info.Info.Name = "test";
        info.Info.PieceSize = 16384;
        info.Info.FullSize = 1000;
        info.Info.Pieces.Add(new byte[20]);

        var torrent = TorrentTestUtility.CreateMinimal(info);

        await torrent.StartAsync();

        Assert.True(torrent.Started);
        Assert.Equal(TorrentState.Active, torrent.State);
    }

    [Fact]
    public async Task Stop_ChangesStateToStopped()
    {
        var info = new TorrentFileMetadata();
        info.Info.Name = "test";
        info.Info.PieceSize = 16384;
        info.Info.FullSize = 1000;
        info.Info.Pieces.Add(new byte[20]);

        var torrent = TorrentTestUtility.CreateMinimal(info);
        await torrent.StartAsync();
        await torrent.StopAsync();

        Assert.False(torrent.Started);
        Assert.Equal(TorrentState.Stopped, torrent.State);
    }

    [Fact]
    public void GetResumeData_CapturesCurrentState()
    {
        var info = new TorrentFileMetadata();
        info.Info.Hash = InfoHash.CreateRandom();
        info.Info.Name = "test";
        info.Info.PieceSize = 16384;
        info.Info.FullSize = 1000;
        info.Info.Pieces.Add(new byte[20]);

        var torrent = TorrentTestUtility.CreateMinimal(info);
        torrent.Pieces.AddPiece(0);

        var resumeData = torrent.GetResumeData();

        Assert.Equal(torrent.Hash, resumeData.Hash);
        Assert.NotEmpty(resumeData.Data);
    }

    [Fact]
    public async Task SetDownloadPath_WorksWhenStopped()
    {
        var torrent = TorrentTestUtility.CreateMinimal();

        await torrent.SetDownloadPathAsync("D:\\NewPath");

        Assert.Equal("D:\\NewPath", torrent.LocalState.DownloadPath);
    }

    [Fact]
    public async Task SetDownloadPath_ThrowsWhenStarted()
    {
        var info = new TorrentFileMetadata();
        info.Info.Name = "test";
        info.Info.PieceSize = 16384;
        info.Info.FullSize = 1000;
        info.Info.Pieces.Add(new byte[20]);

        var torrent = TorrentTestUtility.CreateMinimal(info);
        await torrent.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => torrent.SetDownloadPathAsync("D:\\NewPath"));
    }
}






