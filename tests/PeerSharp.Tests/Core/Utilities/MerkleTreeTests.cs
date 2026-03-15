using PeerSharp.Internals.Utilities;

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
    public void VerifyPieceLayerAgainstRoot_Valid_ReturnsTrue()
    {
        // 1MB file, 32KB pieces
        long fileSize = 1024 * 1024;
        uint pieceSize = 32768;
        byte[] fileData = new byte[fileSize];
        Random.Shared.NextBytes(fileData);

        var leaves = MerkleTree.ComputeLeaves(fileData);
        var root = MerkleTree.ComputeRoot(leaves);
        var pieceLayer = MerkleTree.GetPieceLayer(leaves, pieceSize);

        bool valid = MerkleTree.VerifyPieceLayerAgainstRoot(pieceLayer, root, pieceSize, fileSize);
        Assert.True(valid);
    }
}





