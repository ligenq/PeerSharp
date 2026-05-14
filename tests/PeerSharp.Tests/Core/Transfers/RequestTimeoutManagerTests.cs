using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using PeerSharp.Messages;
using PeerSharp.Internals.Extensions;
using System.Net;
using PeerSharp.Internals.Transfers;

namespace PeerSharp.Tests.Core.Transfers;

public class RequestTimeoutManagerTests
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
        return peer;
    }

    [Fact]
    public void ProcessTimeouts_RemovesExpiredRequests()
    {
        var tracker = new BlockRequestTracker();
        var timeProvider = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();

        int removedCount = 0;
        Action<int, int, PeerCommunication> removeCallback = (p, o, r) =>
        {
            removedCount++;
            tracker.RemoveBlockRequest(p, o, r);
        };

        var manager = new RequestTimeoutManager(
            tracker,
            removeCallback,
            _ => 1000, // Hard timeout 1s
            NullLogger<RequestTimeoutManager>.Instance,
            3);

        var peer = CreatePeer();
        var startTime = timeProvider.GetUtcNow();

        // Add request at t=0
        var request = new BlockRequest { Timestamp = startTime, PieceIndex = 0, Offset = 0, Length = 16384 };
        tracker.AddBlockRequest(0, 0, peer, request);

        // Advance time past timeout
        var now = startTime.AddSeconds(2);

        // Act
        manager.ProcessTimeouts(now, endGameMode: false);

        // Assert
        Assert.Equal(1, removedCount);
        Assert.False(tracker.HasPendingRequestFromPeer(0, 0, peer));
    }

    [Fact]
    public void ProcessTimeouts_KeepsValidRequests()
    {
        var tracker = new BlockRequestTracker();
        var timeProvider = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();

        int removedCount = 0;
        Action<int, int, PeerCommunication> removeCallback = (p, o, r) =>
        {
            removedCount++;
            tracker.RemoveBlockRequest(p, o, r);
        };

        var manager = new RequestTimeoutManager(
            tracker,
            removeCallback,
            _ => 5000, // Hard timeout 5s
            NullLogger<RequestTimeoutManager>.Instance,
            3);

        var peer = CreatePeer();
        var startTime = timeProvider.GetUtcNow();

        // Add request at t=0
        var request = new BlockRequest { Timestamp = startTime, PieceIndex = 0, Offset = 0, Length = 16384 };
        tracker.AddBlockRequest(0, 0, peer, request);

        // Advance time, but not past timeout
        var now = startTime.AddSeconds(2);

        // Act
        manager.ProcessTimeouts(now, endGameMode: false);

        // Assert
        Assert.Equal(0, removedCount);
        Assert.True(tracker.HasPendingRequestFromPeer(0, 0, peer));
    }
}
