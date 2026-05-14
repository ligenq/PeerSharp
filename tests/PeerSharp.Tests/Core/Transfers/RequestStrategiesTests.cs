using Microsoft.Extensions.Logging.Abstractions;
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
            NullLogger.Instance,
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
            NullLogger.Instance,
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
            NullLogger.Instance,
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
            NullLogger.Instance,
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
