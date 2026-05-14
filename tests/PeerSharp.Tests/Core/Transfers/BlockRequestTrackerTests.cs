using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Extensions;
using Microsoft.Extensions.Time.Testing;
using System.Net;
using PeerSharp.Internals.Transfers;

namespace PeerSharp.Tests.Core.Transfers;

public class BlockRequestTrackerTests
{
    private readonly Torrent _torrent;
    private readonly FakeTimeProvider _timeProvider;
    private readonly BlockRequestTracker _tracker;

    public BlockRequestTrackerTests()
    {
        _torrent = TorrentTestUtility.CreateMinimal();
        _timeProvider = new FakeTimeProvider();
        _tracker = new BlockRequestTracker();
    }

    private PeerCommunication CreatePeer(int port)
    {
        var peer = new PeerCommunication(_torrent, new MockPeerListener(), _timeProvider);
        // We need to set the remote endpoint for equality checks in some scenarios, 
        // though PeerCommunication uses reference equality by default.
        return peer;
    }

    [Fact]
    public void AddAndRemoveRequest_UpdatesIndexCount()
    {
        var peer = CreatePeer(1000);
        var request = new BlockRequest { PieceIndex = 0, Offset = 0, Length = 16384, Timestamp = _timeProvider.GetUtcNow() };

        Assert.Equal(0, _tracker.BlockRequestIndexCount);

        _tracker.AddBlockRequest(0, 0, peer, request);
        Assert.Equal(1, _tracker.BlockRequestIndexCount);

        _tracker.RemoveBlockRequest(0, 0, peer);
        Assert.Equal(0, _tracker.BlockRequestIndexCount);
    }

    [Fact]
    public void MultiplePeersForSameBlock_MaintainsIndexCount()
    {
        var peer1 = CreatePeer(1001);
        var peer2 = CreatePeer(1002);
        var req1 = new BlockRequest { PieceIndex = 0, Offset = 0, Length = 16384, Timestamp = _timeProvider.GetUtcNow() };
        var req2 = new BlockRequest { PieceIndex = 0, Offset = 0, Length = 16384, Timestamp = _timeProvider.GetUtcNow() };

        _tracker.AddBlockRequest(0, 0, peer1, req1);
        _tracker.AddBlockRequest(0, 0, peer2, req2);

        Assert.Equal(1, _tracker.BlockRequestIndexCount);

        _tracker.RemoveBlockRequest(0, 0, peer1);
        Assert.Equal(1, _tracker.BlockRequestIndexCount);

        _tracker.RemoveBlockRequest(0, 0, peer2);
        Assert.Equal(0, _tracker.BlockRequestIndexCount);
    }

    [Fact]
    public void GetOldestPendingRequest_ReturnsCorrectPeer()
    {
        var peer1 = CreatePeer(1001);
        var peer2 = CreatePeer(1002);

        var now = _timeProvider.GetUtcNow();
        var oldReq = new BlockRequest { PieceIndex = 0, Offset = 0, Length = 16384, Timestamp = now.AddSeconds(-10) };
        var newReq = new BlockRequest { PieceIndex = 0, Offset = 0, Length = 16384, Timestamp = now.AddSeconds(-5) };

        _tracker.AddBlockRequest(0, 0, peer1, newReq);
        _tracker.AddBlockRequest(0, 0, peer2, oldReq);

        var oldest = _tracker.GetOldestPendingRequest(0, 0, now);

        Assert.NotNull(oldest);
        Assert.Equal(peer2, oldest.Value.Peer);
        Assert.Equal(10000, oldest.Value.AgeMs);
    }

    [Fact]
    public void PeerRequests_TrackedSeparately()
    {
        var peer = CreatePeer(1001);
        var collection = _tracker.GetOrAddPeerRequests(peer);

        var req = new BlockRequest { PieceIndex = 1, Offset = 0, Length = 16384, Timestamp = _timeProvider.GetUtcNow() };
        collection[(1, 0)] = req;

        Assert.True(_tracker.TryGetPeerRequests(peer, out var retrieved));
        Assert.Equal(collection, retrieved);
        Assert.Single(retrieved.Values);

        _tracker.TryRemovePeerRequest(peer, (1, 0), out var removed);
        Assert.Equal(req, removed);
        Assert.True(retrieved.IsEmpty);
    }

    [Fact]
    public void HasPendingRequestFromPeer_ReturnsCorrectValue()
    {
        var peer1 = CreatePeer(1001);
        var peer2 = CreatePeer(1002);
        var req = new BlockRequest { PieceIndex = 0, Offset = 0, Length = 16384, Timestamp = _timeProvider.GetUtcNow() };

        _tracker.AddBlockRequest(0, 0, peer1, req);

        Assert.True(_tracker.HasPendingRequestFromPeer(0, 0, peer1));
        Assert.False(_tracker.HasPendingRequestFromPeer(0, 0, peer2));
        Assert.False(_tracker.HasPendingRequestFromPeer(0, 16384, peer1));
    }

    [Fact]
    public void RemovePeer_CleansUpAllPeerRequests()
    {
        var peer = CreatePeer(1001);
        var req1 = new BlockRequest { PieceIndex = 0, Offset = 0, Length = 16384, Timestamp = _timeProvider.GetUtcNow() };
        var req2 = new BlockRequest { PieceIndex = 1, Offset = 0, Length = 16384, Timestamp = _timeProvider.GetUtcNow() };

        _tracker.AddBlockRequest(0, 0, peer, req1);
        _tracker.AddBlockRequest(1, 0, peer, req2);

        Assert.True(_tracker.HasPendingRequestFromPeer(0, 0, peer));
        Assert.True(_tracker.HasPendingRequestFromPeer(1, 0, peer));
        Assert.True(_tracker.TryGetPeerRequests(peer, out var requests));

        _tracker.RemovePeer(peer);

        Assert.False(_tracker.HasPendingRequestFromPeer(0, 0, peer));
        Assert.False(_tracker.HasPendingRequestFromPeer(1, 0, peer));
        Assert.False(_tracker.TryGetPeerRequests(peer, out _));
        Assert.Equal(0, _tracker.BlockRequestIndexCount);
    }

    private class MockPeerListener : IPeerListener
    {
        public Task HandshakeFinishedAsync(IPeerCommunication peer) => Task.CompletedTask;
        public Task ConnectionClosedAsync(IPeerCommunication peer, int code) => Task.CompletedTask;
        public Task MessageReceivedAsync(IPeerCommunication peer, Messages.PeerMessage msg) => Task.CompletedTask;
        public Task ExtendedHandshakeFinishedAsync(IPeerCommunication peer, ExtensionHandshake handshake) => Task.CompletedTask;
        public Task ExtendedMessageReceivedAsync(IPeerCommunication peer, int type, byte[] data) => Task.CompletedTask;
        public Task PexReceivedAsync(IPeerCommunication peer, List<IPEndPoint> added, List<byte> addedFlags, List<IPEndPoint> dropped) => Task.CompletedTask;
        public Task HolepunchMessageReceivedAsync(IPeerCommunication peer, UtHolepunch.MsgId id, IPEndPoint endpoint, UtHolepunch.ErrorCode error) => Task.CompletedTask;
        public Task PortReceivedAsync(IPeerCommunication peer, ushort dhtPort) => Task.CompletedTask;
    }
}
