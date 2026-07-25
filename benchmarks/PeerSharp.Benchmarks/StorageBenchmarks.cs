using BenchmarkDotNet.Attributes;
using PeerSharp.Internals;
using PeerSharp.PieceWriter;

namespace PeerSharp.Benchmarks;

/// <summary>
/// The engine's hottest disk path: <see cref="Storage"/> is called once per 16 KiB block by
/// BlockCache, so at 50 MB/s this runs a few thousand times a second. Per-call overhead here
/// matters far more than raw disk throughput, which is why the working set is deliberately tiny
/// and stays in the OS cache - we are measuring the code path, not the drive.
///
/// The cross-file cases matter separately: a block that straddles a file boundary takes the
/// multi-lock path, which is where an ill-judged parallelisation can quietly cost more than the
/// concurrency it buys.
/// </summary>
[MemoryDiagnoser]
public class StorageBenchmarks
{
    private const int BlockSize = 16 * 1024;
    private const long FileSize = 4L * 1024 * 1024;

    private string _root = null!;
    private FileHandleCache _handleCache = null!;
    private Storage _storage = null!;
    private byte[] _block = null!;
    private byte[] _readBuffer = null!;

    /// <summary>Offset of a block wholly inside the first file.</summary>
    private const long SingleFileOffset = 1024 * 1024;

    /// <summary>Offset of a block straddling the boundary between file 1 and file 2.</summary>
    private const long BoundaryOffset = FileSize - (BlockSize / 2);

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "PeerSharpBench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var metadata = new TorrentFileMetadata();
        metadata.Info.Name = "bench";
        metadata.Info.PieceSize = 256 * 1024;
        metadata.Info.Files.Add(new TorrentFileEntry { Path = "a.bin", Size = FileSize, Offset = 0 });
        metadata.Info.Files.Add(new TorrentFileEntry { Path = "b.bin", Size = FileSize, Offset = FileSize });
        metadata.Info.FullSize = FileSize * 2;

        _handleCache = new FileHandleCache();
        _storage = new Storage(metadata, _root, new PathValidator(_root), _handleCache, enableSparseFiles: false);
        _storage.InitAsync().GetAwaiter().GetResult();

        _block = new byte[BlockSize];
        Random.Shared.NextBytes(_block);
        _readBuffer = new byte[BlockSize];

        // Populate both regions so the read benchmarks hit real data rather than holes.
        _storage.WriteAsync(SingleFileOffset, _block).AsTask().GetAwaiter().GetResult();
        _storage.WriteAsync(BoundaryOffset, _block).AsTask().GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _storage.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _handleCache.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Benchmark(Description = "Write 16 KiB block (single file)")]
    public ValueTask WriteBlock() => _storage.WriteAsync(SingleFileOffset, _block);

    [Benchmark(Description = "Write 16 KiB block (spans 2 files)")]
    public ValueTask WriteBlockAcrossFiles() => _storage.WriteAsync(BoundaryOffset, _block);

    [Benchmark(Description = "Read 16 KiB block (single file)")]
    public ValueTask ReadBlock() => _storage.ReadAsync(SingleFileOffset, _readBuffer);

    [Benchmark(Description = "Read 16 KiB block (spans 2 files)")]
    public ValueTask ReadBlockAcrossFiles() => _storage.ReadAsync(BoundaryOffset, _readBuffer);
}
