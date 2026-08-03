using BenchmarkDotNet.Attributes;
using PeerSharp.Core;
using PeerSharp.PiecePicking;

namespace PeerSharp.Benchmarks;

/// <summary>
/// Piece selection runs once per outstanding request slot per peer, so on a 50-peer swarm with a
/// deep pipeline it is called hundreds of times a second and scales with piece count.
///
/// The three strategies are measured separately because they have genuinely different shapes:
/// RarestFirst scans availability across all pieces, Sequential short-circuits at the first gap,
/// and Streaming consults the priority window first. A change that helps one can easily cost
/// another.
/// </summary>
[MemoryDiagnoser]
public class PiecePickerBenchmarks
{
    private PiecePicker _picker = null!;
    private FakePeer _peer = null!;
    private FakeContext _context = null!;

    /// <summary>Piece count; 2000 is a ~500 MB torrent, 20000 a large multi-gigabyte one.</summary>
    [Params(2_000, 20_000)]
    public int PieceCount { get; set; }

    [Params(DownloadStrategy.RarestFirst, DownloadStrategy.Sequential, DownloadStrategy.Streaming)]
    public DownloadStrategy Strategy { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(20260725);
        _context = new FakeContext(PieceCount, Strategy, random);
        _peer = new FakePeer(PieceCount, random);

        // Deterministic seed: piece selection consults Random for tie-breaking, and an
        // unseeded one would make run-to-run comparisons meaningless.
        _picker = new PiecePicker(_context, TimeProvider.System, new Random(20260725));

        // Populate availability as if a full swarm were connected, so RarestFirst has a real
        // distribution to scan rather than a uniform one.
        for (int peer = 0; peer < 40; peer++)
        {
            for (int piece = 0; piece < PieceCount; piece++)
            {
                if (random.Next(100) < 60)
                {
                    _picker.IncrementAvailability(piece);
                }
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _picker.Dispose();

    [Benchmark(Description = "PickNextPiece")]
    public int PickNextPiece()
    {
        return _picker.PickNextPiece(_peer, out int pieceIndex) ? pieceIndex : -1;
    }

    private sealed class FakeContext : IPiecePickerContext
    {
        private readonly bool[] _have;

        public FakeContext(int pieceCount, DownloadStrategy strategy, Random random)
        {
            PieceCount = pieceCount;
            DownloadStrategy = strategy;
            _have = BuildHave(pieceCount, random);

            // Precomputed, because the real context exposes Torrent.Pieces.ReceivedCount - an
            // O(1) counter. Recomputing it per call added a full LINQ scan of the piece range to
            // every PickNextPiece, a cost the engine never pays.
            CompletedPieceCount = _have.Count(static x => x);

            StreamingPriorityPieces = strategy == DownloadStrategy.Streaming
                ? [.. Enumerable.Range(0, Math.Min(32, pieceCount))]
                : null;
        }

        public DownloadStrategy DownloadStrategy { get; }
        public int PieceCount { get; }
        public int CompletedPieceCount { get; }
        public IReadOnlyList<int>? StreamingPriorityPieces { get; }

        public IReadOnlyList<FileSelection>? GetFileSelectionSnapshot() => null;

        public Priority GetPiecePriority(int pieceIndex, IReadOnlyList<FileSelection>? selection) => Priority.Normal;

        public bool HasPiece(int pieceIndex) => _have[pieceIndex];

        public bool IsPieceActive(int pieceIndex) => false;

        public bool IsPieceNeeded(int pieceIndex, IReadOnlyList<FileSelection>? selection) => !_have[pieceIndex];

        // A half-finished download is the realistic steady state; an empty or nearly complete
        // bitfield would let the picker short-circuit and hide its actual cost.
        private static bool[] BuildHave(int count, Random random)
        {
            var have = new bool[count];
            for (int i = 0; i < count; i++)
            {
                have[i] = random.Next(100) < 50;
            }
            return have;
        }
    }

    private sealed class FakePeer(int pieceCount, Random random) : IPeerPieceInfo
    {
        private readonly bool[] _bitfield = BuildBitfield(pieceCount, random);

        public int Count => pieceCount;
        public bool IsChoking => false;

        public bool IsSnubbed { get; set; }

        public IEnumerable<int> GetSuggestedPieces() => [];

        public bool HasPiece(int pieceIndex) => _bitfield[pieceIndex];

        public bool IsAllowedFast(int pieceIndex) => false;

        private static bool[] BuildBitfield(int count, Random random)
        {
            var bitfield = new bool[count];
            for (int i = 0; i < count; i++)
            {
                bitfield[i] = random.Next(100) < 80;
            }
            return bitfield;
        }
    }
}
