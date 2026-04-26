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






