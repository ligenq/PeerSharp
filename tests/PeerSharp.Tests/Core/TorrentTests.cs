using PeerSharp.Internals;
using PeerSharp.PieceWriter;
using Microsoft.Extensions.Time.Testing;
using System.Security.Cryptography;

namespace PeerSharp.Tests.Core;

public class TorrentTests
{
    private readonly FakeTimeProvider _timeProvider = new();

    [Fact]
    public void GetAllFileInfo_ReturnsMappedFileInfo()
    {
        var info = new TorrentFileMetadata();
        info.Info.Name = "test";
        info.Info.PieceSize = 16384;
        info.Info.Files.Add(new Internals.TorrentFileEntry { Path = "f1.txt", Size = 100, Offset = 0 });
        info.Info.Files.Add(new Internals.TorrentFileEntry { Path = "f2.txt", Size = 200, Offset = 100 });
        info.Info.FullSize = 300;
        info.Info.Pieces.Add(new byte[20]);

        var torrent = TorrentTestUtility.CreateMinimal(info);

        var fileInfos = torrent.GetAllFileInfo();

        Assert.Equal(2, fileInfos.Count);
        Assert.Equal("f1.txt", fileInfos[0].Path);
        Assert.Equal(100, fileInfos[0].Size);
        Assert.Equal("f2.txt", fileInfos[1].Path);
        Assert.Equal(200, fileInfos[1].Size);
    }

    [Fact]
    public async Task FileSelectionApis_DelegateToManager()
    {
        var info = new TorrentFileMetadata();
        info.Info.Name = "test";
        info.Info.PieceSize = 16384;
        info.Info.FullSize = 100;
        info.Info.Files.Add(new Internals.TorrentFileEntry { Path = "f1.txt", Size = 100, Offset = 0 });
        info.Info.Pieces.Add(new byte[20]);

        var mockSelectionManager = new CustomMockSelectionManager();

        var settings = new Settings();
        settings.Files.DefaultDownloadPath = Path.GetTempPath();

        var torrent = Torrent.Create(
            info,
            settings,
            new TorrentTestUtility.MockBandwidthManager(),
            new TorrentTestUtility.MockAlertsManager(),
            mockSelectionManager,
            new TorrentTestUtility.MockPeerCommunicationFactory(),
            new TorrentTestUtility.MockTrackerFactory(),
            new TorrentTestUtility.MockGeoIpService(),
            new TorrentTestUtility.MockFileHandleCache(),
            new TorrentTestUtility.MockConnectionGovernor(),
            TimeProvider.System);

        var selections = torrent.GetAllFileSelections();
        Assert.Equal(mockSelectionManager.Selections.Count, selections.Count);
        Assert.Equal(mockSelectionManager.Selections[0], selections[0]);

        await torrent.SetFilePriorityAsync(0, Priority.High);
        Assert.Equal(0, mockSelectionManager.LastSetIndex);
        Assert.Equal(Priority.High, mockSelectionManager.LastSetPriority);
    }

    private class CustomMockSelectionManager : PeerSharp.Internals.Framework.IFileSelectionManager
    {
        public bool IsSelectionFinished => true;
        public int TotalSelectedPieces => 0;
        public int ReceivedSelectedPieces => 0;
        public ulong CalculateFinishedSelectedBytes() => 0;
        public float CalculateSelectionProgress() => 0;
        public void SetObserver(PeerSharp.Internals.Framework.IFileSelectionObserver observer) { }
        public FileSelection GetFileSelection(int fileIndex) => new FileSelection();

        public IReadOnlyList<FileSelection> Selections = new List<FileSelection> { new FileSelection() };
        public IReadOnlyList<FileSelection> GetAllFileSelections() => Selections;

        public int LastSetIndex = -1;
        public Priority LastSetPriority = Priority.DoNotDownload;
        public Task SetFileSelectionAsync(int fileIndex, FileSelection selection, CancellationToken ct = default) => Task.CompletedTask;

        public Task SetFilePriorityAsync(int fileIndex, Priority priority, CancellationToken ct = default)
        {
            LastSetIndex = fileIndex;
            LastSetPriority = priority;
            return Task.CompletedTask;
        }

        public Task SetAllFilesPriorityAsync(Priority priority, CancellationToken ct = default) => Task.CompletedTask;
        public void OnPieceVerified(int pieceIndex) { }
        public void Initialize(List<FileSelection>? savedSelection, PiecesProgress pieces) { }
        public void SetBytesProvider(PeerSharp.Internals.Framework.IUnfinishedBytesProvider provider) { }
    }

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
    public async Task StoppedTorrent_PublicSnapshotApis_DoNotThrowAfterInternalsAreStopped()
    {
        string downloadPath = Path.Combine(Path.GetTempPath(), "PeerSharpTests_StoppedApis", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(downloadPath);

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
            new CustomMockSelectionManager(),
            new TorrentTestUtility.MockPeerCommunicationFactory(),
            new TorrentTestUtility.MockTrackerFactory(),
            new TorrentTestUtility.MockGeoIpService(),
            new TorrentTestUtility.MockFileHandleCache(),
            new TorrentTestUtility.MockConnectionGovernor(),
            _timeProvider);

        try
        {
            await torrent.StartAsync(TestContext.Current.CancellationToken);
            await torrent.StopAsync(TestContext.Current.CancellationToken);

            Assert.Equal(TorrentState.Stopped, torrent.State);
            Assert.False(torrent.Started);
            Assert.Equal(1, torrent.FileCount);
            Assert.Single(torrent.GetAllFileInfo());
            Assert.Equal("file.bin", torrent.GetFileInfo(0).Path);
            Assert.Single(torrent.GetAllFileSelections());
            Assert.NotNull(torrent.GetFileSelection(0));
            Assert.NotEmpty(torrent.GetPieceBitfield());
            Assert.Equal(torrent.Hash, torrent.GetResumeData().Hash);
            Assert.Equal(0, torrent.FileTransfer.Downloaded);
            Assert.Equal(0, torrent.FileTransfer.Uploaded);
            Assert.Equal(0, torrent.Peers.ConnectedCount);
            Assert.NotNull(torrent.Trackers.GetTrackers());
        }
        finally
        {
            await torrent.DisposeAsync();
            try { Directory.Delete(downloadPath, true); } catch { }
        }
    }

    [Fact]
    public async Task SetDownloadPathAsync_WorksAfterStop_ReplacingDisposedFiles()
    {
        string originalPath = Path.Combine(Path.GetTempPath(), "PeerSharpTests_PathOriginal", Guid.NewGuid().ToString("N"));
        string newPath = Path.Combine(Path.GetTempPath(), "PeerSharpTests_PathNew", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(originalPath);

        var info = new TorrentFileMetadata();
        info.Info.Name = "test";
        info.Info.Hash = InfoHash.CreateRandom();
        info.Info.PieceSize = 16384;
        info.Info.FullSize = 1000;
        info.Info.Pieces.Add(new byte[20]);
        info.Info.Files.Add(new Internals.TorrentFileEntry { Path = "file.bin", Size = 1000, Offset = 0 });

        var torrent = TorrentTestUtility.CreateMinimal(info, originalPath);

        try
        {
            await torrent.StartAsync(TestContext.Current.CancellationToken);
            await torrent.StopAsync(TestContext.Current.CancellationToken);

            await torrent.SetDownloadPathAsync(newPath);

            Assert.Equal(newPath, torrent.LocalState.DownloadPath);
            Assert.Equal(newPath, torrent.Files.DownloadPath);
        }
        finally
        {
            await torrent.DisposeAsync();
            try { Directory.Delete(originalPath, true); } catch { }
            try { Directory.Delete(newPath, true); } catch { }
        }
    }

    [Fact]
    public async Task OpenStreamAsync_StoppedTorrent_ThrowsInvalidOperationException()
    {
        var torrent = TorrentTestUtility.CreateMinimal();

        await Assert.ThrowsAsync<InvalidOperationException>(() => torrent.OpenStreamAsync(0));
    }

    [Fact]
    public async Task ForceRecheckAsync_WorksAfterStop_ReinitializingFiles()
    {
        string downloadPath = Path.Combine(Path.GetTempPath(), "PeerSharpTests_Recheck", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(downloadPath);

        byte[] content = "verified content"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(downloadPath, "file.bin"), content, TestContext.Current.CancellationToken);

        var info = new TorrentFileMetadata();
        info.Info.Name = "test";
        info.Info.Hash = InfoHash.CreateRandom();
        info.Info.PieceSize = 16384;
        info.Info.FullSize = content.Length;
        info.Info.Pieces.Add(SHA1.HashData(content));
        info.Info.Files.Add(new Internals.TorrentFileEntry { Path = "file.bin", Size = content.Length, Offset = 0 });

        var torrent = TorrentTestUtility.CreateMinimal(info, downloadPath);

        try
        {
            await torrent.StartAsync(TestContext.Current.CancellationToken);
            await torrent.StopAsync(TestContext.Current.CancellationToken);

            int validPieces = await torrent.ForceRecheckAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(1, validPieces);
            Assert.False(torrent.Started);
            Assert.Equal(TorrentState.Stopped, torrent.State);
        }
        finally
        {
            await torrent.DisposeAsync();
            try { Directory.Delete(downloadPath, true); } catch { }
        }
    }

    [Fact]
    public async Task ForceRecheckAsync_ThrowsObjectDisposedException_AfterDispose()
    {
        var torrent = TorrentTestUtility.CreateMinimal();

        await torrent.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => torrent.ForceRecheckAsync());
    }

    [Fact]
    public async Task AttachPeerTransportAsync_ThrowsObjectDisposedException_AfterDispose()
    {
        var torrent = TorrentTestUtility.CreateMinimal();

        await torrent.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            ((IPeerTransportHost)torrent).AttachPeerTransportAsync(Stream.Null, initiator: true));
    }

    [Fact]
    public async Task AttachPeerTransportAsync_ThrowsArgumentNullException_ForNullStream()
    {
        var torrent = TorrentTestUtility.CreateMinimal();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            ((IPeerTransportHost)torrent).AttachPeerTransportAsync(null!, initiator: true));
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
        public List<string> ClosedRoots { get; } = [];

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






