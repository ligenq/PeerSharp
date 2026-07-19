using PeerSharp.Internals;
using PeerSharp.Internals.Framework;

namespace PeerSharp.Tests.Core;

/// <summary>
/// BEP 53: Verifies that a magnet link's "so=" file selection is applied to the torrent
/// once metadata is available, and only then.
/// </summary>
public class TorrentSelectOnlyTests
{
    [Fact]
    public async Task Apply_DeselectsFilesNotInTheSelection_AndClearsPending()
    {
        var torrent = CreateTorrent(CreateMultiFileMetadata());
        try
        {
            torrent.PendingSelectOnlyFileIndices = [0, 2];

            await torrent.ApplyPendingSelectOnlyFileIndicesAsync();

            Assert.True(torrent.GetFileSelection(0).Selected);
            Assert.False(torrent.GetFileSelection(1).Selected);
            Assert.Equal(Priority.DoNotDownload, torrent.GetFileSelection(1).Priority);
            Assert.True(torrent.GetFileSelection(2).Selected);
            Assert.Null(torrent.PendingSelectOnlyFileIndices);
        }
        finally
        {
            await torrent.DisposeAsync();
        }
    }

    [Fact]
    public async Task Apply_WithoutMetadata_KeepsSelectionPending()
    {
        // Magnet-style metadata: hash only, no piece hashes yet
        var metadata = new TorrentFileMetadata();
        metadata.Info.Version = TorrentVersion.V1;
        metadata.Info.Hash = new InfoHash(Enumerable.Range(0, 20).Select(i => (byte)i).ToArray());
        metadata.Info.PieceSize = 16384;

        var torrent = CreateTorrent(metadata);
        try
        {
            torrent.PendingSelectOnlyFileIndices = [0];

            await torrent.ApplyPendingSelectOnlyFileIndicesAsync();

            // Must stay pending so it can be applied when metadata arrives
            Assert.NotNull(torrent.PendingSelectOnlyFileIndices);
        }
        finally
        {
            await torrent.DisposeAsync();
        }
    }

    [Fact]
    public async Task Apply_ResumeDataSelection_WinsOverMagnetSelection()
    {
        var torrent = CreateTorrent(CreateMultiFileMetadata());
        try
        {
            // A restored session already carries an explicit user file selection
            torrent.LocalState.Selection = [new FileSelection(), new FileSelection(), new FileSelection()];
            torrent.PendingSelectOnlyFileIndices = [0];

            await torrent.ApplyPendingSelectOnlyFileIndicesAsync();

            Assert.True(torrent.GetFileSelection(0).Selected);
            Assert.True(torrent.GetFileSelection(1).Selected);
            Assert.True(torrent.GetFileSelection(2).Selected);
            Assert.Null(torrent.PendingSelectOnlyFileIndices);
        }
        finally
        {
            await torrent.DisposeAsync();
        }
    }

    [Fact]
    public async Task Apply_OutOfRangeIndices_AreIgnored()
    {
        var torrent = CreateTorrent(CreateMultiFileMetadata());
        try
        {
            torrent.PendingSelectOnlyFileIndices = [1, 99];

            await torrent.ApplyPendingSelectOnlyFileIndicesAsync();

            Assert.False(torrent.GetFileSelection(0).Selected);
            Assert.True(torrent.GetFileSelection(1).Selected);
            Assert.False(torrent.GetFileSelection(2).Selected);
        }
        finally
        {
            await torrent.DisposeAsync();
        }
    }

    [Fact]
    public async Task Apply_IndicesReferToVisibleFiles_PaddingFilesAreSkipped()
    {
        // BEP 47 padding file between the two real files: "so=" indices refer to the
        // user-visible file list, so index 1 must select f2 (internal index 2)
        var metadata = new TorrentFileMetadata();
        metadata.Info.Version = TorrentVersion.V1;
        metadata.Info.Hash = new InfoHash(Enumerable.Range(0, 20).Select(i => (byte)i).ToArray());
        metadata.Info.PieceSize = 100;
        metadata.Info.FullSize = 300;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "f1", Size = 100, Offset = 0 });
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = ".pad/50", Size = 50, Offset = 100, IsPadding = true });
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "f2", Size = 150, Offset = 150 });
        metadata.Info.Pieces.Add(new byte[20]);
        metadata.Info.Pieces.Add(new byte[20]);
        metadata.Info.Pieces.Add(new byte[20]);

        var torrent = CreateTorrent(metadata);
        try
        {
            torrent.PendingSelectOnlyFileIndices = [1];

            await torrent.ApplyPendingSelectOnlyFileIndicesAsync();

            Assert.False(torrent.GetFileSelection(0).Selected); // f1 deselected
            Assert.True(torrent.GetFileSelection(1).Selected);  // f2 kept
        }
        finally
        {
            await torrent.DisposeAsync();
        }
    }

    private static TorrentFileMetadata CreateMultiFileMetadata()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Version = TorrentVersion.V1;
        metadata.Info.Hash = new InfoHash(Enumerable.Range(0, 20).Select(i => (byte)i).ToArray());
        metadata.Info.PieceSize = 100;
        metadata.Info.FullSize = 300;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "f0", Size = 100, Offset = 0 });
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "f1", Size = 100, Offset = 100 });
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "f2", Size = 100, Offset = 200 });
        metadata.Info.Pieces.Add(new byte[20]);
        metadata.Info.Pieces.Add(new byte[20]);
        metadata.Info.Pieces.Add(new byte[20]);
        return metadata;
    }

    private static Torrent CreateTorrent(TorrentFileMetadata metadata)
    {
        var settings = new Settings();
        settings.Files.DefaultDownloadPath = Path.Combine(Path.GetTempPath(), "PeerSharpTests_SelectOnly", Guid.NewGuid().ToString("N"));

        return Torrent.Create(
            metadata,
            settings,
            new TorrentTestUtility.MockBandwidthManager(),
            new TorrentTestUtility.MockAlertsManager(),
            new FileSelectionManager(metadata),
            new TorrentTestUtility.MockPeerCommunicationFactory(),
            new TorrentTestUtility.MockTrackerFactory(),
            new TorrentTestUtility.MockGeoIpService(),
            new TorrentTestUtility.MockFileHandleCache(),
            new TorrentTestUtility.MockConnectionGovernor(),
            TimeProvider.System);
    }
}
