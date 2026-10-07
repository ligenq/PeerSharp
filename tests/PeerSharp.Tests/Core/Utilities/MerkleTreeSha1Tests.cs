using PeerSharp.Internals.Utilities;

namespace PeerSharp.Tests.Core.Utilities;

public class MerkleTreeSha1Tests
{
    [Fact]
    public void SinglePieceTrustedRootCannotBeReplacedByAnUntrustedHash()
    {
        byte[] good = [1, 2, 3];
        byte[] bad = [4, 5, 6];
        byte[] root = MerkleTreeSha1.ComputePieceHash(good);
        var tree = new MerkleTreeSha1(1, root);
        tree.SetPieceHash(0, MerkleTreeSha1.ComputePieceHash(bad));
        tree.SetNode(0, MerkleTreeSha1.ComputePieceHash(bad));
        Assert.False(tree.TryAddPieceProof(0, MerkleTreeSha1.ComputePieceHash(bad), []));
        Assert.Equal(root, tree.Root);
        Assert.True(tree.VerifyPiece(0, good));
        Assert.False(tree.VerifyPiece(0, bad));
    }

    [Fact]
    public void InvalidProofCannotPoisonAnAlreadyAuthenticatedPiece()
    {
        byte[] first = [1, 2, 3];
        byte[] second = [4, 5, 6];
        var hashes = new[] { first, second }.Select(data => MerkleTreeSha1.ComputePieceHash(data)).ToList();
        var full = new MerkleTreeSha1(2);
        full.BuildFromPieceHashes(hashes);
        var tree = new MerkleTreeSha1(2, full.Root!);
        Assert.True(tree.TryAddPieceProof(0, hashes[0], [hashes[1]]));
        Assert.True(tree.VerifyPiece(0, first));
        Assert.False(tree.TryAddPieceProof(1, new byte[20], [new byte[20]]));
        Assert.True(tree.VerifyPiece(0, first));
        Assert.True(tree.TryAddPieceProof(1, hashes[1], [hashes[0]]));
        Assert.True(tree.VerifyPiece(1, second));
    }

    [Fact]
    public void BuildFromPieceHashes_VerifyPieces()
    {
        var pieces = new[]
        {
            new byte[] { 1, 2, 3 },
            [4, 5, 6, 7],
            [8, 9]
        };

        var hashes = pieces.Select(p => MerkleTreeSha1.ComputePieceHash(p.AsSpan())).ToList();
        var tree = new MerkleTreeSha1(pieces.Length);
        tree.BuildFromPieceHashes(hashes);

        Assert.NotNull(tree.Root);
        Assert.True(tree.CanVerifyPiece(0));
        Assert.True(tree.CanVerifyPiece(1));
        Assert.True(tree.CanVerifyPiece(2));

        for (int i = 0; i < pieces.Length; i++)
        {
            Assert.True(tree.VerifyPiece(i, pieces[i]));
            Assert.Equal(i, tree.NodeToPieceIndex(tree.PieceToNodeIndex(i)));
        }

        int depth = tree.GetDepth();
        var uncles = tree.GetUncleHashes(0);
        Assert.Equal(depth - 1, uncles.Count);
    }

    [Fact]
    public void VerifyPiece_WithUnclesAndRoot()
    {
        var data = new[]
        {
            new byte[] { 10, 11, 12 },
            [20, 21, 22]
        };

        var hashes = data.Select(p => MerkleTreeSha1.ComputePieceHash(p.AsSpan())).ToList();
        var fullTree = new MerkleTreeSha1(data.Length);
        fullTree.BuildFromPieceHashes(hashes);

        var root = fullTree.Root!;
        var uncles = fullTree.GetUncleHashes(1);

        var partialTree = new MerkleTreeSha1(data.Length, root);
        partialTree.SetPieceHash(1, hashes[1]);
        partialTree.SetUncleHashes(1, uncles);

        Assert.True(partialTree.CanVerifyPiece(1));
        Assert.True(partialTree.VerifyPiece(1, data[1]));
    }

    [Fact]
    public void GetNode_ValidIndex_ReturnsHash()
    {
        var tree = new MerkleTreeSha1(2);
        var expectedHash = new byte[20];
        expectedHash[0] = 42;
        tree.SetNode(0, expectedHash);

        var result = tree.GetNode(0);

        Assert.NotNull(result);
        Assert.Equal(expectedHash, result);
    }

    [Fact]
    public void GetNode_InvalidIndex_ReturnsNull()
    {
        var tree = new MerkleTreeSha1(2);

        Assert.Null(tree.GetNode(-1));
        Assert.Null(tree.GetNode(tree.TreeSize));
        Assert.Null(tree.GetNode(tree.TreeSize + 1));
    }

    [Fact]
    public void SetNode_ValidIndex_SetsHash()
    {
        var tree = new MerkleTreeSha1(2);
        var expectedHash = new byte[20];
        expectedHash[0] = 99;

        tree.SetNode(1, expectedHash);

        var result = tree.GetNode(1);
        Assert.Equal(expectedHash, result);
    }

    [Fact]
    public void SetNode_InvalidIndex_DoesNothing()
    {
        var tree = new MerkleTreeSha1(2);
        var expectedHash = new byte[20];
        expectedHash[0] = 99;

        // Ensure no exception is thrown
        tree.SetNode(-1, expectedHash);
        tree.SetNode(tree.TreeSize, expectedHash);
    }
}




