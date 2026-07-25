using BenchmarkDotNet.Attributes;
using PeerSharp.Core;

namespace PeerSharp.Benchmarks;

/// <summary>
/// Torrent creation throughput. This is the one place where a user-visible operation is
/// unambiguously CPU-bound on hashing, so it is the clearest signal for regressions in the
/// build pipeline.
///
/// V1 (SHA-1 over pieces), V2 (SHA-256 Merkle trees) and Hybrid (both) are separated because
/// Hybrid is the interesting case: it used to read each file two or three times and now computes
/// the V1 piece stream, the V2 Merkle data and the per-file SHA-1 digests in a single pass.
/// Hybrid should track close to V1+V2 rather than exceeding it.
///
/// Files live on disk rather than in memory so the async path exercises real file streams.
/// </summary>
[MemoryDiagnoser]
public class TorrentFileBuilderBenchmarks
{
    private string _root = null!;
    private string[] _files = null!;

    /// <summary>Total payload across all files.</summary>
    [Params(16 * 1024 * 1024)]
    public int TotalBytes { get; set; }

    /// <summary>Spreading the same payload over more files exercises per-file setup cost.</summary>
    [Params(1, 32)]
    public int FileCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "PeerSharpBench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        int perFile = TotalBytes / FileCount;
        var payload = new byte[perFile];
        Random.Shared.NextBytes(payload);

        _files = new string[FileCount];
        for (int i = 0; i < FileCount; i++)
        {
            _files[i] = Path.Combine(_root, $"file{i}.bin");
            File.WriteAllBytes(_files[i], payload);
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private TorrentFileBuilder NewBuilder(TorrentFileVersion version, bool perFileSha1)
    {
        var builder = new TorrentFileBuilder()
            .WithName("benchmark")
            .WithVersion(version)
            .WithPieceLength(256 * 1024);

        if (perFileSha1)
        {
            builder = builder.WithPerFileSha1();
        }

        for (int i = 0; i < _files.Length; i++)
        {
            builder = builder.AddFileFromPath(_files[i], $"benchmark/file{i}.bin");
        }

        return builder;
    }

    [Benchmark(Description = "Build V1")]
    public Task<TorrentFile> BuildV1() => NewBuilder(TorrentFileVersion.V1, perFileSha1: false).BuildAsync();

    [Benchmark(Description = "Build V2")]
    public Task<TorrentFile> BuildV2() => NewBuilder(TorrentFileVersion.V2, perFileSha1: false).BuildAsync();

    [Benchmark(Description = "Build Hybrid")]
    public Task<TorrentFile> BuildHybrid() => NewBuilder(TorrentFileVersion.Hybrid, perFileSha1: false).BuildAsync();

    [Benchmark(Description = "Build Hybrid + per-file SHA-1")]
    public Task<TorrentFile> BuildHybridWithSha1() => NewBuilder(TorrentFileVersion.Hybrid, perFileSha1: true).BuildAsync();
}
