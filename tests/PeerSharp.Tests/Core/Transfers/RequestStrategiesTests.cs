using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Transfers;
using PeerSharp.Messages;
using System.Net;

namespace PeerSharp.Tests.Core.Transfers;

public class RequestStrategiesTests
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

    private PeerCommunication CreatePeer()
    {
        var metadata = new TorrentFileMetadata();
        var torrent = TorrentTestUtility.CreateMinimal(metadata);
        var peer = new PeerCommunication(torrent, new MockPeerListener(), TimeProvider.System);
        // Use reflection to set private fields if needed, or just use the public API
        // PeerCommunication is hard to setup fully without a real socket, but we only need it as a key/identity here
        return peer;
    }

    [Fact]
    public void StandardStrategy_IsBlockRequestable_ReturnsTrue_WhenNoPendingRequest()
    {
        var tracker = new BlockRequestTracker();
        var strategy = new StandardBlockRequestStrategy(
            tracker,
            TimeProvider.System,
            _ => 1000,
            16384);

        var pieceState = new PieceState(0, 1); // 1 block
        var peer = CreatePeer();

        // Block 0 not received, no pending requests
        bool result = strategy.IsBlockRequestable(pieceState, 0, 0, peer, isPeerFast: false);

        Assert.True(result);
    }

    [Fact]
    public void StandardStrategy_IsBlockRequestable_ReturnsFalse_WhenAlreadyReceived()
    {
        var tracker = new BlockRequestTracker();
        var strategy = new StandardBlockRequestStrategy(
            tracker,
            TimeProvider.System,
            _ => 1000,
            16384);

        var pieceState = new PieceState(0, 1);
        var peer = CreatePeer();

        // Mark block 0 as received
        // We need to use reflection or a test helper to set the internal bit array if public API doesn't allow direct set without validation
        // PieceState.Blocks is a bool array, we can't set it directly as it's read-only property returning the array?
        // Is PieceState.Blocks an array or a property?
        // It's `public bool[] Blocks { get; }`. Arrays are mutable.
        pieceState.Blocks[0] = true;

        bool result = strategy.IsBlockRequestable(pieceState, 0, 0, peer, isPeerFast: false);

        Assert.False(result);
    }

    [Fact]
    public void StandardStrategy_IsBlockRequestable_ReturnsFalse_WhenPendingRequestExists_AndPeerNotFast()
    {
        var tracker = new BlockRequestTracker();
        var timeProvider = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var strategy = new StandardBlockRequestStrategy(
            tracker,
            timeProvider,
            _ => 5000,
            16384);

        var pieceState = new PieceState(0, 1);
        var peer1 = CreatePeer();
        var peer2 = CreatePeer();

        // Add a pending request from peer1
        tracker.AddBlockRequest(0, 0, peer1, new BlockRequest { Timestamp = timeProvider.GetUtcNow() });

        // Try to request from peer2, but peer2 is not "fast" (not unchoked/high speed enough to warrant duplicate)
        bool result = strategy.IsBlockRequestable(pieceState, 0, 0, peer2, isPeerFast: false);

        Assert.False(result);
    }

    [Fact]
    public void StandardStrategy_IsBlockRequestable_ReturnsTrue_WhenPendingRequestStale_AndPeerIsFast()
    {
        var tracker = new BlockRequestTracker();
        var timeProvider = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var strategy = new StandardBlockRequestStrategy(
            tracker,
            timeProvider,
            _ => 1000, // Soft timeout 1s
            16384);

        var pieceState = new PieceState(0, 1);
        var peer1 = CreatePeer();
        var peer2 = CreatePeer();

        // Add a pending request from peer1
        tracker.AddBlockRequest(0, 0, peer1, new BlockRequest { Timestamp = timeProvider.GetUtcNow() });

        // Advance time past soft timeout
        timeProvider.Advance(TimeSpan.FromSeconds(2));

        // Try to request from peer2, who IS fast
        bool result = strategy.IsBlockRequestable(pieceState, 0, 0, peer2, isPeerFast: true);

        Assert.True(result);
    }

    /// <summary>
    /// Duplication of a stalled block stops once enough peers owe it.
    ///
    /// <para>
    /// Staleness is measured from the oldest outstanding request, and that age only grows until the
    /// block arrives - so a block that passes the soft timeout stays past it permanently, and without a
    /// cap every fast peer keeps qualifying for a copy. A live run duplicated one block 183 times and
    /// delivered 42,769 blocks that were already held, a tenth of everything downloaded, because the
    /// cancel sent when the first copy lands cannot overtake data already on the wire.
    /// </para>
    /// </summary>
    [Fact]
    public void StandardStrategy_StopsDuplicating_OnceEnoughPeersOweTheBlock()
    {
        var tracker = new BlockRequestTracker();
        var timeProvider = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var strategy = new StandardBlockRequestStrategy(
            tracker,
            timeProvider,
            _ => 1000,
            16384);

        var pieceState = new PieceState(0, 1);
        var original = CreatePeer();

        tracker.AddBlockRequest(0, 0, original, new BlockRequest { Timestamp = timeProvider.GetUtcNow() });

        // Well past the soft timeout, and it stays past it however long we wait.
        timeProvider.Advance(TimeSpan.FromSeconds(30));

        // The first duplicate is the point of the feature: a second peer may be asked.
        var second = CreatePeer();
        Assert.True(strategy.IsBlockRequestable(pieceState, 0, 0, second, isPeerFast: true));
        tracker.AddBlockRequest(0, 0, second, new BlockRequest { Timestamp = timeProvider.GetUtcNow() });

        // Two peers already owe it, so further fast peers must be refused - previously they were not,
        // and the request kept being duplicated for as long as the block was outstanding.
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        Assert.False(strategy.IsBlockRequestable(pieceState, 0, 0, CreatePeer(), isPeerFast: true));
        Assert.False(strategy.IsBlockRequestable(pieceState, 0, 0, CreatePeer(), isPeerFast: true));
    }

    /// <summary>
    /// The cap tracks what is outstanding, not what has ever been sent: when a duplicate is cancelled or
    /// times out, another peer becomes eligible again. Otherwise a block whose holders all vanished
    /// would never be re-requested.
    /// </summary>
    [Fact]
    public void StandardStrategy_AllowsAnotherDuplicate_AfterAnOutstandingOneClears()
    {
        var tracker = new BlockRequestTracker();
        var timeProvider = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var strategy = new StandardBlockRequestStrategy(
            tracker,
            timeProvider,
            _ => 1000,
            16384);

        var pieceState = new PieceState(0, 1);
        var first = CreatePeer();
        var second = CreatePeer();

        tracker.AddBlockRequest(0, 0, first, new BlockRequest { Timestamp = timeProvider.GetUtcNow() });
        tracker.AddBlockRequest(0, 0, second, new BlockRequest { Timestamp = timeProvider.GetUtcNow() });
        timeProvider.Advance(TimeSpan.FromSeconds(30));

        Assert.False(strategy.IsBlockRequestable(pieceState, 0, 0, CreatePeer(), isPeerFast: true));

        tracker.RemoveBlockRequest(0, 0, second);

        Assert.True(strategy.IsBlockRequestable(pieceState, 0, 0, CreatePeer(), isPeerFast: true));
    }

    /// <summary>
    /// End game still duplicates broadly, but stops after four peers owe the block. That preserves
    /// several independent chances to finish without broadcasting every final block to the swarm.
    /// </summary>
    [Fact]
    public void EndGameStrategy_CapsBroadDuplicationAtFourPeers()
    {
        var tracker = new BlockRequestTracker();
        var strategy = new EndGameBlockRequestStrategy(tracker, 16384);
        var pieceState = new PieceState(0, 1);

        for (int i = 0; i < 4; i++)
        {
            tracker.AddBlockRequest(0, 0, CreatePeer(), new BlockRequest());
        }

        Assert.False(strategy.IsBlockRequestable(pieceState, 0, 0, CreatePeer(), isPeerFast: false));
    }

    [Fact]
    public void EndGameStrategy_IsBlockRequestable_ReturnsTrue_EvenIfPending_UnlessFromSamePeer()
    {
        var tracker = new BlockRequestTracker();
        var strategy = new EndGameBlockRequestStrategy(tracker, 16384);

        var pieceState = new PieceState(0, 1);
        var peer1 = CreatePeer();
        var peer2 = CreatePeer();

        // Add pending request from peer1
        tracker.AddBlockRequest(0, 0, peer1, new BlockRequest());

        // EndGame: Should allow request from peer2 (duplicate request)
        bool resultFromPeer2 = strategy.IsBlockRequestable(pieceState, 0, 0, peer2, isPeerFast: false);
        Assert.True(resultFromPeer2);

        // EndGame: Should NOT allow request from peer1 (don't duplicate to same peer)
        bool resultFromPeer1 = strategy.IsBlockRequestable(pieceState, 0, 0, peer1, isPeerFast: false);
        Assert.False(resultFromPeer1);
    }
}
