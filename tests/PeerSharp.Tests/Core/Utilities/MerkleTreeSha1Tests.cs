using PeerSharp.Internals.Utilities;

namespace PeerSharp.Tests.Core.Utilities;

public class MerkleTreeSha1Tests
{
    [Fact]
    public void BuildFromPieceHashes_VerifyPieces()
    {
        var pieces = new[]
        {
            new byte[] { 1, 2, 3 },
            new byte[] { 4, 5, 6, 7 },
            new byte[] { 8, 9 }
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
            new byte[] { 20, 21, 22 }
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
}




