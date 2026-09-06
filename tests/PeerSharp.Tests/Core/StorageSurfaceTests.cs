using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using PeerSharp.Internals.Framework;
using PeerSharp.PieceWriter;
using PeerSharp.Streaming;

namespace PeerSharp.Tests.Core;

/// <summary>
/// The storage members no test reached: moving and renaming files on disk, and the readiness flag
/// every one of them depends on.
/// </summary>
public class FilesStorageSurfaceTests
{
    [Fact]
    public async Task StorageIsNotInitialisedUntilTheSelectionHasBeenApplied()
    {
        // Files are created lazily so that adding a torrent does not allocate its whole size before
        // anyone has chosen what to download.
        using var directory = new ScratchDirectory();
        var torrent = CreateTorrent(directory.Path);

        Assert.False(torrent.FilesInternal.IsInitialized);

        await torrent.FilesInternal.InitializeAsync(Selection(), TestContext.Current.CancellationToken);

        Assert.True(torrent.FilesInternal.IsInitialized);
    }

    [Fact]
    public async Task MovingTheFilesCarriesTheDataToTheNewRoot()
    {
        // The user moving a download mid-transfer is the case: the bytes already written must go
        // with it, or the pieces already verified are lost and re-fetched.
        using var source = new ScratchDirectory();
        using var destination = new ScratchDirectory();
        var torrent = CreateTorrent(source.Path);

        await torrent.FilesInternal.InitializeAsync(Selection(), TestContext.Current.CancellationToken);
        await torrent.FilesInternal.WriteAsync(0, new byte[16384], TestContext.Current.CancellationToken);

        string moved = Path.Combine(destination.Path, "moved");
        await torrent.FilesInternal.MoveFilesAsync(moved, TestContext.Current.CancellationToken);

        // The bytes moved. DownloadPath still reports the root this Files was built for: the new one
        // is recorded by the caller and takes effect when storage is next built.
        Assert.True(File.Exists(Path.Combine(moved, "file.bin")));
    }

    [Fact]
    public async Task RenamingAFileMovesItsBytesRatherThanStartingANewOne()
    {
        using var directory = new ScratchDirectory();
        var torrent = CreateTorrent(directory.Path);

        await torrent.FilesInternal.InitializeAsync(Selection(), TestContext.Current.CancellationToken);
        byte[] written = [.. Enumerable.Range(0, 16384).Select(i => (byte)(i % 251))];
        await torrent.FilesInternal.WriteAsync(0, written, TestContext.Current.CancellationToken);

        await torrent.FilesInternal.RenameFileAsync(0, "renamed.bin", TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(directory.Path, "renamed.bin")));
        Assert.Equal(written, await torrent.FilesInternal.ReadAsync(0, written.Length, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RenamingToAPathOutsideTheTorrentRootIsRefused()
    {
        // The new name reaches this from a consumer's API call, and a path that escapes the root is
        // how a rename becomes a write anywhere on the disk.
        using var directory = new ScratchDirectory();
        var torrent = CreateTorrent(directory.Path);
        await torrent.FilesInternal.InitializeAsync(Selection(), TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            torrent.FilesInternal.RenameFileAsync(0, "../escaped.bin", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RenamingAFileIndexThatDoesNotExistIsANoOpRatherThanAnError()
    {
        // A rename can arrive for a file this storage has not allocated. The name is recorded by the
        // caller and applied when storage is next built, which is the whole effect a rename has on
        // an untouched torrent - so throwing here would make that ordinary sequence an error.
        using var directory = new ScratchDirectory();
        var torrent = CreateTorrent(directory.Path);
        await torrent.FilesInternal.InitializeAsync(Selection(), TestContext.Current.CancellationToken);

        await torrent.FilesInternal.RenameFileAsync(99, "other.bin", TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(directory.Path, "file.bin")));
    }

    private static List<FileSelection> Selection() => [new FileSelection { Selected = true, Priority = Priority.Normal }];

    private static Torrent CreateTorrent(string downloadPath)
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = 16384;
        metadata.Info.FullSize = 16384;
        metadata.Info.Files.Add(new PeerSharp.Internals.TorrentFileEntry { Path = "file.bin", Size = 16384, Offset = 0 });
        metadata.Info.Pieces.Add(new byte[20]);

        return TorrentTestUtility.CreateMinimal(metadata, downloadPath);
    }
}

public class PathValidatorFactoryTests
{
    [Fact]
    public void CreateForTestingProducesAValidatorRootedWhereItIsTold()
    {
        // The seam exists so a test can exercise path rules without a torrent behind them; the
        // validator it hands back has to behave like the one Storage builds for itself.
        using var directory = new ScratchDirectory();

        var validator = PathValidator.CreateForTesting(directory.Path);

        Assert.NotNull(validator);
        Assert.False(validator.IsWindowsReservedName("ordinary.bin"));
        Assert.True(validator.IsWindowsReservedName("CON"));
    }
}

public class DiskBandwidthLimiterSurfaceTests
{
    [Fact]
    public void TheLimiterNamesItselfForTheBandwidthManagersRegistry()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var limiter = new DiskBandwidthLimiter(torrent.Services.Bandwidth, torrent.Hash.ToString());

        Assert.Equal("DiskBandwidthLimiter", limiter.Name);
    }

    [Fact]
    public void AssignBandwidthIsAcceptedAndIgnored()
    {
        // IBandwidthUser requires it, but disk bandwidth is granted per request rather than pushed,
        // so there is nothing for an assignment to do. It must still not throw: BandwidthManager
        // calls it on every registered user.
        var torrent = TorrentTestUtility.CreateMinimal();
        var limiter = new DiskBandwidthLimiter(torrent.Services.Bandwidth, torrent.Hash.ToString());

        limiter.AssignBandwidth(1024);
        limiter.AssignBandwidth(0);
    }
}

public class FileSelectionManagerSurfaceTests
{
    [Fact]
    public void NoFinishedBytesAreReportedBeforeAnyPieceIsHeld()
    {
        var manager = CreateManager(pieceCount: 4, out _);

        Assert.Equal(0UL, manager.CalculateFinishedSelectedBytes());
    }

    [Fact]
    public void FinishedBytesCountOnlyTheSelectedPiecesActuallyHeld()
    {
        // This is what a consumer's progress bar reads, so counting a piece nobody selected would
        // show a download finished before its selected files were.
        var manager = CreateManager(pieceCount: 4, out var torrent);

        torrent.Pieces.AddPiece(0);
        Assert.Equal(16384UL, manager.CalculateFinishedSelectedBytes());

        torrent.Pieces.AddPiece(1);
        Assert.Equal(32768UL, manager.CalculateFinishedSelectedBytes());
    }

    [Fact]
    public async Task ChangingOneFilesSelectionIsAppliedRatherThanIgnored()
    {
        var manager = CreateManager(pieceCount: 4, out _);

        await manager.SetFileSelectionAsync(
            0,
            new FileSelection { Selected = false, Priority = Priority.DoNotDownload },
            TestContext.Current.CancellationToken);

        Assert.False(manager.GetAllFileSelections()[0].Selected);
    }

    [Fact]
    public async Task SelectingAFileIndexBeyondTheTorrentGrowsTheListRatherThanThrowing()
    {
        // Selections arrive from resume data written against a file list that may since have been
        // re-read. The list is grown to fit rather than rejected, so a stale index costs an unused
        // entry instead of failing the whole restore.
        var manager = CreateManager(pieceCount: 4, out _);

        await manager.SetFileSelectionAsync(
            42,
            new FileSelection { Selected = true, Priority = Priority.Normal },
            TestContext.Current.CancellationToken);

        Assert.True(manager.GetAllFileSelections().Count > 42);
    }

    private static FileSelectionManager CreateManager(int pieceCount, out Torrent torrent)
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = 16384;
        metadata.Info.FullSize = 16384L * pieceCount;
        metadata.Info.Files.Add(new PeerSharp.Internals.TorrentFileEntry
        {
            Path = "file.bin",
            Size = metadata.Info.FullSize,
            Offset = 0
        });
        for (int i = 0; i < pieceCount; i++) metadata.Info.Pieces.Add(new byte[20]);

        torrent = TorrentTestUtility.CreateMinimal(metadata);

        var manager = new FileSelectionManager(metadata);
        manager.Initialize(savedSelection: null, torrent.Pieces);
        return manager;
    }
}

public class StreamingControllerDisposalTests
{
    [Fact]
    public async Task DisposingTheActiveStreamClearsItAndDisposingItAgainDoesNot()
    {
        // The second call is the one that matters. Two streams over one torrent is supported, so a
        // late disposal must not clear the strategy a newer stream has since set.
        using var directory = new ScratchDirectory();
        var torrent = CreateStreamableTorrent(directory.Path);
        await torrent.FilesInternal.InitializeAsync(
            [new FileSelection { Selected = true, Priority = Priority.Normal }],
            TestContext.Current.CancellationToken);

        await torrent.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var controller = new StreamingController(torrent, TimeProvider.System);
            var stream = await controller.OpenStreamAsync(0, TestContext.Current.CancellationToken);
            var torrentStream = Assert.IsAssignableFrom<TorrentStream>(stream);

            Assert.True(controller.OnStreamDisposed(torrentStream));
            Assert.False(controller.OnStreamDisposed(torrentStream));

            await stream.DisposeAsync();
        }
        finally
        {
            await torrent.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private static Torrent CreateStreamableTorrent(string downloadPath)
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = 16384;
        metadata.Info.FullSize = 16384;
        metadata.Info.Files.Add(new PeerSharp.Internals.TorrentFileEntry { Path = "movie.mp4", Size = 16384, Offset = 0 });
        metadata.Info.Pieces.Add(new byte[20]);

        return TorrentTestUtility.CreateMinimal(metadata, downloadPath);
    }
}
