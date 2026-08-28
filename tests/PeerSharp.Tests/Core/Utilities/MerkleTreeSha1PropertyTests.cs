using CsCheck;
using PeerSharp.Internals.Utilities;

namespace PeerSharp.Tests.Core.Utilities;

/// <summary>
/// What a Merkle proof has to do: accept the data it was built from, and reject everything else.
/// </summary>
/// <remarks>
/// <para>
/// The realistic sequence is the one modelled here. The root arrives in the torrent and is the only
/// thing trusted; a peer then sends a piece together with the uncle hashes along its path, and the
/// engine decides whether that piece belongs to that root. Nothing else guards this - accepting a bad
/// proof means writing a stranger's bytes to disk as though they were verified.
/// </para>
/// <para>
/// The interesting inputs are piece counts that are not powers of two, because the tree is padded up
/// to one and the padding is what makes the index arithmetic and the sibling ordering easy to get
/// wrong. Those cases are laborious to enumerate by hand and free to generate.
/// </para>
/// </remarks>
public class MerkleTreeSha1PropertyTests
{
    private static readonly Gen<byte[][]> Pieces = Gen.Byte.Array[1, 32].Array[1, 12];

    [Fact]
    public void APieceVerifiesAgainstTheRootItWasBuiltInto()
    {
        Pieces.Sample(pieces =>
        {
            var tree = Build(pieces);

            for (int i = 0; i < pieces.Length; i++)
            {
                Assert.True(tree.VerifyPiece(i, pieces[i]), $"piece {i} of {pieces.Length} did not verify against its own root");
            }
        }, iter: 2_000);
    }

    [Fact]
    public void AProofCarriedOverTheWireStillVerifies()
    {
        // The tree the engine actually verifies against: the root from the torrent, plus a piece and
        // the uncles a peer sent for it. If GetUncleHashes and SetUncleHashes disagreed about the
        // order they walk the path, this is where it would show, and only here - the fully built tree
        // verifies either way because it already holds every node.
        Pieces.Sample(pieces =>
        {
            var full = Build(pieces);

            for (int i = 0; i < pieces.Length; i++)
            {
                var received = new MerkleTreeSha1(pieces.Length, full.Root!);
                received.SetPieceHash(i, MerkleTreeSha1.ComputePieceHash(pieces[i]));
                received.SetUncleHashes(i, full.GetUncleHashes(i));

                Assert.True(received.CanVerifyPiece(i), $"piece {i} of {pieces.Length} was not verifiable from root and uncles");
                Assert.True(received.VerifyPiece(i, pieces[i]), $"piece {i} of {pieces.Length} failed to verify from root and uncles");
            }
        }, iter: 2_000);
    }

    [Fact]
    public void AlteredDataDoesNotVerify()
    {
        // One flipped bit anywhere in the piece. This is the whole point of the structure: without it
        // a peer can send whatever it likes for a piece the torrent names.
        Gen.Select(Pieces, Gen.Int[0, 11], Gen.Int[0, 31], Gen.Int[0, 7]).Sample((pieces, pieceSeed, byteSeed, bit) =>
        {
            var tree = Build(pieces);

            int index = pieceSeed % pieces.Length;
            byte[] altered = (byte[])pieces[index].Clone();
            altered[byteSeed % altered.Length] ^= (byte)(1 << bit);

            Assert.False(tree.VerifyPiece(index, altered), $"a piece with one bit flipped verified against the root");
        }, iter: 5_000);
    }

    [Fact]
    public void APieceDoesNotVerifyAtAnotherPiecesIndex()
    {
        // A proof is for one position in the tree. Data that is genuine at index i must not be
        // accepted at index j, or a peer could satisfy every request with one valid piece.
        Pieces.Where(pieces => pieces.Length > 1).Sample(pieces =>
        {
            var tree = Build(pieces);

            for (int i = 0; i < pieces.Length; i++)
            {
                int other = (i + 1) % pieces.Length;
                if (pieces[i].AsSpan().SequenceEqual(pieces[other]))
                {
                    // Identical bytes genuinely belong at either index.
                    continue;
                }

                Assert.False(tree.VerifyPiece(other, pieces[i]), $"piece {i} verified at index {other}");
            }
        }, iter: 2_000);
    }

    [Fact]
    public void LeafIndexesRoundTrip()
    {
        Pieces.Sample(pieces =>
        {
            var tree = Build(pieces);

            for (int i = 0; i < pieces.Length; i++)
            {
                Assert.Equal(i, tree.NodeToPieceIndex(tree.PieceToNodeIndex(i)));
                Assert.Equal(MerkleTreeSha1.ComputePieceHash(pieces[i]), tree.GetPieceHash(i));
            }
        }, iter: 2_000);
    }

    [Fact]
    public void AnIndexOutsideTheTreeIsRejectedRatherThanThrowing()
    {
        // Piece indexes reach this from peer messages, so out of range is untrusted input rather
        // than a programming error.
        Gen.Select(Pieces, Gen.Int[-50, 50]).Sample((pieces, index) =>
        {
            var tree = Build(pieces);
            bool inRange = index >= 0 && index < pieces.Length;

            Assert.Equal(inRange, tree.VerifyPiece(index, inRange ? pieces[index] : []));
            Assert.False(tree.CanVerifyPiece(index) && !inRange);
        }, iter: 2_000);
    }

    private static MerkleTreeSha1 Build(byte[][] pieces)
    {
        var tree = new MerkleTreeSha1(pieces.Length);
        tree.BuildFromPieceHashes([.. pieces.Select(piece => MerkleTreeSha1.ComputePieceHash(piece))]);
        return tree;
    }
}
