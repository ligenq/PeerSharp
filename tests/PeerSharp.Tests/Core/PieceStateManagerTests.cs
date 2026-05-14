using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using PeerSharp.Internals.Transfers;
using PeerSharp.PiecePicking;

namespace PeerSharp.Tests.Core;

public class PieceStateManagerTests
{
    [Fact]
    public void PruneStalePieces_RemovesUnavailablePieces_WhenAtCapacity()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = 16384;
        metadata.Info.FullSize = 16384 * 3;
        var torrent = TorrentTestUtility.CreateMinimal(metadata);

        var piecePicker = new PiecePicker(new TorrentPiecePickerContext(torrent), TimeProvider.System, Random.Shared);
        var manager = new PieceStateManager(piecePicker, NullLogger<PieceStateManager>.Instance, maxActivePieces: 1);

        var stale = new PieceState(0, 1);
        var available = new PieceState(1, 1);

        manager.TryAddPiece(stale);
        manager.TryAddPiece(available);

        piecePicker.IncrementAvailability(1);

        manager.PruneStalePieces();

        Assert.False(manager.ContainsPiece(0));
        Assert.True(manager.ContainsPiece(1));
        Assert.Equal(1, manager.Count);
    }

    [Fact]
    public void Dispose_DisposesAllPieceStates()
    {
        var metadata = new TorrentFileMetadata();
        var torrent = TorrentTestUtility.CreateMinimal(metadata);
        var piecePicker = new PiecePicker(new TorrentPiecePickerContext(torrent), TimeProvider.System, Random.Shared);
        var manager = new PieceStateManager(piecePicker, NullLogger<PieceStateManager>.Instance, maxActivePieces: 10);

        var state1 = new PieceState(0, 1);
        var block1 = new PeerSharp.Core.Block(16384);
        var peer = new PeerSharp.Internals.Peers.PeerCommunication(torrent, null!, TimeProvider.System);
        state1.TryAddBlock(0, block1, peer);

        var state2 = new PieceState(1, 1);
        var block2 = new PeerSharp.Core.Block(16384);
        state2.TryAddBlock(0, block2, peer);

        manager.TryAddPiece(state1);
        manager.TryAddPiece(state2);

        manager.Dispose();

        Assert.Equal(0, manager.Count);
        Assert.False(manager.ContainsPiece(0));
        Assert.False(manager.ContainsPiece(1));

        // Blocks should be disposed
        Assert.Throws<ObjectDisposedException>(() => block1.Buffer);
        Assert.Throws<ObjectDisposedException>(() => block2.Buffer);
    }
}
