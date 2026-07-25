using BenchmarkDotNet.Attributes;
using PeerSharp.Internals.Utilities;

namespace PeerSharp.Benchmarks;

/// <summary>
/// BEP 52 hashing. Every v2 or hybrid torrent build walks the whole payload through
/// <see cref="MerkleTree.ComputeLeaves"/>, and every received v2 piece is verified against the
/// piece layer, so both the build and the verify direction are worth tracking.
///
/// Sizes are chosen to straddle a power-of-two leaf count, since the tree pads up to the next
/// power of two and the cost step at that boundary is the interesting part.
/// </summary>
[MemoryDiagnoser]
public class MerkleTreeBenchmarks
{
    private const uint PieceSize = 256 * 1024;

    private byte[] _data = null!;
    private byte[] _piece = null!;
    private List<byte[]> _leaves = null!;
    private List<byte[]> _pieceLayer = null!;
    private byte[] _pieceLayerHash = null!;

    /// <summary>Payload size in bytes; 4 MiB is 256 leaves, 6 MiB forces padding to 512.</summary>
    [Params(4 * 1024 * 1024, 6 * 1024 * 1024)]
    public int DataSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _data = new byte[DataSize];
        Random.Shared.NextBytes(_data);

        _leaves = MerkleTree.ComputeLeaves(_data);
        _pieceLayer = MerkleTree.GetPieceLayer(_leaves, PieceSize);

        _piece = new byte[PieceSize];
        _data.AsSpan(0, (int)PieceSize).CopyTo(_piece);
        _pieceLayerHash = _pieceLayer[0];
    }

    [Benchmark(Description = "ComputeLeaves (16 KiB blocks)")]
    public List<byte[]> ComputeLeaves() => MerkleTree.ComputeLeaves(_data);

    [Benchmark(Description = "ComputeRoot from leaves")]
    public byte[] ComputeRoot() => MerkleTree.ComputeRoot(_leaves);

    [Benchmark(Description = "GetPieceLayer from leaves")]
    public List<byte[]> GetPieceLayer() => MerkleTree.GetPieceLayer(_leaves, PieceSize);

    [Benchmark(Description = "Full build: leaves + root + piece layer")]
    public byte[] FullBuild()
    {
        var leaves = MerkleTree.ComputeLeaves(_data);
        _ = MerkleTree.GetPieceLayer(leaves, PieceSize);
        return MerkleTree.ComputeRoot(leaves);
    }

    [Benchmark(Description = "VerifyPiece against piece layer")]
    public bool VerifyPiece() => MerkleTree.VerifyPiece(_piece, 0, _pieceLayerHash, PieceSize);
}
