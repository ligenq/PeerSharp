using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Extensions;
using PeerSharp.Messages;
using PeerSharp.PiecePicking;
using System.Net;

using TransferStats = PeerSharp.Internals.TransferStats;

namespace PeerSharp.Tests.Core.Transfers;

public class BlockProcessorTests
{
    private class MockPeerListener : IPeerListener
    {
        public Task HandshakeFinishedAsync(IPeerCommunication peer) => Task.CompletedTask;
        public Task ConnectionClosedAsync(IPeerCommunication peer, int code) => Task.CompletedTask;
        public Task MessageReceivedAsync(IPeerCommunication peer, PeerMessage msg) => Task.CompletedTask;
        public Task ExtendedHandshakeFinishedAsync(IPeerCommunication peer, ExtensionHandshake handshake) => Task.CompletedTask;
        public Task ExtendedMessageReceivedAsync(IPeerCommunication peer, int type, byte[] data) => Task.CompletedTask;
        public Task PexReceivedAsync(IPeerCommunication peer, List<IPEndPoint> added, List<byte> addedFlags, List<IPEndPoint> dropped) => Task.CompletedTask;
        public Task HolepunchMessageReceivedAsync(IPeerCommunication peer, UtHolepunch.MsgId id, IPEndPoint endpoint, UtHolepunch.ErrorCode error) => Task.CompletedTask;
        public Task PortReceivedAsync(IPeerCommunication peer, ushort dhtPort) => Task.CompletedTask;
    }

    [Fact]
    public async Task HandlePeerBlockAsync_AddsBlockToPieceState()
    {
        // Setup
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = 16384;
        metadata.Info.FullSize = 16384; // 1 piece
        var torrent = TorrentTestUtility.CreateMinimal(metadata);

        var piecePicker = new PiecePicker(new TorrentPiecePickerContext(torrent), TimeProvider.System, Random.Shared);
        var pieceStateManager = new PieceStateManager(piecePicker, NullLogger<PieceStateManager>.Instance, 10);

        // Add active piece to state manager so processor can find it
        var pieceState = new PieceState(0, 1);
        pieceStateManager.TryAddPiece(pieceState);

        var requestTracker = new BlockRequestTracker();
        var requestCompletionTracker = new RequestCompletionTracker(requestTracker, TimeProvider.System, (p, o, r) => { });
        var downloader = new TransferStats();

        bool enqueued = false;
        Func<PieceState, Task> enqueuePeerPiece = (ps) =>
        {
            enqueued = true;
            return Task.CompletedTask;
        };

        var processor = new BlockProcessor(new BlockProcessorOptions
        {
            PieceStateManager = pieceStateManager,
            BlockSize = 16384,
            EnqueuePeerPiece = enqueuePeerPiece,
            EnqueueWebSeedPiece = (_, _) => Task.CompletedTask,
            Downloader = downloader,
            RequestCompletionTracker = requestCompletionTracker,
            Torrent = torrent,
            CancelBlockRequest = (_, _, _) => Task.CompletedTask,
            Logger = NullLogger<BlockProcessor>.Instance
        });

        var peer = new PeerCommunication(torrent, new MockPeerListener(), TimeProvider.System);
        var block = new Block(0, 0, 16384);

        // Act
        await processor.HandlePeerBlockAsync(peer, block);

        // Assert
        Assert.True(pieceState.Blocks[0]); // Block marked received
        Assert.Equal(16384, downloader.Downloaded); // Stats updated
        Assert.True(enqueued); // Piece enqueued for processing (verification/writing)
    }

    [Fact]
    public async Task HandlePeerBlockAsync_IgnoresBlockIfPiece_NotActive()
    {
        // Setup
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = 16384;
        metadata.Info.FullSize = 16384;
        var torrent = TorrentTestUtility.CreateMinimal(metadata);

        var piecePicker = new PiecePicker(new TorrentPiecePickerContext(torrent), TimeProvider.System, Random.Shared);
        var pieceStateManager = new PieceStateManager(piecePicker, NullLogger<PieceStateManager>.Instance, 10);
        // Do NOT add piece to state manager

        var requestTracker = new BlockRequestTracker();
        var requestCompletionTracker = new RequestCompletionTracker(requestTracker, TimeProvider.System, (p, o, r) => { });
        var downloader = new TransferStats();

        bool enqueued = false;

        var processor = new BlockProcessor(new BlockProcessorOptions
        {
            PieceStateManager = pieceStateManager,
            BlockSize = 16384,
            EnqueuePeerPiece = (_) => { enqueued = true; return Task.CompletedTask; },
            EnqueueWebSeedPiece = (_, _) => Task.CompletedTask,
            Downloader = downloader,
            RequestCompletionTracker = requestCompletionTracker,
            Torrent = torrent,
            CancelBlockRequest = (_, _, _) => Task.CompletedTask,
            Logger = NullLogger<BlockProcessor>.Instance
        });

        var peer = new PeerCommunication(torrent, new MockPeerListener(), TimeProvider.System);
        var block = new Block(0, 0, 16384);

        // Act
        await processor.HandlePeerBlockAsync(peer, block);

        // Assert
        Assert.False(enqueued);
        Assert.Equal(0, downloader.Downloaded);
        // Block should be disposed, but we can't easily check that without a mock Block or careful memory inspection
    }
}
