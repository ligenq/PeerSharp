using PeerSharp.Internals.Utilities;
using System.Security.Cryptography;

namespace PeerSharp.Tests.Core.Utilities;

public class MerkleTreeTests
{
    [Fact]
    public void ComputeRoot_SingleLeaf_ReturnsLeaf()
    {
        byte[] leaf = new byte[32];
        leaf[0] = 1;
        var leaves = new List<byte[]> { leaf };
        var root = MerkleTree.ComputeRoot(leaves);
        Assert.Equal(leaf, root);
    }

    [Fact]
    public void ComputeLeaves_PartialBlock_HashesPaddedData()
    {
        byte[] data = new byte[1234];
        Random.Shared.NextBytes(data);

        var leaves = MerkleTree.ComputeLeaves(data);

        Assert.Single(leaves);

        byte[] padded = new byte[MerkleTree.BlockSize];
        data.CopyTo(padded);
        Assert.Equal(SHA256.HashData(padded), leaves[0]);
    }

    [Fact]
    public void ComputeRoot_TwoLeaves_ReturnsCombinedHash()
    {
        byte[] l1 = new byte[32]; l1[0] = 1;
        byte[] l2 = new byte[32]; l2[0] = 2;
        var leaves = new List<byte[]> { l1, l2 };

        var expected = MerkleTree.HashPair(l1, l2);
        var actual = MerkleTree.ComputeRoot(leaves);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeRoot_ThreeLeaves_PadsToFour()
    {
        byte[] l1 = new byte[32]; l1[0] = 1;
        byte[] l2 = new byte[32]; l2[0] = 2;
        byte[] l3 = new byte[32]; l3[0] = 3;
        var leaves = new List<byte[]> { l1, l2, l3 };

        // Padded: [l1, l2, l3, 0]
        // Level 1: [H(l1,l2), H(l3, 0)]
        // Level 2: [H(H(l1,l2), H(l3, 0))]

        byte[] h12 = MerkleTree.HashPair(l1, l2);
        byte[] h30 = MerkleTree.HashPair(l3, new byte[32]);
        byte[] expected = MerkleTree.HashPair(h12, h30);

        var actual = MerkleTree.ComputeRoot(leaves);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void VerifyPiece_CorrectData_ReturnsTrue()
    {
        byte[] data = new byte[32768]; // 2 blocks (16KB each)
        Random.Shared.NextBytes(data);

        var leaves = MerkleTree.ComputeLeaves(data);
        var pieceRoot = MerkleTree.ComputeRoot(leaves);

        bool valid = MerkleTree.VerifyPiece(data, 0, pieceRoot, 32768);
        Assert.True(valid);
    }

    [Fact]
    public void VerifyPiece_CorruptData_ReturnsFalse()
    {
        byte[] data = new byte[32768];
        Random.Shared.NextBytes(data);

        var leaves = MerkleTree.ComputeLeaves(data);
        var pieceRoot = MerkleTree.ComputeRoot(leaves);

        data[0]++; // Corrupt
        bool valid = MerkleTree.VerifyPiece(data, 0, pieceRoot, 32768);
        Assert.False(valid);
    }

    [Fact]
    public void VerifyPiece_FinalPartialPiece_PadsToPieceBoundary()
    {
        const uint pieceSize = 64 * 1024;
        byte[] fileData = new byte[80 * 1024]; // 64KB piece + 16KB final piece
        Random.Shared.NextBytes(fileData);

        var leaves = MerkleTree.ComputeLeaves(fileData);
        var pieceLayer = MerkleTree.GetPieceLayer(leaves, pieceSize);
        byte[] finalPiece = fileData.AsSpan(64 * 1024).ToArray();

        Assert.Equal(2, pieceLayer.Count);
        Assert.False(MerkleTree.VerifyPiece(finalPiece, 1, pieceLayer[1], pieceSize));
        Assert.True(MerkleTree.VerifyPiece(finalPiece, 1, pieceLayer[1], pieceSize, padToPieceSize: true));
    }

    [Fact]
    public void VerifyPiece_SinglePieceSmallFile_DoesNotPadToPieceBoundary()
    {
        const uint pieceSize = 64 * 1024;
        byte[] fileData = new byte[24 * 1024];
        Random.Shared.NextBytes(fileData);

        var leaves = MerkleTree.ComputeLeaves(fileData);
        var root = MerkleTree.ComputeRoot(leaves);

        Assert.True(MerkleTree.VerifyPiece(fileData, 0, root, pieceSize));
        Assert.False(MerkleTree.VerifyPiece(fileData, 0, root, pieceSize, padToPieceSize: true));
    }

    [Fact]
    public void VerifyPieceLayerAgainstRoot_Valid_ReturnsTrue()
    {
        // 1MB file, 32KB pieces
        const long fileSize = 1024 * 1024;
        const uint pieceSize = 32768;
        byte[] fileData = new byte[fileSize];
        Random.Shared.NextBytes(fileData);

        var leaves = MerkleTree.ComputeLeaves(fileData);
        var root = MerkleTree.ComputeRoot(leaves);
        var pieceLayer = MerkleTree.GetPieceLayer(leaves, pieceSize);

        bool valid = MerkleTree.VerifyPieceLayerAgainstRoot(pieceLayer, root, pieceSize, fileSize);
        Assert.True(valid);
    }

    [Fact]
    public void PadHashAtLayer_Layer0_IsZeroHash()
    {
        Assert.Equal(new byte[32], MerkleTree.PadHashAtLayer(0));
    }

    [Fact]
    public void PadHashAtLayer_Layer1_IsZeroPairHash()
    {
        byte[] expected = MerkleTree.HashPair(new byte[32], new byte[32]);
        Assert.Equal(expected, MerkleTree.PadHashAtLayer(1));
    }

    [Fact]
    public void PadHashAtLayer_LayerN_FoldsZeroN_Times()
    {
        byte[] expected = new byte[32];
        for (int i = 0; i < 4; i++)
        {
            expected = MerkleTree.HashPair(expected, expected);
        }
        Assert.Equal(expected, MerkleTree.PadHashAtLayer(4));
    }

    // Bug fix coverage: BEP 52 padding was previously zero-hash at the piece layer, causing
    // every v2 file with non-power-of-2 piece count AND pieceSize > blockSize to fail validation.
    [Fact]
    public void GetPieceLayer_NonPowerOfTwoPieceCount_ReturnsExactlyPieceCount()
    {
        // 80KB file, 32KB pieces => 5 blocks, 3 real pieces. Without the trim, GetPieceLayer
        // historically returned 4 entries (last one was hash(zero, zero) padding).
        const long fileSize = 80 * 1024;
        const uint pieceSize = 32 * 1024;
        byte[] fileData = new byte[fileSize];
        Random.Shared.NextBytes(fileData);

        var leaves = MerkleTree.ComputeLeaves(fileData);
        var pieceLayer = MerkleTree.GetPieceLayer(leaves, pieceSize);

        Assert.Equal(3, pieceLayer.Count);
    }

    [Fact]
    public void VerifyPieceLayerAgainstRoot_NonPowerOfTwoPieceCount_PieceLargerThanBlock_Verifies()
    {
        // The original bug: VerifyPieceLayerAgainstRoot padded with zero hash at the piece
        // layer instead of merkle_pad(blocks_per_piece, 1). Pre-fix this returned false.
        const long fileSize = 80 * 1024;       // 5 blocks → 3 pieces (last partial)
        const uint pieceSize = 32 * 1024;      // 2 blocks per piece
        byte[] fileData = new byte[fileSize];
        Random.Shared.NextBytes(fileData);

        var leaves = MerkleTree.ComputeLeaves(fileData);
        var root = MerkleTree.ComputeRoot(leaves);
        var pieceLayer = MerkleTree.GetPieceLayer(leaves, pieceSize);

        Assert.True(MerkleTree.VerifyPieceLayerAgainstRoot(pieceLayer, root, pieceSize, fileSize));
    }

    [Fact]
    public void VerifyPieceLayerAgainstRoot_LibtorrentSpec_PadFoldsThroughLayers()
    {
        // Reproduce the exact tree libtorrent computes (`merkle_pad(blocks_per_piece, 1)`)
        // and check our code agrees layer for layer.
        // 96KB file with 32KB pieces => 6 blocks → 3 pieces.
        const long fileSize = 96 * 1024;
        const uint pieceSize = 32 * 1024;
        byte[] fileData = new byte[fileSize];
        Random.Shared.NextBytes(fileData);

        var leaves = MerkleTree.ComputeLeaves(fileData);
        Assert.Equal(6, leaves.Count);

        // Manual computation per libtorrent: pad blocks to 8 with zero, hash up.
        byte[] zero = new byte[32];
        byte[] p0 = MerkleTree.HashPair(leaves[0], leaves[1]);
        byte[] p1 = MerkleTree.HashPair(leaves[2], leaves[3]);
        byte[] p2 = MerkleTree.HashPair(leaves[4], leaves[5]);
        byte[] pieceLayerPad = MerkleTree.HashPair(zero, zero);
        byte[] expectedRoot = MerkleTree.HashPair(
            MerkleTree.HashPair(p0, p1),
            MerkleTree.HashPair(p2, pieceLayerPad));

        var pieceLayer = MerkleTree.GetPieceLayer(leaves, pieceSize);
        Assert.Equal(3, pieceLayer.Count);
        Assert.Equal(p0, pieceLayer[0]);
        Assert.Equal(p1, pieceLayer[1]);
        Assert.Equal(p2, pieceLayer[2]);

        var root = MerkleTree.ComputeRoot(leaves);
        Assert.Equal(expectedRoot, root);
        Assert.True(MerkleTree.VerifyPieceLayerAgainstRoot(pieceLayer, root, pieceSize, fileSize));
    }

    [Fact]
    public void GetPieceLayerHashesWithProof_FinalChunk_VirtuallyPadsBeyondPieceCount()
    {
        // libtorrent requests the final chunk with count = NextPow2(remaining); the server
        // must virtually fill entries past file_num_pieces with the layer pad. Pre-fix,
        // GetV2Hashes returned null when index+length exceeded layer.Count.
        const long fileSize = 4 * 16_384L;     // 4 blocks → 4 pieces with pieceSize=16KB
        const uint pieceSize = 16_384;
        byte[] fileData = new byte[fileSize];
        Random.Shared.NextBytes(fileData);
        var leaves = MerkleTree.ComputeLeaves(fileData);
        var pieceLayer = MerkleTree.GetPieceLayer(leaves, pieceSize);

        // Pretend pieceCount is 3 and ask for index=2, count=2 (1 real + 1 virtually-padded).
        var realLayer = pieceLayer.GetRange(0, 3);
        var result = MerkleTree.GetPieceLayerHashesWithProof(realLayer, pieceSize, 3 * 16_384L, 2, 2, 0);

        Assert.NotNull(result);
        Assert.Equal(2 * 32, result!.Length);
        // First hash is the real piece 2; second is the layer pad (zero hash at depth 0).
        Assert.Equal(realLayer[2], result.AsSpan(0, 32).ToArray());
        Assert.Equal(MerkleTree.PadHashAtLayer(0), result.AsSpan(32, 32).ToArray());
    }
}





