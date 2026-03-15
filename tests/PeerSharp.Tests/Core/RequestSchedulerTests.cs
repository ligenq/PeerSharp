using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using PeerSharp.Messages;
using PeerSharp.PiecePicking;
using System.Net;
using System.Reflection;

namespace PeerSharp.Tests.Core;

public class RequestSchedulerTests
{
    [Fact]
    public async Task EvaluateNextRequestsAsync_SendsRequests_ForActivePiece()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = 16384 * 2;
        metadata.Info.FullSize = 16384 * 2;
        var torrent = TorrentTestUtility.CreateMinimal(metadata);

        var piecePicker = new PiecePicker(new TorrentPiecePickerContext(torrent), TimeProvider.System, Random.Shared);
        var pieceStateManager = new PieceStateManager(piecePicker, NullLogger<PieceStateManager>.Instance, maxActivePieces: 1);
        var requestTracker = new BlockRequestTracker();

        var scheduler = new RequestScheduler(new RequestSchedulerOptions
        {
            Torrent = torrent,
            RequestTracker = requestTracker,
            PieceStateManager = pieceStateManager,
            TimeProvider = TimeProvider.System,
            Logger = NullLogger<RequestScheduler>.Instance,
            BlockSize = 16384,
            MaxRequestsPerPeer = 8,
            GetSoftTimeoutMs = _ => 3000
        }, piecePicker);

        var peer = new PeerCommunication(torrent, new MockPeerListener(), TimeProvider.System);
        SetPrivateField(peer, "_peerChoking", 0);
        SetPrivateField(peer, "_connected", 1);
        peer.PeerPieces.AddPiece(0);

        var state = new PieceState(0, 2);
        pieceStateManager.TryAddPiece(state);

        await scheduler.EvaluateNextRequestsAsync(peer, endGameMode: false, isQueueFull: () => false);

        Assert.True(requestTracker.TryGetPeerRequests(peer, out var requests));
        Assert.Equal(2, requests.Count);
    }

    private static void SetPrivateField(object target, string fieldName, int value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private sealed class MockPeerListener : IPeerListener
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
}
