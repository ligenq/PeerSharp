using BenchmarkDotNet.Attributes;
using PeerSharp.Core;

namespace PeerSharp.Benchmarks;

/// <summary>
/// End-to-end .torrent parsing. Distinct from <see cref="BencodeBenchmarks"/>, which measures the
/// decoder alone: this adds info-hash computation, structural validation and - for v2 and hybrid -
/// file-tree walking and piece-layer parsing.
///
/// It runs whenever a user adds a torrent, on every entry when a session is restored, and once per
/// magnet whose metadata arrives from the swarm. A session with a few hundred saved torrents pays
/// it a few hundred times during startup, which is exactly when a client feels slow.
///
/// v1, v2 and hybrid are separated because they parse genuinely different structures, and file
/// count is varied because the v2 file tree is where the per-file work concentrates.
/// </summary>
[MemoryDiagnoser]
public class TorrentFileParseBenchmarks
{
    private byte[] _v1 = null!;
    private byte[] _v2 = null!;
    private byte[] _hybrid = null!;

    /// <summary>Files in the torrent. 500 is an ordinary season pack or game repack.</summary>
    [Params(1, 500)]
    public int FileCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _v1 = Build(TorrentFileVersion.V1);
        _v2 = Build(TorrentFileVersion.V2);
        _hybrid = Build(TorrentFileVersion.Hybrid);
    }

    private byte[] Build(TorrentFileVersion version)
    {
        var builder = new TorrentFileBuilder()
            .WithName("parse-benchmark")
            .WithVersion(version)
            .WithPieceLength(64 * 1024)
            .AddTracker("https://tracker.example/announce");

        // Sizes vary so files are not piece-aligned by accident, which would skip the padding
        // and boundary handling that real torrents exercise.
        var payload = new byte[96 * 1024];
        Random.Shared.NextBytes(payload);

        for (int i = 0; i < FileCount; i++)
        {
            int length = (32 * 1024) + ((i % 5) * 16 * 1024);
            builder = builder.AddFile($"parse-benchmark/dir{i % 16}/file{i}.bin", payload[..length]);
        }

        return builder.Build().RawData.ToArray();
    }

    [Benchmark(Baseline = true, Description = "Parse v1")]
    public object ParseV1() => TorrentFile.Parse(_v1);

    [Benchmark(Description = "Parse v2")]
    public object ParseV2() => TorrentFile.Parse(_v2);

    [Benchmark(Description = "Parse hybrid")]
    public object ParseHybrid() => TorrentFile.Parse(_hybrid);

    /// <summary>
    /// The span overload avoids the caller's defensive copy. Worth tracking separately since
    /// TorrentFile.RawData is a ReadOnlyMemory, so a round-trip through the byte[] overload
    /// forces a ToArray the span overload does not.
    /// </summary>
    [Benchmark(Description = "Parse hybrid (span overload)")]
    public object ParseHybridSpan() => TorrentFile.Parse(_hybrid.AsSpan());
}
