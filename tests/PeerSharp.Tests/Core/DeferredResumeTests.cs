using System.Reflection;
using System.Text.Json;
using PeerSharp.Internals;
using PeerSharp.Internals.Transfers;
using PeerSharp.PieceWriter;

namespace PeerSharp.Tests.Core;

public sealed class DeferredResumeTests
{
    [Fact]
    public async Task StoppedTorrentKeepsProgressAndExportDataWithoutMaterializingBlocks()
    {
        byte[] data = new byte[32768];
        data.AsSpan(0, 16384).Fill(42);
        var state = new TorrentStateData
        {
            UnfinishedPieces = [new() { Index = 0, Blocks = [true, false], Data = data }]
        };
        var resume = new TorrentResumeData { Data = JsonSerializer.SerializeToUtf8Bytes(state, PeerSharpJsonContext.Default.TorrentStateData) };
        await using var torrent = TorrentTestUtility.CreateMinimal(Metadata(), resumeData: resume);

        Assert.False(torrent.Started);
        Assert.Equal(0, ActivePieces(torrent.FileTransferInternal).Count);
        Assert.Empty(torrent.LocalState.UnfinishedPieces);
        Assert.Equal(16384, torrent.FileTransferInternal.GetUnfinishedBytes());
        Assert.Equal(16384, torrent.GetFileInfo(0).DownloadedBytes);

        var exported = JsonSerializer.Deserialize(torrent.GetResumeData().Data, PeerSharpJsonContext.Default.TorrentStateData)!;
        Assert.Equal(data, Assert.Single(exported.UnfinishedPieces).Data);
        Assert.Equal(0, ActivePieces(torrent.FileTransferInternal).Count);

        torrent.FileTransferInternal.RestoreDeferredPieces();
        Assert.Equal(1, ActivePieces(torrent.FileTransferInternal).Count);
        Assert.Equal(data, Assert.Single(torrent.FileTransferInternal.GetUnfinishedPiecesState()).Data);
        torrent.FileTransferInternal.RestoreDeferredPieces();
        Assert.Equal(1, ActivePieces(torrent.FileTransferInternal).Count);
    }

    [Fact]
    public async Task StartingRestoresDeferredBlocksBeforeTransfersBegin()
    {
        string root = Path.Combine(Path.GetTempPath(), "PeerSharpDeferred-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] data = new byte[32768];
            data.AsSpan(0, 16384).Fill(77);
            var state = new TorrentStateData { UnfinishedPieces = [new() { Index = 0, Blocks = [true, false], Data = data }] };
            var resume = new TorrentResumeData { Data = JsonSerializer.SerializeToUtf8Bytes(state, PeerSharpJsonContext.Default.TorrentStateData) };
            await using var torrent = TorrentTestUtility.CreateMinimal(Metadata(), root, resumeData: resume);
            torrent.Settings.Connection.EnableWebSeeds = false;
            torrent.Settings.Connection.EnableTcpIn = false;
            torrent.Settings.Connection.EnableTcpOut = false;
            torrent.Settings.Connection.EnableUtpIn = false;
            torrent.Settings.Connection.EnableUtpOut = false;

            await torrent.StartCoreAsync(TestContext.Current.CancellationToken);

            Assert.True(torrent.Started);
            Assert.Equal(1, ActivePieces(torrent.FileTransferInternal).Count);
            Assert.Equal(data, Assert.Single(torrent.FileTransferInternal.GetUnfinishedPiecesState()).Data);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RecheckCompletedPiecesAreNotRestoredAsUnfinished()
    {
        await using var torrent = TorrentTestUtility.CreateMinimal(Metadata());
        torrent.FileTransferInternal.DeferUnfinishedPiecesState([new() { Index = 0, Blocks = [true, false], Data = new byte[32768] }]);
        torrent.Pieces.AddPiece(0);
        torrent.FileTransferInternal.RestoreDeferredPieces();
        Assert.Empty(torrent.FileTransferInternal.GetUnfinishedPiecesState());
        Assert.Equal(0, torrent.FileTransferInternal.GetUnfinishedBytes());
    }

    private static TorrentFileMetadata Metadata()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Hash = InfoHash.CreateRandom();
        metadata.Info.PieceSize = 32768;
        metadata.Info.FullSize = 32768;
        metadata.Info.Pieces.Add(new byte[20]);
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "data.bin", Size = 32768 });
        return metadata;
    }

    private static PieceStateManager ActivePieces(FileTransfer transfer) => (PieceStateManager)typeof(FileTransfer)
        .GetField("_pieceStateManager", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transfer)!;
}
