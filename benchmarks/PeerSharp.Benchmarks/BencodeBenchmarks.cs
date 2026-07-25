using BenchmarkDotNet.Attributes;
using PeerSharp.BEncoding;
using System.Text;

namespace PeerSharp.Benchmarks;

/// <summary>
/// Bencoding sits on two hot paths: parsing every tracker response and extension message, and
/// re-encoding the info dictionary whenever an info hash is computed. The README claims
/// "zero-copy Bencoding", so allocation per parse is as interesting as wall time here.
///
/// The fixture is a realistic multi-file torrent rather than a toy document, because parser cost
/// is dominated by the piece-hash BString and the file-list traversal.
/// </summary>
[MemoryDiagnoser]
public class BencodeBenchmarks
{
    private byte[] _smallTorrent = null!;
    private byte[] _largeTorrent = null!;
    private byte[] _trackerResponse = null!;

    // IBNode is internal, so benchmark methods return object rather than leaking an
    // inaccessible type through a public signature. Boxing a reference type is free.
    private IBNode _largeTree = null!;

    [GlobalSetup]
    public void Setup()
    {
        _smallTorrent = BencodeWriter.Write(BuildTorrent(fileCount: 1, pieceCount: 64));
        _largeTorrent = BencodeWriter.Write(BuildTorrent(fileCount: 500, pieceCount: 8000));
        _trackerResponse = BencodeWriter.Write(BuildTrackerResponse(peerCount: 200));
        _largeTree = BencodeParser.Parse(_largeTorrent);
    }

    [Benchmark(Description = "Parse small torrent (1 file, 64 pieces)")]
    public object ParseSmallTorrent() => BencodeParser.Parse(_smallTorrent);

    [Benchmark(Description = "Parse large torrent (500 files, 8000 pieces)")]
    public object ParseLargeTorrent() => BencodeParser.Parse(_largeTorrent);

    [Benchmark(Description = "Parse compact tracker response (200 peers)")]
    public object ParseTrackerResponse() => BencodeParser.Parse(_trackerResponse);

    [Benchmark(Description = "Write large torrent")]
    public byte[] WriteLargeTorrent() => BencodeWriter.Write(_largeTree);

    [Benchmark(Description = "Round-trip large torrent")]
    public byte[] RoundTripLargeTorrent() => BencodeWriter.Write(BencodeParser.Parse(_largeTorrent));

    private static BString Text(string value) => new(Encoding.UTF8.GetBytes(value));

    private static BDict BuildTorrent(int fileCount, int pieceCount)
    {
        var pieces = new byte[pieceCount * 20];
        Random.Shared.NextBytes(pieces);

        var files = new BList();
        for (int i = 0; i < fileCount; i++)
        {
            var path = new BList();
            path.List.Add(Text($"dir{i % 16}"));
            path.List.Add(Text($"file{i}.bin"));

            var file = new BDict();
            file.Dict["length"] = new BNumber(1024L * 1024 * ((i % 32) + 1));
            file.Dict["path"] = path;
            files.List.Add(file);
        }

        var info = new BDict();
        info.Dict["files"] = files;
        info.Dict["name"] = Text("benchmark-torrent");
        info.Dict["piece length"] = new BNumber(256 * 1024);
        info.Dict["pieces"] = new BString(pieces);

        var root = new BDict();
        root.Dict["announce"] = Text("https://tracker.example/announce");
        root.Dict["created by"] = Text("PeerSharp benchmarks");
        root.Dict["creation date"] = new BNumber(1_700_000_000);
        root.Dict["info"] = info;
        return root;
    }

    private static BDict BuildTrackerResponse(int peerCount)
    {
        // BEP 23 compact form: 6 bytes per peer.
        var peers = new byte[peerCount * 6];
        Random.Shared.NextBytes(peers);

        var root = new BDict();
        root.Dict["complete"] = new BNumber(120);
        root.Dict["incomplete"] = new BNumber(45);
        root.Dict["interval"] = new BNumber(1800);
        root.Dict["peers"] = new BString(peers);
        return root;
    }
}
