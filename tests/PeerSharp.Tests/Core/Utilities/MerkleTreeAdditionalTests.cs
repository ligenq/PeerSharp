using PeerSharp.Internals.Utilities;
using System.Security.Cryptography;

namespace PeerSharp.Tests.Core.Utilities;

/// <summary>
/// Covers MerkleTree methods not reached by MerkleTreeTests.cs:
/// GetLayer, ParsePieceLayer, ValidateLayerRequest, GetTotalLevels,
/// VerifyPieceLayerSubsetAgainstRoot, CeilingPowerOf2, FloorLog2, HashBlock.
/// </summary>
public class MerkleTreeAdditionalTests
{
    // ── GetLayer ──────────────────────────────────────────────────────────────

    [Fact]
    public void GetLayer_Depth0_ReturnsLeaves()
    {
        var leaves = new List<byte[]> { new byte[32] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 } };
        var layer = MerkleTree.GetLayer(leaves, 0);
        Assert.Same(leaves, layer);
    }

    [Fact]
    public void GetLayer_Depth1_ReturnsPairHash()
    {
        byte[] l1 = new byte[32]; l1[0] = 1;
        byte[] l2 = new byte[32]; l2[0] = 2;
        var leaves = new List<byte[]> { l1, l2 };

        var layer = MerkleTree.GetLayer(leaves, 1);

        Assert.Single(layer);
        Assert.Equal(MerkleTree.HashPair(l1, l2), layer[0]);
    }

    [Fact]
    public void GetLayer_Depth1_OddLeaves_PadsToEven()
    {
        byte[] l1 = new byte[32]; l1[0] = 1;
        byte[] l2 = new byte[32]; l2[0] = 2;
        byte[] l3 = new byte[32]; l3[0] = 3;
        var leaves = new List<byte[]> { l1, l2, l3 };

        var layer = MerkleTree.GetLayer(leaves, 1);

        Assert.Equal(2, layer.Count); // 4 padded → 2 parents
        Assert.Equal(MerkleTree.HashPair(l1, l2), layer[0]);
        Assert.Equal(MerkleTree.HashPair(l3, new byte[32]), layer[1]);
    }

    [Fact]
    public void GetLayer_DepthExceedsTree_ReturnsRootSingleElement()
    {
        byte[] l1 = new byte[32]; l1[0] = 1;
        byte[] l2 = new byte[32]; l2[0] = 2;
        var leaves = new List<byte[]> { l1, l2 };

        var layer = MerkleTree.GetLayer(leaves, 5); // deeper than tree → collapses to root

        Assert.Single(layer);
        Assert.Equal(MerkleTree.HashPair(l1, l2), layer[0]);
    }

    // ── ParsePieceLayer ───────────────────────────────────────────────────────

    [Fact]
    public void ParsePieceLayer_TwoHashes_ReturnsTwoEntries()
    {
        byte[] h1 = Enumerable.Repeat((byte)0xAA, 32).ToArray();
        byte[] h2 = Enumerable.Repeat((byte)0xBB, 32).ToArray();
        byte[] layerData = h1.Concat(h2).ToArray();

        var result = MerkleTree.ParsePieceLayer(layerData);

        Assert.Equal(2, result.Count);
        Assert.Equal(h1, result[0]);
        Assert.Equal(h2, result[1]);
    }

    [Fact]
    public void ParsePieceLayer_Empty_ReturnsEmptyList()
    {
        var result = MerkleTree.ParsePieceLayer([]);
        Assert.Empty(result);
    }

    [Fact]
    public void ParsePieceLayer_TruncatedTrailing_IgnoresIncomplete()
    {
        // 32 + 15 bytes → only one complete hash
        byte[] data = new byte[47];
        data[0] = 0xFF;

        var result = MerkleTree.ParsePieceLayer(data);

        Assert.Single(result);
        Assert.Equal(0xFF, result[0][0]);
    }

    // ── ValidateLayerRequest ──────────────────────────────────────────────────

    [Fact]
    public void ValidateLayerRequest_Valid_ReturnsTrue()
    {
        // 1MB file, 16KB pieces → 64 blocks, 64 pieces, piece depth = 0
        long fileSize = 1024 * 1024;
        uint pieceSize = 16384;
        bool valid = MerkleTree.ValidateLayerRequest(pieceSize, fileSize, 0, 0, 1, 5);
        Assert.True(valid);
    }

    [Fact]
    public void ValidateLayerRequest_PieceSizeLessThanBlockSize_ReturnsFalse()
    {
        bool valid = MerkleTree.ValidateLayerRequest(pieceSize: 8192, fileSize: 16384, baseLayer: 0, index: 0, count: 1, proofLayers: 0);
        Assert.False(valid);
    }

    [Fact]
    public void ValidateLayerRequest_PieceSizeNotMultipleOfBlock_ReturnsFalse()
    {
        bool valid = MerkleTree.ValidateLayerRequest(pieceSize: 20000, fileSize: 16384, baseLayer: 0, index: 0, count: 1, proofLayers: 0);
        Assert.False(valid);
    }

    [Fact]
    public void ValidateLayerRequest_NegativeIndex_ReturnsFalse()
    {
        bool valid = MerkleTree.ValidateLayerRequest(pieceSize: 16384, fileSize: 16384, baseLayer: 0, index: -1, count: 1, proofLayers: 0);
        Assert.False(valid);
    }

    [Fact]
    public void ValidateLayerRequest_ZeroCount_ReturnsFalse()
    {
        bool valid = MerkleTree.ValidateLayerRequest(pieceSize: 16384, fileSize: 16384, baseLayer: 0, index: 0, count: 0, proofLayers: 0);
        Assert.False(valid);
    }

    [Fact]
    public void ValidateLayerRequest_CountExceedsMax_ReturnsFalse()
    {
        bool valid = MerkleTree.ValidateLayerRequest(pieceSize: 16384, fileSize: 16384 * 10000L, baseLayer: 0, index: 0, count: 8193, proofLayers: 0);
        Assert.False(valid);
    }

    [Fact]
    public void ValidateLayerRequest_NegativeProofLayers_ReturnsFalse()
    {
        bool valid = MerkleTree.ValidateLayerRequest(pieceSize: 16384, fileSize: 16384, baseLayer: 0, index: 0, count: 1, proofLayers: -1);
        Assert.False(valid);
    }

    [Fact]
    public void ValidateLayerRequest_BaseLayerExceedsTotal_ReturnsFalse()
    {
        // Only 1 block → 1 total level; asking for baseLayer=5 should fail
        bool valid = MerkleTree.ValidateLayerRequest(pieceSize: 16384, fileSize: 16384, baseLayer: 5, index: 0, count: 1, proofLayers: 0);
        Assert.False(valid);
    }

    [Fact]
    public void ValidateLayerRequest_IndexOutOfRange_ReturnsFalse()
    {
        // 2 blocks, piece depth 0, levelSize = 2; index=5 is out of range
        bool valid = MerkleTree.ValidateLayerRequest(pieceSize: 16384, fileSize: 32768, baseLayer: 0, index: 5, count: 1, proofLayers: 0);
        Assert.False(valid);
    }

    // ── GetTotalLevels ────────────────────────────────────────────────────────

    [Fact]
    public void GetTotalLevels_OneBlock_ReturnsZero()
    {
        // 1 block → NextPowerOf2(1)=1 → Log2(1)=0
        Assert.Equal(0, MerkleTree.GetTotalLevels(16384));
    }

    [Fact]
    public void GetTotalLevels_TwoBlocks_ReturnsOne()
    {
        Assert.Equal(1, MerkleTree.GetTotalLevels(32768));
    }

    [Fact]
    public void GetTotalLevels_FourBlocks_ReturnsTwo()
    {
        Assert.Equal(2, MerkleTree.GetTotalLevels(4 * 16384));
    }

    [Fact]
    public void GetTotalLevels_ThreeBlocks_RoundsUpToFour()
    {
        // 3 blocks → NextPowerOf2(3)=4 → Log2(4)=2
        Assert.Equal(2, MerkleTree.GetTotalLevels(3 * 16384));
    }

    // ── CeilingPowerOf2 / FloorLog2 ───────────────────────────────────────────

    [Fact]
    public void CeilingPowerOf2_AlreadyPowerOf2_ReturnsSame()
    {
        Assert.Equal(4, MerkleTree.CeilingPowerOf2(4));
        Assert.Equal(8, MerkleTree.CeilingPowerOf2(8));
    }

    [Fact]
    public void CeilingPowerOf2_NotPowerOf2_RoundsUp()
    {
        Assert.Equal(4, MerkleTree.CeilingPowerOf2(3));
        Assert.Equal(8, MerkleTree.CeilingPowerOf2(5));
    }

    [Fact]
    public void FloorLog2_PowerOf2_ReturnsExponent()
    {
        Assert.Equal(3, MerkleTree.FloorLog2(8));
        Assert.Equal(4, MerkleTree.FloorLog2(16));
    }

    [Fact]
    public void FloorLog2_NotPowerOf2_FloorsDown()
    {
        // floor(log2(5)) = 2 (since 2^2=4 <= 5 < 8=2^3)
        Assert.Equal(2, MerkleTree.FloorLog2(5));
    }

    // ── HashBlock ─────────────────────────────────────────────────────────────

    [Fact]
    public void HashBlock_MatchesSHA256()
    {
        byte[] data = new byte[16384];
        Random.Shared.NextBytes(data);

        byte[] expected = SHA256.HashData(data);
        byte[] actual = MerkleTree.HashBlock(data);

        Assert.Equal(expected, actual);
    }

    // ── ComputeLeaves edge case ───────────────────────────────────────────────

    [Fact]
    public void ComputeLeaves_EmptyData_ReturnsEmptyList()
    {
        var leaves = MerkleTree.ComputeLeaves([]);
        Assert.Empty(leaves);
    }

    [Fact]
    public void ComputeLeaves_ExactlyOneBlock_NoZeroPadding()
    {
        byte[] data = new byte[MerkleTree.BlockSize];
        data[0] = 99;

        var leaves = MerkleTree.ComputeLeaves(data);

        Assert.Single(leaves);
        Assert.Equal(SHA256.HashData(data), leaves[0]);
    }

    // ── VerifyPieceLayerSubsetAgainstRoot ─────────────────────────────────────

    [Fact]
    public void VerifyPieceLayerSubsetAgainstRoot_ValidSinglePiece_ReturnsTrue()
    {
        // 4-block file with pieceSize = 16KB (each piece = 1 block)
        // 4 blocks → totalLevels = 2; pieceDepth = 0; baseTreeLayers = 0 (count=1 → log2(1)=0)
        // proofLayers = 1 (must reach root from single piece: 2 - 0 = 2 levels needed,
        // baseTreeLayers(0) + proofHashes(2) = 2 ✓)
        byte[] fileData = new byte[4 * 16384];
        Random.Shared.NextBytes(fileData);
        uint pieceSize = 16384;
        long fileSize = fileData.Length;

        var leaves = MerkleTree.ComputeLeaves(fileData);
        var root = MerkleTree.ComputeRoot(leaves);
        var pieceLayer = MerkleTree.GetPieceLayer(leaves, pieceSize);

        // Ask for index=0, count=1, proofLayers = totalLevels - baseLayer - 1 = 2 - 1 = 1
        int proofLayers = MerkleTree.GetTotalLevels(fileSize) - 1;
        var proofData = MerkleTree.GetPieceLayerHashesWithProof(pieceLayer, pieceSize, fileSize, 0, 1, proofLayers);
        Assert.NotNull(proofData);

        // Split into piece hashes and proof hashes
        int pieceHashBytes = 1 * MerkleTree.HashSize;
        int proofHashBytes = proofData!.Length - pieceHashBytes;
        int proofCount = proofHashBytes / MerkleTree.HashSize;
        var piecesSubset = new List<byte[]> { proofData[..MerkleTree.HashSize] };
        var proofHashes = new List<byte[]>();
        for (int i = 0; i < proofCount; i++)
        {
            var h = new byte[MerkleTree.HashSize];
            Buffer.BlockCopy(proofData, pieceHashBytes + (i * MerkleTree.HashSize), h, 0, MerkleTree.HashSize);
            proofHashes.Add(h);
        }

        bool valid = MerkleTree.VerifyPieceLayerSubsetAgainstRoot(
            piecesSubset, root, pieceSize, fileSize, 0, proofLayers, proofHashes);

        Assert.True(valid);
    }

    [Fact]
    public void VerifyPieceLayerSubsetAgainstRoot_WrongRoot_ReturnsFalse()
    {
        byte[] fileData = new byte[4 * 16384];
        Random.Shared.NextBytes(fileData);
        uint pieceSize = 16384;
        long fileSize = fileData.Length;

        var leaves = MerkleTree.ComputeLeaves(fileData);
        var pieceLayer = MerkleTree.GetPieceLayer(leaves, pieceSize);
        int proofLayers = MerkleTree.GetTotalLevels(fileSize) - 1;
        var proofData = MerkleTree.GetPieceLayerHashesWithProof(pieceLayer, pieceSize, fileSize, 0, 1, proofLayers);
        Assert.NotNull(proofData);

        int pieceHashBytes = 1 * MerkleTree.HashSize;
        int proofCount = (proofData!.Length - pieceHashBytes) / MerkleTree.HashSize;
        var piecesSubset = new List<byte[]> { proofData[..MerkleTree.HashSize] };
        var proofHashes = new List<byte[]>();
        for (int i = 0; i < proofCount; i++)
        {
            var h = new byte[MerkleTree.HashSize];
            Buffer.BlockCopy(proofData, pieceHashBytes + (i * MerkleTree.HashSize), h, 0, MerkleTree.HashSize);
            proofHashes.Add(h);
        }

        byte[] wrongRoot = new byte[MerkleTree.HashSize];
        wrongRoot[0] = 0xFF;

        bool valid = MerkleTree.VerifyPieceLayerSubsetAgainstRoot(
            piecesSubset, wrongRoot, pieceSize, fileSize, 0, proofLayers, proofHashes);

        Assert.False(valid);
    }

    [Fact]
    public void VerifyPieceLayerSubsetAgainstRoot_WrongProofCount_ReturnsFalse()
    {
        byte[] fileData = new byte[4 * 16384];
        Random.Shared.NextBytes(fileData);
        uint pieceSize = 16384;
        long fileSize = fileData.Length;

        var leaves = MerkleTree.ComputeLeaves(fileData);
        var root = MerkleTree.ComputeRoot(leaves);
        var pieceLayer = MerkleTree.GetPieceLayer(leaves, pieceSize);

        var piecesSubset = new List<byte[]> { pieceLayer[0] };

        // Provide wrong number of proof hashes
        bool valid = MerkleTree.VerifyPieceLayerSubsetAgainstRoot(
            piecesSubset, root, pieceSize, fileSize, 0, proofLayers: 1,
            proofHashes: new List<byte[]>()); // expected 2, got 0

        Assert.False(valid);
    }

    [Fact]
    public void VerifyPieceLayerSubsetAgainstRoot_InvalidLayerRequest_ReturnsFalse()
    {
        var pieces = new List<byte[]> { new byte[32] };
        bool valid = MerkleTree.VerifyPieceLayerSubsetAgainstRoot(
            pieces, new byte[32], pieceSize: 8192 /* invalid */, fileSize: 16384,
            index: 0, proofLayers: 0, proofHashes: new List<byte[]>());

        Assert.False(valid);
    }
}
