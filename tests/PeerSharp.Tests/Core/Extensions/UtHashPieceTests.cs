using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using PeerSharp.BEncoding;
using System.Net;
using PeerSharp.Messages;

namespace PeerSharp.Tests.Core.Extensions;

public class UtHashPieceTests
{
    private class MockPeerCommunication : IPeerCommunication
    {
        public byte[] PeerId { get; set; } = new byte[20];
        public IPEndPoint? RemoteEndPoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 1234);
        public bool RemoteSupportsExtensions { get; set; }
        public ExtensionHandshake? RemoteExtensions { get; set; }
        public IUtMetadata UtMetadata => throw new NotImplementedException();
        public IUtPex UtPex => throw new NotImplementedException();
        public IPeerListener Listener { get; set; } = null!;
        public IUtHashPiece? UtHashPiece => throw new NotImplementedException();
        public IUtHolepunch UtHolepunch => throw new NotImplementedException();
        public List<PeerMessage> SentMessages { get; } = [];
        public Task SetInterestedAsync(bool interested) => Task.CompletedTask;
        public Task SendMessageAsync(PeerMessage msg)
        {
            SentMessages.Add(msg);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void RequestHashes_ValidPiece_SendsCorrectMessage()
    {
        // Arrange
        var metadata = new TorrentFileMetadata();
        metadata.Info.MerkleRootHash = new byte[20];
        metadata.Info.PieceSize = 16384;
        metadata.Info.FullSize = 16384 * 8; // 8 pieces

        var torrent = TorrentTestUtility.CreateMinimal(metadata);

        var mockPeer = new MockPeerCommunication();
        var utHashPiece = new UtHashPiece(mockPeer, torrent);
        utHashPiece.RemoteMessageId = 5;

        // Act
        utHashPiece.RequestHashes(2);

        // Assert
        Assert.Single(mockPeer.SentMessages);
        var msg = mockPeer.SentMessages[0];
        Assert.Equal(5, msg.Data[0]);

        var dict = Assert.IsType<BDict>(BencodeParser.Parse(msg.Data.AsSpan(1).ToArray()));
        Assert.Equal(0, (int?)dict.GetLong("msg_type"));
        Assert.Equal(2, (int?)dict.GetLong("piece"));
    }

    [Fact]
    public async Task HandleMessage_HashRequest_SendsHashPieceResponse()
    {
        // Arrange
        var metadata = new TorrentFileMetadata();
        metadata.Info.MerkleRootHash = new byte[20];
        metadata.Info.PieceSize = 16384;
        metadata.Info.FullSize = 16384 * 2; // 2 pieces

        var torrent = TorrentTestUtility.CreateMinimal(metadata);
        await torrent.ReinitializeAfterMetadataAsync(); // Setup PiecesProgress
        torrent.Pieces.AddPiece(0); // Mark piece 0 as available

        var mockPeer = new MockPeerCommunication();
        var utHashPiece = new UtHashPiece(mockPeer, torrent);
        utHashPiece.RemoteMessageId = 5;

        // Manually set a piece hash in the tree so it can respond
        var pieceHash = new byte[20];
        pieceHash[0] = 0xFF;
        utHashPiece.SetPieceHash(0, pieceHash);

        // BEP 30: hash_request: { "msg_type": 0, "piece": 0 }
        var dict = new BDict();
        dict.Dict["msg_type"] = new BNumber(0);
        dict.Dict["piece"] = new BNumber(0);
        var data = BencodeWriter.Write(dict);

        // Act
        utHashPiece.HandleMessage(data);

        // Assert
        Assert.Single(mockPeer.SentMessages);
        var response = mockPeer.SentMessages[0];
        Assert.Equal(5, response.Data[0]);

        var responseDict = Assert.IsType<BDict>(BencodeParser.Parse(response.Data.AsSpan(1).ToArray()));
        Assert.Equal(1, (int?)responseDict.GetLong("msg_type"));
        Assert.Equal(0, (int?)responseDict.GetLong("piece"));
        Assert.True(responseDict.Dict.ContainsKey("hashes"));
    }

    [Fact]
    public async Task HandleMessage_HashPiece_StoresPieceHashAndUncles()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.MerkleRootHash = new byte[20];
        metadata.Info.PieceSize = 16384;
        metadata.Info.FullSize = 16384 * 2;

        var torrent = TorrentTestUtility.CreateMinimal(metadata);
        await torrent.ReinitializeAfterMetadataAsync();

        var mockPeer = new MockPeerCommunication();
        var utHashPiece = new UtHashPiece(mockPeer, torrent);
        byte[] pieceHash = Enumerable.Range(0, 20).Select(i => (byte)(i + 1)).ToArray();
        byte[] uncleHash = Enumerable.Range(0, 20).Select(i => (byte)(0x80 + i)).ToArray();

        var dict = new BDict();
        dict.Dict["msg_type"] = new BNumber(1);
        dict.Dict["piece"] = new BNumber(1);
        dict.Dict["hashes"] = new BString(pieceHash.Concat(uncleHash).ToArray());

        utHashPiece.HandleMessage(BencodeWriter.Write(dict));

        Assert.Equal(pieceHash, utHashPiece.GetPieceHash(1));
        Assert.Equal(pieceHash, torrent.MerkleTree!.GetPieceHash(1));
        Assert.Equal(uncleHash, torrent.MerkleTree.GetUncleHashes(1).Single());
    }

    [Fact]
    public async Task HandleMessage_HashPiece_MissingHashes_DoesNothing()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.MerkleRootHash = new byte[20];
        metadata.Info.PieceSize = 16384;
        metadata.Info.FullSize = 16384 * 2;

        var torrent = TorrentTestUtility.CreateMinimal(metadata);
        await torrent.ReinitializeAfterMetadataAsync();

        var mockPeer = new MockPeerCommunication();
        var utHashPiece = new UtHashPiece(mockPeer, torrent);
        var dict = new BDict();
        dict.Dict["msg_type"] = new BNumber(1);
        dict.Dict["piece"] = new BNumber(1);

        utHashPiece.HandleMessage(BencodeWriter.Write(dict));

        Assert.Null(utHashPiece.GetPieceHash(1));
        Assert.Null(torrent.MerkleTree!.GetPieceHash(1));
    }

    [Fact]
    public async Task HandleMessage_HashPiece_TruncatedHashPayload_DoesNothing()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.MerkleRootHash = new byte[20];
        metadata.Info.PieceSize = 16384;
        metadata.Info.FullSize = 16384 * 2;

        var torrent = TorrentTestUtility.CreateMinimal(metadata);
        await torrent.ReinitializeAfterMetadataAsync();

        var mockPeer = new MockPeerCommunication();
        var utHashPiece = new UtHashPiece(mockPeer, torrent);
        var dict = new BDict();
        dict.Dict["msg_type"] = new BNumber(1);
        dict.Dict["piece"] = new BNumber(1);
        dict.Dict["hashes"] = new BString(new byte[19]);

        utHashPiece.HandleMessage(BencodeWriter.Write(dict));

        Assert.Null(utHashPiece.GetPieceHash(1));
        Assert.Null(torrent.MerkleTree!.GetPieceHash(1));
    }
}






