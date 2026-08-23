using PeerSharp.Internals.Utilities;
using System.Security.Cryptography;

namespace PeerSharp.Tests.Core.Utilities;

/// <summary>
/// BEP 52 piece verification, checked against a tree built here rather than by the engine.
///
/// <para>
/// This exists because the engine agreeing with itself is exactly how the bug it covers survived.
/// PeerSharp zero-padded the data of a short final block to 16 KiB before hashing it, and made the
/// same mistake when building a torrent as when verifying one - so every round trip through
/// PeerSharp matched, every merkle test passed, and only the rest of the world disagreed. Measured
/// against a torrent from libtorrent's <c>connection_tester</c>: every full-size piece verified and
/// the last piece of every file did not, which is one failure per file and enough to end a download,
/// because a peer that alone supplied a bad piece is dropped.
/// </para>
/// <para>
/// So the expected values below are computed from the spec with <see cref="SHA256"/> directly. The
/// only thing borrowed from the engine is the data being hashed.
/// </para>
/// </summary>
public class MerkleV2ConformanceTests
{
    private const int BlockSize = 16 * 1024;

    [Fact]
    public void AShortFinalBlockIsHashedAtItsRealLength()
    {
        // The rule, stated against plain SHA-256: BEP 52 pads the tree with zero hashes past the end
        // of the data, and never pads the data.
        byte[] tail = RandomNumberGenerator.GetBytes(1234);

        var leaves = MerkleTree.ComputeLeaves(tail);

        Assert.Equal(SHA256.HashData(tail), Assert.Single(leaves));
    }

    [Fact]
    public void APieceEndingMidBlockVerifiesAgainstAnIndependentlyBuiltRoot()
    {
        // A final piece of a multi-piece file: two whole blocks and a partial third, padded up to a
        // full piece with zero hashes. This is the shape that failed.
        const uint pieceSize = 8 * BlockSize;
        byte[] piece = RandomNumberGenerator.GetBytes((2 * BlockSize) + 5000);

        byte[] expectedRoot = BuildPieceRoot(piece, (int)(pieceSize / BlockSize));

        Assert.True(
            MerkleTree.VerifyPiece(piece, pieceIndex: 3, expectedRoot, pieceSize, padToPieceSize: true),
            "a piece whose last block is partial must verify against the spec's tree");
    }

    [Fact]
    public void APieceEndingOnABlockBoundaryStillVerifies()
    {
        // The case that always worked, kept so a fix aimed at the tail cannot break the common path.
        const uint pieceSize = 8 * BlockSize;
        byte[] piece = RandomNumberGenerator.GetBytes(3 * BlockSize);

        byte[] expectedRoot = BuildPieceRoot(piece, (int)(pieceSize / BlockSize));

        Assert.True(MerkleTree.VerifyPiece(piece, 1, expectedRoot, pieceSize, padToPieceSize: true));
    }

    [Fact]
    public void PaddingTheDataInsteadOfTheTreeIsRejected()
    {
        // The old behaviour, asserted as wrong. Without this the fix could be undone by anything that
        // reintroduces a widened buffer, and every other merkle test would still pass.
        const uint pieceSize = 8 * BlockSize;
        byte[] piece = RandomNumberGenerator.GetBytes(BlockSize + 77);

        byte[] wrongRoot = BuildPieceRootPaddingTheData(piece, (int)(pieceSize / BlockSize));

        Assert.False(
            MerkleTree.VerifyPiece(piece, 2, wrongRoot, pieceSize, padToPieceSize: true),
            "a root built by zero-padding the last block's data must not be accepted");
    }

    /// <summary>
    /// The spec's construction, written out: hash each block at the length it has, pad the leaf layer
    /// to a full piece with zero hashes, then combine pairs to a single root.
    /// </summary>
    private static byte[] BuildPieceRoot(ReadOnlySpan<byte> piece, int blocksPerPiece)
    {
        var layer = new List<byte[]>();
        for (int offset = 0; offset < piece.Length; offset += BlockSize)
        {
            int length = Math.Min(BlockSize, piece.Length - offset);
            layer.Add(SHA256.HashData(piece.Slice(offset, length)));
        }

        while (layer.Count < blocksPerPiece)
        {
            layer.Add(new byte[32]);
        }

        return CollapseToRoot(layer);
    }

    /// <summary>The same construction with the mistake this file exists to rule out.</summary>
    private static byte[] BuildPieceRootPaddingTheData(ReadOnlySpan<byte> piece, int blocksPerPiece)
    {
        var layer = new List<byte[]>();
        for (int offset = 0; offset < piece.Length; offset += BlockSize)
        {
            int length = Math.Min(BlockSize, piece.Length - offset);
            byte[] widened = new byte[BlockSize];
            piece.Slice(offset, length).CopyTo(widened);
            layer.Add(SHA256.HashData(widened));
        }

        while (layer.Count < blocksPerPiece)
        {
            layer.Add(new byte[32]);
        }

        return CollapseToRoot(layer);
    }

    private static byte[] CollapseToRoot(List<byte[]> layer)
    {
        while (layer.Count > 1)
        {
            var next = new List<byte[]>(layer.Count / 2);
            for (int i = 0; i < layer.Count; i += 2)
            {
                byte[] combined = new byte[64];
                layer[i].CopyTo(combined, 0);
                layer[i + 1].CopyTo(combined, 32);
                next.Add(SHA256.HashData(combined));
            }

            layer = next;
        }

        return layer[0];
    }
}
