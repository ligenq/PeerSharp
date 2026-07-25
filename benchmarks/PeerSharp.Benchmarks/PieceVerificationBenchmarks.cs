using BenchmarkDotNet.Attributes;
using PeerSharp.Internals.Utilities;
using System.Security.Cryptography;

namespace PeerSharp.Benchmarks;

/// <summary>
/// Piece hash verification - the longest CPU-bound operation a user ever waits on. Every
/// completed piece runs through this once, and a full recheck of an existing download is
/// essentially nothing but this in a loop.
///
/// The three torrent generations use genuinely different algorithms and are measured separately:
/// v1 is a single SHA-1 over the piece, v2 walks a SHA-256 Merkle path against the piece layer,
/// and BEP 30 walks a SHA-1 Merkle path to the root.
///
/// <see cref="Bep30VerifyViaByteArray"/> exists to quantify a specific inefficiency rather than
/// to measure a path worth keeping: the production caller hands this method
/// <c>pieceData.ToArray()</c>, copying the whole piece even though a ReadOnlySpan overload sits
/// next to it. The pair of benchmarks prices that copy.
/// </summary>
[MemoryDiagnoser]
public class PieceVerificationBenchmarks
{
    private byte[] _piece = null!;
    private byte[] _v1ExpectedHash = null!;
    private byte[] _v2PieceLayerHash = null!;
    private MerkleTreeSha1 _bep30Tree = null!;

    /// <summary>Piece size in bytes. Common real-world torrents sit between these.</summary>
    [Params(256 * 1024, 4 * 1024 * 1024)]
    public int PieceSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _piece = new byte[PieceSize];
        Random.Shared.NextBytes(_piece);

        _v1ExpectedHash = SHA1.HashData(_piece);

        var leaves = MerkleTree.ComputeLeaves(_piece);
        _v2PieceLayerHash = MerkleTree.GetPieceLayer(leaves, (uint)PieceSize)[0];

        // A 64-piece BEP 30 tree with our piece at index 0, so the verify walks a real path
        // to the root rather than short-circuiting at a one-node tree.
        var pieceHashes = new List<byte[]>(64) { _v1ExpectedHash };
        for (int i = 1; i < 64; i++)
        {
            var filler = new byte[20];
            Random.Shared.NextBytes(filler);
            pieceHashes.Add(filler);
        }

        _bep30Tree = new MerkleTreeSha1(64);
        _bep30Tree.BuildFromPieceHashes(pieceHashes);
    }

    [Benchmark(Baseline = true, Description = "v1: SHA-1 over piece")]
    public bool V1Verify() => SHA1.HashData(_piece).AsSpan().SequenceEqual(_v1ExpectedHash);

    [Benchmark(Description = "v2: Merkle path vs piece layer")]
    public bool V2Verify() => MerkleTree.VerifyPiece(_piece, 0, _v2PieceLayerHash, (uint)PieceSize);

    [Benchmark(Description = "BEP 30: Merkle path (span)")]
    public bool Bep30VerifyViaSpan() => _bep30Tree.VerifyPiece(0, _piece.AsSpan());

    [Benchmark(Description = "BEP 30: Merkle path (byte[] copy, as called today)")]
    public bool Bep30VerifyViaByteArray() => _bep30Tree.VerifyPiece(0, _piece.ToArray());
}
