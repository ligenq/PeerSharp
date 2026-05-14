using System.Security.Cryptography;
using PeerSharp.Internals;
using PeerSharp.Internals.Utilities;
using PeerSharp.PiecePicking;
using InternalEntry = PeerSharp.Internals.TorrentFileEntry;

namespace PeerSharp.Tests.Core.Pieces;

public class TorrentPieceCheckerContextTests
{
    private static TorrentFileMetadata BuildV2Metadata(
        InternalEntry entry,
        uint pieceSize = 16384)
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Version = TorrentVersion.V2;
        metadata.Info.Name = "v2_test";
        metadata.Info.PieceSize = pieceSize;
        metadata.Info.FullSize = entry.Size;
        metadata.Info.Files.Add(entry);
        return metadata;
    }

    [Fact]
    public void IsV2_V2Torrent_ReturnsTrue()
    {
        var entry = new InternalEntry
        {
            Path = "test.bin",
            Size = 16384,
            Offset = 0,
            FirstPieceIndex = 0,
            PieceCount = 1,
            PiecesRoot = new byte[32]
        };

        var torrent = TorrentTestUtility.CreateMinimal(BuildV2Metadata(entry));
        var ctx = new TorrentPieceCheckerContext(torrent);

        Assert.True(ctx.IsV2);
        Assert.False(ctx.IsMerkle);
    }

    [Fact]
    public void VerifyPiece_V2SinglePiece_CorrectData_ReturnsTrue()
    {
        var data = new byte[16384];
        Random.Shared.NextBytes(data);

        // Single 16KB piece: piecesRoot = SHA256(data)
        // GetPieceLayerDepth(16384) == 0 => leaf layer = piece layer
        // GetLayer([SHA256(data)], 0)[0] = SHA256(data)
        var piecesRoot = SHA256.HashData(data);

        var entry = new InternalEntry
        {
            Path = "test.bin",
            Size = 16384,
            Offset = 0,
            FirstPieceIndex = 0,
            PieceCount = 1,
            PiecesRoot = piecesRoot
        };

        var torrent = TorrentTestUtility.CreateMinimal(BuildV2Metadata(entry));
        var ctx = new TorrentPieceCheckerContext(torrent);

        Assert.True(ctx.VerifyPiece(0, data));
    }

    [Fact]
    public void VerifyPiece_V2SinglePiece_WrongData_ReturnsFalse()
    {
        var data = new byte[16384];
        Random.Shared.NextBytes(data);
        var piecesRoot = SHA256.HashData(data);

        var entry = new InternalEntry
        {
            Path = "test.bin",
            Size = 16384,
            Offset = 0,
            FirstPieceIndex = 0,
            PieceCount = 1,
            PiecesRoot = piecesRoot
        };

        var torrent = TorrentTestUtility.CreateMinimal(BuildV2Metadata(entry));
        var ctx = new TorrentPieceCheckerContext(torrent);

        var wrongData = new byte[16384]; // all zeros — different hash
        Assert.False(ctx.VerifyPiece(0, wrongData));
    }

    [Fact]
    public void VerifyPiece_V2MultiPiece_ValidPieceLayers_ReturnsTrue()
    {
        var data = new byte[32768]; // 2 × 16KB pieces
        Random.Shared.NextBytes(data);

        var leaf0 = SHA256.HashData(data.AsSpan(0, 16384));
        var leaf1 = SHA256.HashData(data.AsSpan(16384, 16384));
        var piecesRoot = MerkleTree.HashPair(leaf0, leaf1);

        var entry = new InternalEntry
        {
            Path = "test.bin",
            Size = 32768,
            Offset = 0,
            FirstPieceIndex = 0,
            PieceCount = 2,
            PiecesRoot = piecesRoot
        };
        entry.PieceLayers = [leaf0, leaf1];

        var metadata = BuildV2Metadata(entry);
        metadata.Info.FullSize = 32768;

        var torrent = TorrentTestUtility.CreateMinimal(metadata);
        var ctx = new TorrentPieceCheckerContext(torrent);

        Assert.True(ctx.VerifyPiece(0, data[..16384]));
        Assert.True(ctx.VerifyPiece(1, data[16384..]));
    }

    [Fact]
    public void VerifyPiece_V2MultiPiece_WrongData_ReturnsFalse()
    {
        var data = new byte[32768];
        Random.Shared.NextBytes(data);

        var leaf0 = SHA256.HashData(data.AsSpan(0, 16384));
        var leaf1 = SHA256.HashData(data.AsSpan(16384, 16384));

        var entry = new InternalEntry
        {
            Path = "test.bin",
            Size = 32768,
            Offset = 0,
            FirstPieceIndex = 0,
            PieceCount = 2,
            PiecesRoot = MerkleTree.HashPair(leaf0, leaf1)
        };
        entry.PieceLayers = [leaf0, leaf1];

        var metadata = BuildV2Metadata(entry);
        metadata.Info.FullSize = 32768;

        var torrent = TorrentTestUtility.CreateMinimal(metadata);
        var ctx = new TorrentPieceCheckerContext(torrent);

        var wrongPiece = new byte[16384]; // all zeros
        Assert.False(ctx.VerifyPiece(0, wrongPiece));
    }

    [Fact]
    public void VerifyPiece_V2NoPiecesRoot_ReturnsFalse()
    {
        var entry = new InternalEntry
        {
            Path = "test.bin",
            Size = 16384,
            Offset = 0,
            FirstPieceIndex = 0,
            PieceCount = 1,
            PiecesRoot = null // no hash available
        };

        var torrent = TorrentTestUtility.CreateMinimal(BuildV2Metadata(entry));
        var ctx = new TorrentPieceCheckerContext(torrent);

        Assert.False(ctx.VerifyPiece(0, new byte[16384]));
    }
}
