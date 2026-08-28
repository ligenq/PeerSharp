using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Transfers;
using PeerSharp.Messages;
using PeerSharp.Tests.Core.Peers;

namespace PeerSharp.Tests.Core.Transfers;

/// <summary>
/// What happens to everyone else's outstanding requests when a piece finishes.
/// </summary>
/// <remarks>
/// <para>
/// A block may be owed by more than one peer at a time - the request strategies duplicate a stalled
/// block on purpose - so completing a piece leaves requests for it scattered across peers who have
/// not answered yet. Those have to come out of the tracker, or they sit there being counted against
/// the duplication cap for a block nobody will ever ask for again, and the timeout machinery keeps
/// examining them.
/// </para>
/// <para>
/// Cancels are sent in end game only, which is deliberate: outside it the duplicates are few, and a
/// cancel races the data already on the wire often enough not to be worth the message. Pinned here
/// so the asymmetry reads as a decision rather than an oversight.
/// </para>
/// </remarks>
public class PieceCompletionHandlerTests
{
    private const int BlockSize = 16384;

    [Fact(Timeout = 30_000)]
    public async Task RequestsForTheCompletedPieceAreDroppedForEveryPeer()
    {
        var context = new Context();
        var slow = context.AddPeer();
        var other = context.AddPeer();

        context.Request(slow, piece: 3, block: 0);
        context.Request(other, piece: 3, block: 1);
        context.Request(other, piece: 3, block: 2);

        await context.Handler.HandlePieceCompletedAsync(3, endGameMode: false);

        Assert.Equal(0, context.Tracker.GetPendingRequestCount(3, 0));
        Assert.Equal(0, context.Tracker.GetPendingRequestCount(3, BlockSize));
        Assert.Equal(0, context.Tracker.GetPendingRequestCount(3, 2 * BlockSize));
        Assert.All(context.Tracker.EnumeratePeerRequests().Values, requests => Assert.Empty(requests.AsEnumerable()));
    }

    [Fact(Timeout = 30_000)]
    public async Task RequestsForOtherPiecesAreLeftAlone()
    {
        // The obvious way to get this wrong is to clear the peer's whole collection rather than the
        // entries for one piece, which would throw away requests still in flight and stall them until
        // the timeout machinery noticed.
        var context = new Context();
        var peer = context.AddPeer();

        context.Request(peer, piece: 3, block: 0);
        context.Request(peer, piece: 4, block: 0);
        context.Request(peer, piece: 5, block: 7);

        await context.Handler.HandlePieceCompletedAsync(3, endGameMode: false);

        Assert.Equal(0, context.Tracker.GetPendingRequestCount(3, 0));
        Assert.Equal(1, context.Tracker.GetPendingRequestCount(4, 0));
        Assert.Equal(1, context.Tracker.GetPendingRequestCount(5, 7 * BlockSize));

        Assert.True(context.Tracker.TryGetPeerRequests(peer, out var requests));
        Assert.Equal(2, requests.AsEnumerable().Count);
    }

    [Fact(Timeout = 30_000)]
    public async Task EachDroppedRequestIsReportedOnce()
    {
        // The callback is what clears the block index; the handler only clears the per-peer map. Miss
        // one and the block keeps counting towards the duplication cap forever.
        var context = new Context();
        var peer = context.AddPeer();

        context.Request(peer, piece: 3, block: 0);
        context.Request(peer, piece: 3, block: 1);

        await context.Handler.HandlePieceCompletedAsync(3, endGameMode: false);

        Assert.Equal(
            [(3, 0, peer), (3, BlockSize, peer)],
            context.Removed.OrderBy(r => r.Offset).ToArray());
    }

    [Fact(Timeout = 30_000)]
    public async Task NothingIsCancelledOutsideEndGame()
    {
        var context = new Context();
        var peer = context.AddPeer();
        context.Request(peer, piece: 3, block: 0);

        await context.Handler.HandlePieceCompletedAsync(3, endGameMode: false);

        Assert.Empty(peer.Sent);
    }

    [Fact(Timeout = 30_000)]
    public async Task EveryDroppedRequestIsCancelledInEndGame()
    {
        // End game asks several peers for the same block at once, so once it lands the others are
        // about to send a copy of something already on disk. That is the case worth a cancel.
        var context = new Context();
        var first = context.AddPeer();
        var second = context.AddPeer();

        context.Request(first, piece: 3, block: 0);
        context.Request(second, piece: 3, block: 0);
        context.Request(second, piece: 3, block: 1);
        context.Request(second, piece: 9, block: 0);

        await context.Handler.HandlePieceCompletedAsync(3, endGameMode: true);

        var cancels = first.Sent.Concat(second.Sent).ToArray();
        Assert.All(cancels, message => Assert.Equal(MessageId.Cancel, message.Id));
        Assert.Equal(3, cancels.Length);
        Assert.All(cancels, message => Assert.Equal(3, message.PieceIndex));
        Assert.Equal([0, 0, BlockSize], cancels.Select(m => m.BlockOffset).Order());

        // The request on another piece is neither dropped nor cancelled.
        Assert.Equal(1, context.Tracker.GetPendingRequestCount(9, 0));
    }

    [Fact(Timeout = 30_000)]
    public async Task CompletingAPieceNobodyOwesIsHarmless()
    {
        var context = new Context();
        var peer = context.AddPeer();
        context.Request(peer, piece: 3, block: 0);

        await context.Handler.HandlePieceCompletedAsync(7, endGameMode: true);

        Assert.Empty(context.Removed);
        Assert.Empty(peer.Sent);
        Assert.Equal(1, context.Tracker.GetPendingRequestCount(3, 0));
    }

    private sealed class Context
    {
        public Context()
        {
            var metadata = new TorrentFileMetadata();
            metadata.Info.PieceSize = BlockSize;
            metadata.Info.FullSize = BlockSize * 32L;
            Torrent = TorrentTestUtility.CreateMinimal(metadata);

            Handler = new PieceCompletionHandler(
                Tracker,
                (piece, offset, peer) =>
                {
                    Removed.Add((piece, offset, peer));

                    // What FileTransfer passes: clearing the per-peer entry is only half of it, the
                    // block index is cleared here.
                    Tracker.RemoveBlockRequest(piece, offset, peer);
                },
                Torrent,
                NullLogger<PieceCompletionHandler>.Instance);
        }

        public BlockRequestTracker Tracker { get; } = new();
        public PieceCompletionHandler Handler { get; }
        public Torrent Torrent { get; }
        public List<(int Piece, int Offset, PeerCommunication Peer)> Removed { get; } = [];

        public RecordingPeer AddPeer() => new(Torrent);

        public void Request(RecordingPeer peer, int piece, int block)
        {
            Tracker.AddBlockRequest(piece, block * BlockSize, peer, new BlockRequest
            {
                PieceIndex = piece,
                Offset = block * BlockSize,
                Length = BlockSize,
                Timestamp = DateTimeOffset.UtcNow
            });
        }
    }

    /// <summary>
    /// A peer that keeps what it was asked to send. <see cref="PeerCommunication.SendMessageAsync"/>
    /// drops everything on the floor while disconnected, which would make the cancels invisible.
    /// </summary>
    private sealed class RecordingPeer(Torrent torrent)
        : PeerCommunication(torrent, new NullPeerListener(), TimeProvider.System)
    {
        public List<PeerMessage> Sent { get; } = [];

        public override Task SendMessageAsync(PeerMessage msg)
        {
            Sent.Add(msg);
            return Task.CompletedTask;
        }
    }
}
