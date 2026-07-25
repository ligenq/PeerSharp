using BenchmarkDotNet.Attributes;
using PeerSharp.PieceWriter;

namespace PeerSharp.Benchmarks;

/// <summary>
/// Byte-range to file-operation mapping. Every single <see cref="Storage"/> read and write starts
/// here, so it inherits the block cadence - a few thousand calls a second on a fast download.
///
/// It is measured on its own because the Storage suite folds this cost into disk I/O and hides
/// how it scales. File counts in the thousands are ordinary for season packs and game repacks,
/// and that is the regime where an O(files) lookup would start to show.
/// </summary>
[MemoryDiagnoser]
public class FileMapperBenchmarks
{
    private const int BlockSize = 16 * 1024;

    private FileMapper _mapper = null!;
    private long _midOffset;
    private long _lateOffset;
    private long _boundaryOffset;

    /// <summary>Files in the torrent. 10k is a large but entirely realistic pack.</summary>
    [Params(2, 500, 10_000)]
    public int FileCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var sizes = new long[FileCount];
        for (int i = 0; i < FileCount; i++)
        {
            // Uneven sizes, so offsets cannot be resolved by a divide.
            sizes[i] = 1024L * 1024 * ((i % 7) + 1);
        }

        _mapper = new FileMapper(sizes);

        long total = _mapper.TotalSize;
        _midOffset = total / 2;
        // Late offsets are the worst case for any structure that scans from the front.
        _lateOffset = total - (BlockSize * 4);

        // An offset positioned so the block straddles the first file boundary, forcing MapRange
        // to emit two operations instead of one.
        _boundaryOffset = sizes[0] - (BlockSize / 2);
    }

    [Benchmark(Baseline = true, Description = "MapOffset, mid-torrent")]
    public int MapOffsetMid() => _mapper.MapOffset(_midOffset).FileIndex;

    [Benchmark(Description = "MapOffset, last file")]
    public int MapOffsetLate() => _mapper.MapOffset(_lateOffset).FileIndex;

    [Benchmark(Description = "MapRange, 16 KiB inside one file")]
    public int MapRangeSingleFile()
    {
        int count = 0;
        foreach (var _ in _mapper.MapRange(_midOffset, BlockSize))
        {
            count++;
        }
        return count;
    }

    [Benchmark(Description = "MapRange, 16 KiB spanning a boundary")]
    public int MapRangeAcrossFiles()
    {
        int count = 0;
        foreach (var _ in _mapper.MapRange(_boundaryOffset, BlockSize))
        {
            count++;
        }
        return count;
    }

    /// <summary>A whole 4 MiB piece, which on a many-small-files torrent fans out widely.</summary>
    [Benchmark(Description = "MapRange, 4 MiB piece")]
    public int MapRangeWholePiece()
    {
        int count = 0;
        foreach (var _ in _mapper.MapRange(_midOffset, 4 * 1024 * 1024))
        {
            count++;
        }
        return count;
    }
}
