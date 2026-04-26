using PeerSharp.Internals;
using PeerSharp.PieceWriter;
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

    [Fact(Timeout = 30000)]
    public async Task Stop_ReleasesTorrentFileHandles()
    {
        string downloadPath = Path.Combine(Path.GetTempPath(), "MtTorrentTests_Stop", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(downloadPath);
        var handleCache = new RecordingFileHandleCache();
        var info = new TorrentFileMetadata();
        info.Info.Name = "test";
        info.Info.Hash = InfoHash.CreateRandom();
        info.Info.PieceSize = 16384;
        info.Info.FullSize = 1000;
        info.Info.Pieces.Add(new byte[20]);
        info.Info.Files.Add(new Internals.TorrentFileEntry { Path = "file.bin", Size = 1000, Offset = 0 });

        var settings = new Settings();
        settings.Files.DefaultDownloadPath = downloadPath;
        var torrent = Torrent.Create(
            info,
            settings,
            new TorrentTestUtility.MockBandwidthManager(),
            new TorrentTestUtility.MockAlertsManager(),
            new TorrentTestUtility.MockFileSelectionManager(),
            new TorrentTestUtility.MockPeerCommunicationFactory(),
            new TorrentTestUtility.MockTrackerFactory(),
            new TorrentTestUtility.MockGeoIpService(),
            handleCache,
            new TorrentTestUtility.MockConnectionGovernor(),
            _timeProvider);

        try
        {
            await torrent.StartAsync();
            await torrent.StopAsync();

            Assert.Equal(downloadPath, Assert.Single(handleCache.ClosedRoots));
        }
        finally
        {
            await torrent.DisposeAsync();
            handleCache.Dispose();
            try { Directory.Delete(downloadPath, true); } catch { }
        }
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

    [Fact]
    public void RegisterPeerTransport_RejectsNullAndDuplicateInstances()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var transport = new RecordingPeerTransport();

        Assert.Throws<ArgumentNullException>(() => torrent.RegisterPeerTransport(null!));

        torrent.RegisterPeerTransport(transport);

        Assert.Throws<InvalidOperationException>(() => torrent.RegisterPeerTransport(transport));
    }

    [Fact]
    public async Task RegisteredPeerTransport_StartsAndStopsWithTorrent()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var transport = new RecordingPeerTransport();
        torrent.RegisterPeerTransport(transport);

        await torrent.StartAsync();
        await torrent.StopAsync();

        Assert.Equal(1, transport.StartCalls);
        Assert.Equal(1, transport.StopCalls);
        Assert.Equal(0, transport.DisposeCalls);
    }

    [Fact]
    public async Task RegisteredPeerTransport_AfterStartWaitsUntilNextStartCycle()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        await torrent.StartAsync();

        var transport = new RecordingPeerTransport();
        torrent.RegisterPeerTransport(transport);

        Assert.Equal(0, transport.StartCalls);

        await torrent.StopAsync();
        await torrent.StartAsync();

        Assert.Equal(1, transport.StartCalls);
    }

    [Fact]
    public async Task StopAsync_SwallowsPeerTransportStopException()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var transport = new RecordingPeerTransport { ThrowOnStop = true };
        torrent.RegisterPeerTransport(transport);

        await torrent.StartAsync();
        await torrent.StopAsync();

        Assert.Equal(1, transport.StopCalls);
    }

    [Fact]
    public async Task DisposeAsync_StopsDisposesAndClearsPeerTransports()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var transport = new RecordingPeerTransport { ThrowOnStop = true, ThrowOnDispose = true };
        torrent.RegisterPeerTransport(transport);

        await torrent.StartAsync();
        await torrent.DisposeAsync();

        Assert.Equal(1, transport.StopCalls);
        Assert.Equal(1, transport.DisposeCalls);
        Assert.Throws<ObjectDisposedException>(() => torrent.RegisterPeerTransport(new RecordingPeerTransport()));
    }

    private sealed class RecordingPeerTransport : IPeerTransport
    {
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public bool ThrowOnStop { get; init; }
        public bool ThrowOnDispose { get; init; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCalls++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCalls++;
            if (ThrowOnStop)
            {
                throw new InvalidOperationException("stop failed");
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            if (ThrowOnDispose)
            {
                throw new InvalidOperationException("dispose failed");
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingFileHandleCache : IFileHandleCache
    {
        public List<string> ClosedRoots { get; } = new();

        public void CloseTorrentHandles(string rootPath)
        {
            ClosedRoots.Add(rootPath);
        }

        public void Dispose()
        {
        }

        public ValueTask<IFileHandleLease> GetHandleAsync(string path, bool writable, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}






