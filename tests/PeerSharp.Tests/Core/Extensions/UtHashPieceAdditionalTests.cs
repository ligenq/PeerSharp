using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using PeerSharp.BEncoding;
using System.Net;
using PeerSharp.Messages;

namespace PeerSharp.Tests.Core.Extensions;

/// <summary>
/// Covers UtHashPiece paths not reached by UtHashPieceTests.cs:
/// CanVerifyPiece, VerifyPiece, HandleMessage on non-BEP30 torrent,
/// HandleMessage with unknown msg_type, HandleHashRequest when piece not available.
/// </summary>
public class UtHashPieceAdditionalTests
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

    private static TorrentFileMetadata MakeBep30Metadata(int pieceCount = 4)
    {
        var meta = new TorrentFileMetadata();
        meta.Info.MerkleRootHash = new byte[20];
        meta.Info.PieceSize = 16384;
        meta.Info.FullSize = 16384L * pieceCount;
        return meta;
    }

    // ── Non-BEP30 torrent ────────────────────────────────────────────────────

    [Fact]
    public void HandleMessage_NonBep30Torrent_IsNoOp()
    {
        var torrent = TorrentTestUtility.CreateMinimal(); // no MerkleRootHash
        var peer = new MockPeerCommunication();
        var utHashPiece = new UtHashPiece(peer, torrent);

        var dict = new BDict();
        dict.Dict["msg_type"] = new BNumber(0);
        dict.Dict["piece"] = new BNumber(0);

        utHashPiece.HandleMessage(BencodeWriter.Write(dict));

        Assert.Empty(peer.SentMessages);
    }

    [Fact]
    public void CanVerifyPiece_NonBep30Torrent_ReturnsFalse()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var utHashPiece = new UtHashPiece(new MockPeerCommunication(), torrent);

        Assert.False(utHashPiece.CanVerifyPiece(0));
    }

    [Fact]
    public void VerifyPiece_NonBep30Torrent_ReturnsFalse()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var utHashPiece = new UtHashPiece(new MockPeerCommunication(), torrent);

        Assert.False(utHashPiece.VerifyPiece(0, new byte[16384]));
    }

    // ── CanVerifyPiece / VerifyPiece on BEP30 torrent ────────────────────────

    [Fact]
    public void CanVerifyPiece_WithoutHash_ReturnsFalse()
    {
        var torrent = TorrentTestUtility.CreateMinimal(MakeBep30Metadata());
        var utHashPiece = new UtHashPiece(new MockPeerCommunication(), torrent);

        Assert.False(utHashPiece.CanVerifyPiece(0));
    }

    [Fact]
    public void CanVerifyPiece_AfterSettingHash_ReturnsTrue()
    {
        // 1-piece tree: leaf IS the root (_leafStart=0), so no uncle hashes needed
        var meta = MakeBep30Metadata(pieceCount: 1);
        var torrent = TorrentTestUtility.CreateMinimal(meta);
        var utHashPiece = new UtHashPiece(new MockPeerCommunication(), torrent);
        utHashPiece.SetPieceHash(0, new byte[20]);

        Assert.True(utHashPiece.CanVerifyPiece(0));
    }

    [Fact]
    public void VerifyPiece_CorrectData_ReturnsTrue()
    {
        byte[] pieceData = new byte[16384];
        pieceData[0] = 42;

        // For a 1-piece tree the root equals SHA1(pieceData) directly — no uncle hashes needed
        byte[] expectedRoot = System.Security.Cryptography.SHA1.HashData(pieceData);
        var meta = MakeBep30Metadata(pieceCount: 1);
        meta.Info.MerkleRootHash = expectedRoot;

        var torrent = TorrentTestUtility.CreateMinimal(meta);
        var utHashPiece = new UtHashPiece(new MockPeerCommunication(), torrent);

        Assert.True(utHashPiece.VerifyPiece(0, pieceData));
    }

    [Fact]
    public void VerifyPiece_CorruptData_ReturnsFalse()
    {
        var torrent = TorrentTestUtility.CreateMinimal(MakeBep30Metadata());
        var utHashPiece = new UtHashPiece(new MockPeerCommunication(), torrent);

        byte[] pieceData = new byte[16384];
        byte[] hash = System.Security.Cryptography.SHA1.HashData(pieceData);
        utHashPiece.SetPieceHash(0, hash);

        pieceData[0] = 99; // corrupt
        Assert.False(utHashPiece.VerifyPiece(0, pieceData));
    }

    // ── Unknown msg_type ─────────────────────────────────────────────────────

    [Fact]
    public void HandleMessage_UnknownMsgType_IsNoOp()
    {
        var torrent = TorrentTestUtility.CreateMinimal(MakeBep30Metadata());
        var peer = new MockPeerCommunication();
        var utHashPiece = new UtHashPiece(peer, torrent);
        utHashPiece.RemoteMessageId = 5;

        var dict = new BDict();
        dict.Dict["msg_type"] = new BNumber(99); // unknown
        dict.Dict["piece"] = new BNumber(0);

        utHashPiece.HandleMessage(BencodeWriter.Write(dict));

        Assert.Empty(peer.SentMessages);
    }

    // ── HandleHashRequest when piece not available locally ────────────────────

    [Fact]
    public async Task HandleMessage_HashRequest_PieceNotAvailable_SendsNothing()
    {
        var meta = MakeBep30Metadata(pieceCount: 2);
        var torrent = TorrentTestUtility.CreateMinimal(meta);
        await torrent.ReinitializeAfterMetadataAsync();
        // Piece 0 NOT added to torrent.Pieces

        var peer = new MockPeerCommunication();
        var utHashPiece = new UtHashPiece(peer, torrent);
        utHashPiece.RemoteMessageId = 5;

        var pieceHash = new byte[20]; pieceHash[0] = 1;
        utHashPiece.SetPieceHash(0, pieceHash);

        var dict = new BDict();
        dict.Dict["msg_type"] = new BNumber(0);
        dict.Dict["piece"] = new BNumber(0);
        utHashPiece.HandleMessage(BencodeWriter.Write(dict));

        Assert.Empty(peer.SentMessages);
    }

    // ── Out-of-range piece index ──────────────────────────────────────────────

    [Fact]
    public void HandleMessage_PieceIndexOutOfRange_IsNoOp()
    {
        var torrent = TorrentTestUtility.CreateMinimal(MakeBep30Metadata(pieceCount: 2));
        var peer = new MockPeerCommunication();
        var utHashPiece = new UtHashPiece(peer, torrent);

        var dict = new BDict();
        dict.Dict["msg_type"] = new BNumber(0);
        dict.Dict["piece"] = new BNumber(999); // out of range

        utHashPiece.HandleMessage(BencodeWriter.Write(dict));

        Assert.Empty(peer.SentMessages);
    }

    // ── SetLocalMessageId ────────────────────────────────────────────────────

    [Fact]
    public void SetLocalMessageId_StoresValue()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var utHashPiece = new UtHashPiece(new MockPeerCommunication(), torrent);

        utHashPiece.SetLocalMessageId(42);

        Assert.Equal(42, utHashPiece.LocalMessageId);
    }
}
