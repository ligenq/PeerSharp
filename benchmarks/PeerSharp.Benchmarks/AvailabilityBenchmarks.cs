using BenchmarkDotNet.Attributes;
using PeerSharp.Core;
using PeerSharp.PiecePicking;

namespace PeerSharp.Benchmarks;

/// <summary>
/// Per-piece availability bookkeeping - the data structure behind RarestFirst selection.
///
/// <see cref="PiecePicker.IncrementAvailability"/> is the per-message path: a peer sending
/// <c>Have</c> costs exactly one call, and a busy swarm produces those continuously. Each call
/// takes the picker's selection lock on its own, so the lock cost is paid per piece, not
/// amortised.
///
/// <see cref="BulkIncrementUnbatched"/> shows what that costs when the same API is driven in a
/// loop over the whole piece range. Read it as an upper bound on peer connect rather than as the
/// real figure: <see cref="PiecePicker.RegisterPeerAvailability"/> takes the lock once and loops
/// inside it, so it pays one acquisition instead of <c>PieceCount</c> of them. That method takes
/// a concrete <c>PeerCommunication</c>, which cannot be built without a live torrent, bandwidth
/// manager and transport, so it is not directly measurable here - the gap between these rows and
/// the real cost is precisely the per-call locking.
/// </summary>
[MemoryDiagnoser]
public class AvailabilityBenchmarks
{
    private PiecePicker _picker = null!;
    private FakeContext _context = null!;
    private PiecesProgress _partialBitfield = null!;

    /// <summary>Piece count; 20,000 is a large multi-gigabyte torrent.</summary>
    [Params(2_000, 20_000)]
    public int PieceCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _context = new FakeContext(PieceCount);
        _picker = new PiecePicker(_context, TimeProvider.System, new Random(20260726));

        var random = new Random(20260726);
        _partialBitfield = new PiecesProgress(PieceCount);
        for (int i = 0; i < PieceCount; i++)
        {
            if (random.Next(100) < 60)
            {
                _partialBitfield.AddPiece(i);
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _picker.Dispose();

    /// <summary>
    /// One <c>Have</c> message from one peer. This is the figure that scales with swarm chatter.
    /// </summary>
    [Benchmark(Baseline = true, Description = "IncrementAvailability, single piece")]
    public void IncrementOne() => _picker.IncrementAvailability(PieceCount / 2);

    [Benchmark(Description = "GetAvailability, single piece")]
    public int ReadAvailability() => _picker.GetAvailability(PieceCount / 2);

    /// <summary>
    /// The whole piece range through the per-piece API, taking the selection lock once per piece.
    /// An upper bound on peer connect; see the class remarks.
    /// </summary>
    [Benchmark(Description = "Whole range, unbatched (upper bound on connect)")]
    public void BulkIncrementUnbatched()
    {
        for (int i = 0; i < PieceCount; i++)
        {
            if (_partialBitfield.HasPiece(i))
            {
                _picker.IncrementAvailability(i);
            }
        }

        for (int i = 0; i < PieceCount; i++)
        {
            if (_partialBitfield.HasPiece(i))
            {
                _picker.DecrementAvailability(i);
            }
        }
    }

    /// <summary>
    /// The bitfield probe alone, with no picker involvement. Subtracting this from the row above
    /// isolates how much of the walk is lock acquisition rather than reading the peer's pieces.
    /// </summary>
    [Benchmark(Description = "Whole range, bitfield probe only")]
    public int BitfieldProbeOnly()
    {
        int count = 0;
        for (int i = 0; i < PieceCount; i++)
        {
            if (_partialBitfield.HasPiece(i))
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>Minimal context: availability bookkeeping only consults PieceCount.</summary>
    private sealed class FakeContext(int pieceCount) : IPiecePickerContext
    {
        public DownloadStrategy DownloadStrategy => DownloadStrategy.RarestFirst;
        public int PieceCount => pieceCount;
        public int CompletedPieceCount => 0;
        public IReadOnlyList<int>? StreamingPriorityPieces => null;

        public IReadOnlyList<FileSelection>? GetFileSelectionSnapshot() => null;

        public Priority GetPiecePriority(int pieceIndex, IReadOnlyList<FileSelection>? selection) => Priority.Normal;

        public bool HasPiece(int pieceIndex) => false;

        public bool IsPieceActive(int pieceIndex) => false;

        public bool IsPieceNeeded(int pieceIndex, IReadOnlyList<FileSelection>? selection) => true;
    }
}
