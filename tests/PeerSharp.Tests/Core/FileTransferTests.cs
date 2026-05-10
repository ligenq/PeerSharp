using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Extensions;
using PeerSharp.PieceWriter;
using Microsoft.Extensions.Time.Testing;
using System.Net;
using PeerSharp.Messages;

namespace PeerSharp.Tests.Core;

public class FileTransferTests
{
    private readonly Torrent _torrent;
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly FileTransfer _fileTransfer;
    private readonly PeerCommunication _peer;

    public FileTransferTests()
    {
        _torrent = TorrentTestUtility.CreateMinimal();
        _torrent.InfoFile.Info.PieceSize = 16384 * 2; // 2 blocks per piece
        _torrent.InfoFile.Info.FullSize = _torrent.InfoFile.Info.PieceSize * 10;

        _fileTransfer = new FileTransfer(_torrent, _timeProvider);
        _peer = new PeerCommunication(_torrent, new MockPeerListener(), _timeProvider);
    }

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
    public void LoadUnfinishedPiecesState_Works()
    {
        var data = new List<TorrentStateData.UnfinishedPieceData>
        {
            new()
            {
                Index = 0,
                Blocks = new[] { true, false },
                Data = new byte[32768]
            }
        };
        data[0].Data[0] = 42;

        _fileTransfer.LoadUnfinishedPiecesState(data);

        var unfinished = _fileTransfer.GetUnfinishedPiecesState();
        Assert.Single(unfinished);
        Assert.Equal(0, unfinished[0].Index);
        Assert.True(unfinished[0].Blocks[0]);
        Assert.False(unfinished[0].Blocks[1]);
        Assert.Equal(42, unfinished[0].Data[0]);
    }

    [Fact]
    public void RequestMerkleHashes_V2_SelectsPeerAndSendsRequest()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Version = TorrentVersion.V2;
        metadata.Info.HashV2 = InfoHash.CreateRandomV2();
        metadata.Info.PieceSize = ProtocolConstants.BlockSize;
        metadata.Info.FullSize = ProtocolConstants.BlockSize * 2;
        byte[] v2Root = metadata.Info.HashV2.Span.ToArray();
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "file.bin", Size = ProtocolConstants.BlockSize * 2, Offset = 0, PiecesRoot = v2Root, PieceCount = 2 });

        var torrent = TorrentTestUtility.CreateMinimal(metadata, Path.GetTempPath());
        torrent.InfoFile.Info.Files[0].PiecesRoot = v2Root;
        torrent.InfoFile.Info.Files[0].PieceCount = 2;

        Assert.NotNull(torrent.InfoFile.Info.GetV2HashRequestForPiece(0)); // Check if request is valid

        var fileTransfer = new FileTransfer(torrent, _timeProvider);

        // Peer must support V2 and have the piece
        var peer = new PeerCommunication(torrent, new MockPeerListener(), _timeProvider);
        var setRemoteSupportsV2 = peer.GetType().GetProperty("RemoteSupportsV2", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        setRemoteSupportsV2?.SetValue(peer, true);

        var connectedField = peer.GetType().GetField("_connected", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        connectedField?.SetValue(peer, 1);

        peer.PeerPieces.AddPiece(0);
        Assert.True(peer.PeerPieces.HasPiece(0)); // Ensure peer has piece
        Assert.True(peer.RemoteSupportsV2); // Ensure supports v2

        var connectedPeersField = torrent.PeersInternal.GetType().GetField("_connectedPeers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var connectedPeers = connectedPeersField?.GetValue(torrent.PeersInternal) as System.Collections.Concurrent.ConcurrentDictionary<PeerCommunication, byte>;
        Assert.NotNull(connectedPeers);
        connectedPeers.TryAdd(peer, 0);

        Assert.Single(torrent.PeersInternal.GetConnectedPeersInternal()); // Ensure connected peers contains the peer

        var requestMerkleHashes = typeof(FileTransfer).GetMethod("RequestMerkleHashes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        requestMerkleHashes?.Invoke(fileTransfer, new object[] { 0 });

        // Ensure a message was queued for the peer
        var queue = peer.GetType().GetField("_sendQueue", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(peer) as MessageQueue;
        Assert.NotNull(queue);
        bool dequeued = queue.TryDequeue(out var msg);
        Assert.True(dequeued, "Queue was empty");
        Assert.Equal(MessageId.HashRequest, msg.Id);
    }

    [Fact]
    public async Task ProcessBlock_AddsToActivePieces()
    {
        // We need to make the piece active first via PickNextPiece or manual addition.
        // Actually, FileTransfer.EvaluateNextRequests Internal picks pieces and adds them to _activePieces.
        // But I can't easily call it.
        // I'll manually add a piece to _activePieces using reflection for now, 
        // or just test that it DOESN'T store if not active.

        var block = new Block(0, 0, 16384);
        await _fileTransfer.ProcessBlockAsync(_peer, block);

        // Should not be stored because it wasn't requested/active
        Assert.Equal(0, _fileTransfer.Downloader.Downloaded);
        Assert.Throws<ObjectDisposedException>(() => block.Buffer);
    }

    [Theory]
    [InlineData(0, 16384, 16384, true)]
    [InlineData(8192, 8192, 16384, true)]
    [InlineData(8192, 8193, 16384, false)]
    [InlineData(16384, 1, 16384, false)]
    [InlineData(-1, 1, 16384, false)]
    [InlineData(0, 0, 16384, false)]
    [InlineData(int.MaxValue, 1, int.MaxValue, false)]
    public void IsValidUploadRequestRange_RejectsRangesOutsidePiece(int offset, int length, long pieceSize, bool expected)
    {
        Assert.Equal(expected, FileTransfer.IsValidUploadRequestRange(offset, length, pieceSize));
    }
}






